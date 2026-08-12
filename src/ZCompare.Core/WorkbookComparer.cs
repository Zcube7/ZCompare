using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace ZCompare.Core;

public sealed class WorkbookComparer : IWorkbookComparer
{
    private readonly IWorkbookReader _reader;

    public WorkbookComparer(IWorkbookReader? reader = null)
    {
        _reader = reader ?? new OpenXmlWorkbookReader();
    }

    public async Task<WorkbookCompareResult> CompareAsync(
        string leftPath,
        string rightPath,
        ComparisonOptions? options = null,
        IProgress<ComparisonProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        OpenXmlWorkbookReader.ValidateXlsxPath(leftPath);
        OpenXmlWorkbookReader.ValidateXlsxPath(rightPath);
        options ??= new ComparisonOptions();
        ValidateComparisonOptions(options);
        var stopwatch = Stopwatch.StartNew();
        progress?.Report(new ComparisonProgress(ComparisonStage.Hashing, leftPath, 0, 2, "正在计算左侧 SHA-256…"));
        progress?.Report(new ComparisonProgress(ComparisonStage.Hashing, rightPath, 1, 2, "正在计算右侧 SHA-256…"));
        var leftHashTask = ComputeSha256Async(leftPath, cancellationToken);
        var rightHashTask = ComputeSha256Async(rightPath, cancellationToken);
        await Task.WhenAll(leftHashTask, rightHashTask).ConfigureAwait(false);
        var leftHash = await leftHashTask.ConfigureAwait(false);
        var rightHash = await rightHashTask.ConfigureAwait(false);
        if (string.Equals(leftHash, rightHash, StringComparison.Ordinal) &&
            options.WorksheetPairingMode != WorksheetPairingMode.Manual &&
            options.RowAlignmentMode != RowAlignmentMode.KeyColumns &&
            options.ColumnMappings.Count == 0)
        {
            return await InspectByteIdenticalAsync(
                leftPath,
                rightPath,
                options,
                progress,
                leftHash,
                rightHash,
                stopwatch,
                cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new ComparisonProgress(ComparisonStage.Reading, null, 0, 0, "正在读取工作簿结构…"));
        var accessPair = await CreateAccessPairAsync(leftPath, rightPath, options, cancellationToken).ConfigureAwait(false);
        using var left = accessPair.Left;
        using var right = accessPair.Right;
        var workbookDifferences = new List<Difference>();
        var warnings = new List<string>();
        warnings.AddRange(ActiveWarnings(left.Warnings, options).Select(static warning => "左侧：" + warning));
        warnings.AddRange(ActiveWarnings(right.Warnings, options).Select(static warning => "右侧：" + warning));

        var worksheetResults = new List<WorksheetCompareResult>();
        var worksheetPairs = BuildWorksheetPairs(left.Sheets, right.Sheets, options);
        ValidateColumnMappingsAgainstPairs(options, worksheetPairs);

        for (var index = 0; index < worksheetPairs.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pair = worksheetPairs[index];
            var name = pair.DisplayName;
            progress?.Report(new ComparisonProgress(
                ComparisonStage.Comparing,
                name,
                index,
                worksheetPairs.Count,
                $"正在比较工作表：{name}"));

            if (pair.Left is null || pair.Right is null)
            {
                var kind = pair.Left is not null ? DifferenceKind.WorksheetRemoved : DifferenceKind.WorksheetAdded;
                var missingStatus = pair.Left is not null ? ComparisonStatus.LeftOnly : ComparisonStatus.RightOnly;
                var difference = new Difference(
                    kind,
                    name,
                    null,
                    pair.Left is not null ? "工作表仅存在于左侧。" : "工作表仅存在于右侧。",
                    null,
                    null,
                    pair.Left?.Name,
                    pair.Right?.Name);
                worksheetResults.Add(new WorksheetCompareResult(
                    name,
                    missingStatus,
                    1,
                    [difference],
                    0,
                    0,
                    LeftWorksheetName: pair.Left?.Name,
                    RightWorksheetName: pair.Right?.Name));
                continue;
            }

            var presentLeftSheet = pair.Left;
            var presentRightSheet = pair.Right;
            if (options.CompareLayout && presentLeftSheet.Index != presentRightSheet.Index)
            {
                workbookDifferences.Add(new Difference(
                    DifferenceKind.WorksheetOrder,
                    name,
                    null,
                    "工作表顺序不同。",
                    null,
                    null,
                    (presentLeftSheet.Index + 1).ToString(),
                    (presentRightSheet.Index + 1).ToString()));
            }
            if (options.CompareLayout &&
                !string.Equals(presentLeftSheet.Visibility, presentRightSheet.Visibility, StringComparison.OrdinalIgnoreCase))
            {
                workbookDifferences.Add(new Difference(
                    DifferenceKind.WorksheetVisibility,
                    name,
                    null,
                    "工作表可见性不同。",
                    null,
                    null,
                    presentLeftSheet.Visibility,
                    presentRightSheet.Visibility));
            }

            var worksheetResult = await CompareWorksheetAsync(
                left,
                right,
                presentLeftSheet,
                presentRightSheet,
                options,
                cancellationToken).ConfigureAwait(false);
            var normalizedDifferences = worksheetResult.Differences
                .Select(difference => difference with { WorksheetName = name })
                .ToArray();
            worksheetResults.Add(worksheetResult with
            {
                WorksheetName = name,
                Differences = normalizedDifferences,
                LeftWorksheetName = presentLeftSheet.Name,
                RightWorksheetName = presentRightSheet.Name
            });
        }

        await EnsureSourceHashesUnchangedAsync(
            leftPath,
            rightPath,
            leftHash,
            rightHash,
            cancellationToken).ConfigureAwait(false);

        foreach (var warning in warnings)
        {
            workbookDifferences.Add(new Difference(
                IsUncomparedObjectWarning(warning) ? DifferenceKind.UncomparedObject : DifferenceKind.Warning,
                null,
                null,
                warning,
                null,
                null,
                null,
                null));
        }

        var status = DetermineStatus(workbookDifferences, worksheetResults);
        progress?.Report(new ComparisonProgress(
            ComparisonStage.Completed,
            null,
            worksheetPairs.Count,
            worksheetPairs.Count,
            status == ComparisonStatus.Same ? "语义相同。" : "比较完成。"));
        return new WorkbookCompareResult(
            leftPath,
            rightPath,
            status,
            worksheetResults,
            workbookDifferences,
            warnings,
            false,
            leftHash,
            rightHash,
            stopwatch.Elapsed);
    }

    private static void ValidateComparisonOptions(ComparisonOptions options)
    {
        if (options.WorksheetPairingMode == WorksheetPairingMode.Manual &&
            options.ManualWorksheetPairs.Count == 0)
        {
            throw new ArgumentException("手工工作表配对模式至少需要一组左右工作表。", nameof(options));
        }

        var manualLeft = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manualRight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in options.ManualWorksheetPairs)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.LeftWorksheetName, nameof(options));
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.RightWorksheetName, nameof(options));
            if (!manualLeft.Add(pair.LeftWorksheetName))
            {
                throw new ArgumentException($"左侧工作表“{pair.LeftWorksheetName}”被重复手工配对。", nameof(options));
            }
            if (!manualRight.Add(pair.RightWorksheetName))
            {
                throw new ArgumentException($"右侧工作表“{pair.RightWorksheetName}”被重复手工配对。", nameof(options));
            }
        }

        if (options.RowAlignmentMode == RowAlignmentMode.KeyColumns &&
            options.KeyColumnRules.Count == 0)
        {
            throw new ArgumentException("按关键列对齐至少需要一条工作表关键列规则。", nameof(options));
        }

        var ruleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in options.KeyColumnRules)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rule.WorksheetName, nameof(options));
            if (!ruleNames.Add(rule.WorksheetName))
            {
                throw new ArgumentException($"工作表“{rule.WorksheetName}”存在重复关键列规则。", nameof(options));
            }
            if (rule.HeaderRow < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "关键列表头行必须大于等于 1。");
            }
            if (rule.ColumnIdentifiers is null || rule.ColumnIdentifiers.Count == 0)
            {
                throw new ArgumentException($"工作表“{rule.WorksheetName}”至少需要一个关键列。", nameof(options));
            }

            var columns = new HashSet<int>();
            foreach (var identifier in rule.ColumnIdentifiers)
            {
                if (!TryParseColumnIdentifier(identifier, out var column))
                {
                    throw new ArgumentException(
                        $"关键列“{identifier}”无效；请使用 A 到 XFD 的 Excel 列字母。",
                        nameof(options));
                }
                if (!columns.Add(column))
                {
                    throw new ArgumentException(
                        $"工作表“{rule.WorksheetName}”重复配置了关键列“{identifier}”。",
                        nameof(options));
                }
            }
        }

        var worksheetMappings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in options.ColumnMappings)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(mapping.LeftWorksheetName, nameof(options));
            ArgumentException.ThrowIfNullOrWhiteSpace(mapping.RightWorksheetName, nameof(options));
            if (!worksheetMappings.Add(mapping.LeftWorksheetName + "\0" + mapping.RightWorksheetName))
            {
                throw new ArgumentException(
                    $"工作表对“{mapping.LeftWorksheetName} -> {mapping.RightWorksheetName}”存在重复的列映射配置。",
                    nameof(options));
            }
            if (mapping.ColumnPairs is null || mapping.ColumnPairs.Count == 0)
            {
                throw new ArgumentException(
                    $"工作表对“{mapping.LeftWorksheetName} -> {mapping.RightWorksheetName}”至少需要一组列配对。",
                    nameof(options));
            }

            var leftColumns = new HashSet<int>();
            var rightColumns = new HashSet<int>();
            foreach (var pair in mapping.ColumnPairs)
            {
                if (!TryParseColumnIdentifier(pair.LeftColumnIdentifier, out var leftColumn))
                {
                    throw new ArgumentException(
                        $"左侧列“{pair.LeftColumnIdentifier}”无效；请使用 A 到 XFD 的 Excel 列字母。",
                        nameof(options));
                }
                if (!TryParseColumnIdentifier(pair.RightColumnIdentifier, out var rightColumn))
                {
                    throw new ArgumentException(
                        $"右侧列“{pair.RightColumnIdentifier}”无效；请使用 A 到 XFD 的 Excel 列字母。",
                        nameof(options));
                }
                if (!leftColumns.Add(leftColumn))
                {
                    throw new ArgumentException(
                        $"左侧工作表“{mapping.LeftWorksheetName}”的列“{pair.LeftColumnIdentifier}”被重复映射。",
                        nameof(options));
                }
                if (!rightColumns.Add(rightColumn))
                {
                    throw new ArgumentException(
                        $"右侧工作表“{mapping.RightWorksheetName}”的列“{pair.RightColumnIdentifier}”被重复映射。",
                        nameof(options));
                }
            }
        }
    }

    private static void ValidateColumnMappingsAgainstPairs(
        ComparisonOptions options,
        IReadOnlyList<WorksheetPairPlan> worksheetPairs)
    {
        foreach (var mapping in options.ColumnMappings)
        {
            var paired = worksheetPairs.Any(pair =>
                pair.Left is not null &&
                pair.Right is not null &&
                string.Equals(pair.Left.Name, mapping.LeftWorksheetName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(pair.Right.Name, mapping.RightWorksheetName, StringComparison.OrdinalIgnoreCase));
            if (!paired)
            {
                throw new ArgumentException(
                    $"列映射引用了未配对的工作表：“{mapping.LeftWorksheetName} -> {mapping.RightWorksheetName}”。",
                    nameof(options));
            }
        }
    }

    private static IReadOnlyList<WorksheetPairPlan> BuildWorksheetPairs(
        IReadOnlyList<AccessSheet> leftSheets,
        IReadOnlyList<AccessSheet> rightSheets,
        ComparisonOptions options)
    {
        return options.WorksheetPairingMode switch
        {
            WorksheetPairingMode.Name => BuildNamePairs(leftSheets, rightSheets),
            WorksheetPairingMode.Index => BuildIndexPairs(leftSheets, rightSheets),
            WorksheetPairingMode.Manual => BuildManualPairs(
                leftSheets,
                rightSheets,
                options.ManualWorksheetPairs),
            _ => throw new ArgumentOutOfRangeException(nameof(options.WorksheetPairingMode))
        };
    }

    private static IReadOnlyList<WorksheetPairPlan> BuildNamePairs(
        IReadOnlyList<AccessSheet> leftSheets,
        IReadOnlyList<AccessSheet> rightSheets)
    {
        var rightByName = rightSheets.ToDictionary(static sheet => sheet.Name, StringComparer.OrdinalIgnoreCase);
        var pairedRight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<WorksheetPairPlan>(leftSheets.Count + rightSheets.Count);
        foreach (var left in leftSheets)
        {
            rightByName.TryGetValue(left.Name, out var right);
            if (right is not null)
            {
                pairedRight.Add(right.Name);
            }
            result.Add(CreateWorksheetPairPlan(left, right));
        }
        foreach (var right in rightSheets)
        {
            if (!pairedRight.Contains(right.Name))
            {
                result.Add(CreateWorksheetPairPlan(null, right));
            }
        }
        return result;
    }

    private static IReadOnlyList<WorksheetPairPlan> BuildIndexPairs(
        IReadOnlyList<AccessSheet> leftSheets,
        IReadOnlyList<AccessSheet> rightSheets)
    {
        var count = Math.Max(leftSheets.Count, rightSheets.Count);
        var result = new List<WorksheetPairPlan>(count);
        for (var index = 0; index < count; index++)
        {
            result.Add(CreateWorksheetPairPlan(
                index < leftSheets.Count ? leftSheets[index] : null,
                index < rightSheets.Count ? rightSheets[index] : null));
        }
        return result;
    }

    private static IReadOnlyList<WorksheetPairPlan> BuildManualPairs(
        IReadOnlyList<AccessSheet> leftSheets,
        IReadOnlyList<AccessSheet> rightSheets,
        IReadOnlyList<WorksheetPair> configuredPairs)
    {
        var leftByName = leftSheets.ToDictionary(static sheet => sheet.Name, StringComparer.OrdinalIgnoreCase);
        var rightByName = rightSheets.ToDictionary(static sheet => sheet.Name, StringComparer.OrdinalIgnoreCase);
        var pairedLeft = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pairedRight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<WorksheetPairPlan>(leftSheets.Count + rightSheets.Count);
        foreach (var configured in configuredPairs)
        {
            if (!leftByName.TryGetValue(configured.LeftWorksheetName, out var left))
            {
                throw new ArgumentException($"左侧不存在手工配对的工作表“{configured.LeftWorksheetName}”。");
            }
            if (!rightByName.TryGetValue(configured.RightWorksheetName, out var right))
            {
                throw new ArgumentException($"右侧不存在手工配对的工作表“{configured.RightWorksheetName}”。");
            }
            pairedLeft.Add(left.Name);
            pairedRight.Add(right.Name);
            result.Add(CreateWorksheetPairPlan(left, right));
        }
        foreach (var left in leftSheets)
        {
            if (!pairedLeft.Contains(left.Name))
            {
                result.Add(CreateWorksheetPairPlan(left, null));
            }
        }
        foreach (var right in rightSheets)
        {
            if (!pairedRight.Contains(right.Name))
            {
                result.Add(CreateWorksheetPairPlan(null, right));
            }
        }
        return result;
    }

    private static WorksheetPairPlan CreateWorksheetPairPlan(AccessSheet? left, AccessSheet? right)
    {
        var displayName = left is null
            ? right!.Name
            : right is null || string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)
                ? left.Name
                : left.Name + " ↔ " + right.Name;
        return new WorksheetPairPlan(left, right, displayName);
    }

    private async Task<WorkbookCompareResult> InspectByteIdenticalAsync(
        string leftPath,
        string rightPath,
        ComparisonOptions options,
        IProgress<ComparisonProgress>? progress,
        string leftHash,
        string rightHash,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        progress?.Report(new ComparisonProgress(
            ComparisonStage.Reading,
            null,
            0,
            0,
            "文件字节相同，正在检查公式缓存和安全警告…"));
        using var workbook = await CreateByteIdenticalProbeAccessAsync(
            leftPath,
            options,
            cancellationToken).ConfigureAwait(false);
        var workbookDifferences = new List<Difference>();
        var warnings = ActiveWarnings(workbook.Warnings, options)
            .Select(static warning => "两侧：" + warning)
            .ToList();
        var worksheetResults = new List<WorksheetCompareResult>(workbook.Sheets.Count);

        for (var index = 0; index < workbook.Sheets.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sheet = workbook.Sheets[index];
            progress?.Report(new ComparisonProgress(
                ComparisonStage.Comparing,
                sheet.Name,
                index,
                workbook.Sheets.Count,
                $"正在检查工作表：{sheet.Name}"));
            var differences = new List<Difference>();
            var cellCount = 0;
            var rows = new HashSet<int>();
            var rowsWithWarnings = new HashSet<int>();
            await foreach (var cell in workbook.ReadCellsAsync(sheet, cancellationToken).ConfigureAwait(false))
            {
                cellCount++;
                CellReferenceUtility.TryParse(cell.CellReference, out _, out var row);
                if (row > 0)
                {
                    rows.Add(row);
                }
                var hasFormula = !string.IsNullOrEmpty(cell.Formula) || cell.FormulaKind != FormulaKind.None;
                if (HasUnusableFormulaCache(cell, hasFormula))
                {
                    if (row > 0)
                    {
                        rowsWithWarnings.Add(row);
                    }
                    AddDifference(
                        differences,
                        DifferenceKind.Warning,
                        sheet.Name,
                        cell.CellReference,
                        "公式缓存结果缺失，无法判定保存结果是否相同。",
                        cell,
                        cell,
                        null,
                        null);
                }
            }

            var rowAlignments = rows
                .Order()
                .Select(row => new RowAlignment(
                    row,
                    row,
                    row,
                    options.RowAlignmentMode == RowAlignmentMode.StrictRowNumber
                        ? RowAlignmentStatus.NotApplied
                        : rowsWithWarnings.Contains(row)
                            ? RowAlignmentStatus.Modified
                            : RowAlignmentStatus.Matched))
                .ToArray();

            worksheetResults.Add(new WorksheetCompareResult(
                sheet.Name,
                DetermineStatus(differences),
                differences.Count,
                differences,
                cellCount,
                cellCount,
                rowAlignments,
                rows.Count,
                rows.Count));
        }

        await EnsureSourceHashesUnchangedAsync(
            leftPath,
            rightPath,
            leftHash,
            rightHash,
            cancellationToken).ConfigureAwait(false);

        foreach (var warning in warnings)
        {
            workbookDifferences.Add(new Difference(
                IsUncomparedObjectWarning(warning) ? DifferenceKind.UncomparedObject : DifferenceKind.Warning,
                null,
                null,
                warning,
                null,
                null,
                null,
                null));
        }

        var status = DetermineStatus(workbookDifferences, worksheetResults);
        progress?.Report(new ComparisonProgress(
            ComparisonStage.Completed,
            null,
            workbook.Sheets.Count,
            workbook.Sheets.Count,
            status == ComparisonStatus.Same ? "文件字节完全相同。" : "文件字节相同，但存在安全警告。"));
        return new WorkbookCompareResult(
            leftPath,
            rightPath,
            status,
            worksheetResults,
            workbookDifferences,
            warnings,
            true,
            leftHash,
            rightHash,
            stopwatch.Elapsed);
    }

    private async Task<WorksheetCompareResult> CompareWorksheetAsync(
        IWorkbookAccess leftWorkbook,
        IWorkbookAccess rightWorkbook,
        AccessSheet leftSheet,
        AccessSheet rightSheet,
        ComparisonOptions options,
        CancellationToken cancellationToken)
    {
        var differences = new List<Difference>();
        var columnMapping = ResolveColumnMapping(leftSheet.Name, rightSheet.Name, options);
        if (options.CompareLayout)
        {
            var leftLayout = leftWorkbook.GetLayout(leftSheet);
            var rightLayout = rightWorkbook.GetLayout(rightSheet);
            CompareLayout(leftSheet.Name, leftLayout, rightLayout, differences);
        }

        if (options.RowAlignmentMode == RowAlignmentMode.KeyColumns)
        {
            return await CompareWorksheetByKeyColumnsAsync(
                leftWorkbook,
                rightWorkbook,
                leftSheet,
                rightSheet,
                options,
                columnMapping,
                differences,
                cancellationToken).ConfigureAwait(false);
        }

        if (options.RowAlignmentMode == RowAlignmentMode.Conservative)
        {
            return await CompareWorksheetConservativelyAsync(
                leftWorkbook,
                rightWorkbook,
                leftSheet,
                rightSheet,
                options,
                columnMapping,
                differences,
                cancellationToken).ConfigureAwait(false);
        }

        if (!columnMapping.IsEmpty)
        {
            return await CompareWorksheetStrictWithColumnMappingAsync(
                leftWorkbook,
                rightWorkbook,
                leftSheet,
                rightSheet,
                options,
                columnMapping,
                differences,
                cancellationToken).ConfigureAwait(false);
        }

        var leftCount = 0;
        var rightCount = 0;
        var leftRows = new HashSet<int>();
        var rightRows = new HashSet<int>();
        await using var leftEnumerator = leftWorkbook.ReadCellsAsync(leftSheet, cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using var rightEnumerator = rightWorkbook.ReadCellsAsync(rightSheet, cancellationToken).GetAsyncEnumerator(cancellationToken);
        var hasLeft = await leftEnumerator.MoveNextAsync().ConfigureAwait(false);
        var hasRight = await rightEnumerator.MoveNextAsync().ConfigureAwait(false);
        while (hasLeft || hasRight)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CellSnapshot? leftCell = hasLeft ? leftEnumerator.Current : null;
            CellSnapshot? rightCell = hasRight ? rightEnumerator.Current : null;
            var comparison = leftCell is null
                ? 1
                : rightCell is null
                    ? -1
                    : CellReferenceUtility.Compare(leftCell.CellReference, rightCell.CellReference);

            if (comparison < 0)
            {
                leftCount++;
                AddRow(leftRows, leftCell!.CellReference);
                CompareCell(leftSheet.Name, leftCell, null, leftWorkbook.DefaultFormat, rightWorkbook.DefaultFormat, options, differences);
                hasLeft = await leftEnumerator.MoveNextAsync().ConfigureAwait(false);
            }
            else if (comparison > 0)
            {
                rightCount++;
                AddRow(rightRows, rightCell!.CellReference);
                CompareCell(leftSheet.Name, null, rightCell, leftWorkbook.DefaultFormat, rightWorkbook.DefaultFormat, options, differences);
                hasRight = await rightEnumerator.MoveNextAsync().ConfigureAwait(false);
            }
            else
            {
                leftCount++;
                rightCount++;
                AddRow(leftRows, leftCell!.CellReference);
                AddRow(rightRows, rightCell!.CellReference);
                CompareCell(leftSheet.Name, leftCell, rightCell, leftWorkbook.DefaultFormat, rightWorkbook.DefaultFormat, options, differences);
                hasLeft = await leftEnumerator.MoveNextAsync().ConfigureAwait(false);
                hasRight = await rightEnumerator.MoveNextAsync().ConfigureAwait(false);
            }
        }

        var status = DetermineStatus(differences);
        var rowAlignments = leftRows
            .Concat(rightRows)
            .Distinct()
            .Order()
            .Select(row => new RowAlignment(
                row,
                leftRows.Contains(row) ? row : null,
                rightRows.Contains(row) ? row : null,
                RowAlignmentStatus.NotApplied))
            .ToArray();
        return new WorksheetCompareResult(
            leftSheet.Name,
            status,
            differences.Count,
            differences,
            leftCount,
            rightCount,
            rowAlignments,
            leftRows.Count,
            rightRows.Count);

        static void AddRow(HashSet<int> rows, string reference)
        {
            if (CellReferenceUtility.TryParse(reference, out _, out var row))
            {
                rows.Add(row);
            }
        }
    }

    private static async Task<WorksheetCompareResult> CompareWorksheetStrictWithColumnMappingAsync(
        IWorkbookAccess leftWorkbook,
        IWorkbookAccess rightWorkbook,
        AccessSheet leftSheet,
        AccessSheet rightSheet,
        ComparisonOptions options,
        ResolvedColumnMapping columnMapping,
        List<Difference> differences,
        CancellationToken cancellationToken)
    {
        var leftRead = await ReadRowsAsync(
            leftWorkbook,
            leftSheet,
            options,
            cancellationToken).ConfigureAwait(false);
        var rightRead = await ReadRowsAsync(
            rightWorkbook,
            rightSheet,
            options,
            cancellationToken).ConfigureAwait(false);
        var alignedRows = new List<AlignedWorksheetRow>(Math.Max(leftRead.Rows.Count, rightRead.Rows.Count));
        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < leftRead.Rows.Count || rightIndex < rightRead.Rows.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leftRow = leftIndex < leftRead.Rows.Count ? leftRead.Rows[leftIndex] : null;
            var rightRow = rightIndex < rightRead.Rows.Count ? rightRead.Rows[rightIndex] : null;
            if (rightRow is null || (leftRow is not null && leftRow.RowNumber < rightRow.RowNumber))
            {
                alignedRows.Add(new AlignedWorksheetRow(leftRow, null, RowAlignmentStatus.NotApplied, null));
                leftIndex++;
            }
            else if (leftRow is null || rightRow.RowNumber < leftRow.RowNumber)
            {
                alignedRows.Add(new AlignedWorksheetRow(null, rightRow, RowAlignmentStatus.NotApplied, null));
                rightIndex++;
            }
            else
            {
                alignedRows.Add(new AlignedWorksheetRow(leftRow, rightRow, RowAlignmentStatus.NotApplied, null));
                leftIndex++;
                rightIndex++;
            }
        }

        return BuildAlignedWorksheetResult(
            leftWorkbook,
            rightWorkbook,
            leftSheet,
            options,
            differences,
            leftRead,
            rightRead,
            alignedRows,
            columnMapping,
            cancellationToken);
    }

    private static async Task<WorksheetCompareResult> CompareWorksheetConservativelyAsync(
        IWorkbookAccess leftWorkbook,
        IWorkbookAccess rightWorkbook,
        AccessSheet leftSheet,
        AccessSheet rightSheet,
        ComparisonOptions options,
        ResolvedColumnMapping columnMapping,
        List<Difference> differences,
        CancellationToken cancellationToken)
    {
        var leftRead = await ReadRowsAsync(
            leftWorkbook,
            leftSheet,
            options,
            cancellationToken).ConfigureAwait(false);
        var rightRead = await ReadRowsAsync(
            rightWorkbook,
            rightSheet,
            options,
            cancellationToken).ConfigureAwait(false);
        var leftRows = ApplyColumnMapping(leftRead.Rows, columnMapping, isLeft: true, options.CaseSensitive);
        var rightRows = ApplyColumnMapping(rightRead.Rows, columnMapping, isLeft: false, options.CaseSensitive);
        var alignedRows = AlignRows(
            leftRows,
            rightRows,
            (left, right) => RowsIdentityEquivalent(left, right, options.CaseSensitive),
            static _ => false,
            changedRowsAreDeleteInsert: false,
            cancellationToken);
        return BuildAlignedWorksheetResult(
            leftWorkbook,
            rightWorkbook,
            leftSheet,
            options,
            differences,
            leftRead,
            rightRead,
            alignedRows,
            columnMapping,
            cancellationToken);
    }

    private static async Task<WorksheetCompareResult> CompareWorksheetByKeyColumnsAsync(
        IWorkbookAccess leftWorkbook,
        IWorkbookAccess rightWorkbook,
        AccessSheet leftSheet,
        AccessSheet rightSheet,
        ComparisonOptions options,
        ResolvedColumnMapping columnMapping,
        List<Difference> differences,
        CancellationToken cancellationToken)
    {
        var (leftRule, rightRule) = ResolveKeyColumnRules(leftSheet.Name, rightSheet.Name, options);
        columnMapping = MergeKeyColumnPairs(columnMapping, leftRule, rightRule, leftSheet.Name, rightSheet.Name);
        var leftRead = await ReadRowsAsync(
            leftWorkbook,
            leftSheet,
            options,
            cancellationToken).ConfigureAwait(false);
        var rightRead = await ReadRowsAsync(
            rightWorkbook,
            rightSheet,
            options,
            cancellationToken).ConfigureAwait(false);

        var leftRows = ApplyColumnMapping(leftRead.Rows, columnMapping, isLeft: true, options.CaseSensitive);
        var rightRows = ApplyColumnMapping(rightRead.Rows, columnMapping, isLeft: false, options.CaseSensitive);

        var leftHeaderRows = leftRows.Where(row => row.RowNumber <= leftRule.HeaderRow).ToArray();
        var rightHeaderRows = rightRows.Where(row => row.RowNumber <= rightRule.HeaderRow).ToArray();
        var alignedRows = AlignRows(
            leftHeaderRows,
            rightHeaderRows,
            (left, right) => RowsIdentityEquivalent(left, right, options.CaseSensitive),
            static _ => false,
            changedRowsAreDeleteInsert: false,
            cancellationToken);

        var leftDataRows = ApplyKeyColumns(
            leftRows.Where(row => row.RowNumber > leftRule.HeaderRow),
            leftRule,
            options.CaseSensitive);
        var rightDataRows = ApplyKeyColumns(
            rightRows.Where(row => row.RowNumber > rightRule.HeaderRow),
            rightRule,
            options.CaseSensitive);
        MarkDuplicateKeysAmbiguous(leftDataRows, rightDataRows);
        alignedRows.AddRange(AlignRows(
            leftDataRows,
            rightDataRows,
            static (left, right) =>
                !left.KeyAmbiguous &&
                !right.KeyAmbiguous &&
                left.AlignmentKey is not null &&
                string.Equals(left.AlignmentKey, right.AlignmentKey, StringComparison.Ordinal),
            static row => row.KeyAmbiguous,
            changedRowsAreDeleteInsert: true,
            cancellationToken));

        return BuildAlignedWorksheetResult(
            leftWorkbook,
            rightWorkbook,
            leftSheet,
            options,
            differences,
            leftRead,
            rightRead,
            alignedRows,
            columnMapping,
            cancellationToken);
    }

    private static (KeyColumnSpec Left, KeyColumnSpec Right) ResolveKeyColumnRules(
        string leftWorksheetName,
        string rightWorksheetName,
        ComparisonOptions options)
    {
        var rules = options.KeyColumnRules.ToDictionary(
            static rule => rule.WorksheetName,
            StringComparer.OrdinalIgnoreCase);
        if (!rules.TryGetValue(leftWorksheetName, out var leftRule))
        {
            throw new ArgumentException($"左侧工作表“{leftWorksheetName}”缺少关键列规则。", nameof(options));
        }
        var rightRule = string.Equals(leftWorksheetName, rightWorksheetName, StringComparison.OrdinalIgnoreCase)
            ? leftRule
            : rules.TryGetValue(rightWorksheetName, out var configuredRight)
                ? configuredRight
                : throw new ArgumentException($"右侧工作表“{rightWorksheetName}”缺少关键列规则。", nameof(options));
        if (leftRule.ColumnIdentifiers.Count != rightRule.ColumnIdentifiers.Count)
        {
            throw new ArgumentException(
                $"配对工作表“{leftWorksheetName}”与“{rightWorksheetName}”的关键列数量不同。",
                nameof(options));
        }
        return (CreateKeyColumnSpec(leftRule), CreateKeyColumnSpec(rightRule));
    }

    private static ResolvedColumnMapping ResolveColumnMapping(
        string leftWorksheetName,
        string rightWorksheetName,
        ComparisonOptions options)
    {
        var configured = options.ColumnMappings.FirstOrDefault(mapping =>
            string.Equals(mapping.LeftWorksheetName, leftWorksheetName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(mapping.RightWorksheetName, rightWorksheetName, StringComparison.OrdinalIgnoreCase));
        if (configured is null)
        {
            return ResolvedColumnMapping.Empty;
        }

        var pairs = new List<ResolvedColumnPair>(configured.ColumnPairs.Count);
        foreach (var pair in configured.ColumnPairs)
        {
            if (!TryParseColumnIdentifier(pair.LeftColumnIdentifier, out var leftColumn) ||
                !TryParseColumnIdentifier(pair.RightColumnIdentifier, out var rightColumn))
            {
                throw new ArgumentException("列映射包含无效的 Excel 列标识。", nameof(options));
            }
            pairs.Add(new ResolvedColumnPair(leftColumn, rightColumn));
        }
        pairs.Sort(static (left, right) => left.LeftColumn.CompareTo(right.LeftColumn));
        return ResolvedColumnMapping.Create(pairs);
    }

    private static KeyColumnSpec CreateKeyColumnSpec(KeyColumnRule rule)
    {
        var columns = new int[rule.ColumnIdentifiers.Count];
        for (var index = 0; index < columns.Length; index++)
        {
            if (!TryParseColumnIdentifier(rule.ColumnIdentifiers[index], out columns[index]))
            {
                throw new ArgumentException(
                    $"关键列“{rule.ColumnIdentifiers[index]}”无效；请使用 A 到 XFD 的 Excel 列字母。",
                    nameof(rule));
            }
        }
        return new KeyColumnSpec(rule.HeaderRow, columns);
    }

    private static ResolvedColumnMapping MergeKeyColumnPairs(
        ResolvedColumnMapping configured,
        KeyColumnSpec leftRule,
        KeyColumnSpec rightRule,
        string leftWorksheetName,
        string rightWorksheetName)
    {
        var pairs = configured.Pairs.ToList();
        for (var index = 0; index < leftRule.Columns.Count; index++)
        {
            var leftColumn = leftRule.Columns[index];
            var rightColumn = rightRule.Columns[index];
            if (configured.LeftToRight.TryGetValue(leftColumn, out var mappedRight))
            {
                if (mappedRight != rightColumn)
                {
                    throw new ArgumentException(
                        $"工作表对“{leftWorksheetName} -> {rightWorksheetName}”的显式列映射与关键列配对 {CellReferenceUtility.ToColumnName(leftColumn)} -> {CellReferenceUtility.ToColumnName(rightColumn)} 冲突。",
                        "options");
                }
                continue;
            }
            if (configured.RightToLeft.TryGetValue(rightColumn, out var mappedLeft))
            {
                throw new ArgumentException(
                    $"工作表对“{leftWorksheetName} -> {rightWorksheetName}”的右侧关键列 {CellReferenceUtility.ToColumnName(rightColumn)} 已由左侧列 {CellReferenceUtility.ToColumnName(mappedLeft)} 映射。",
                    "options");
            }
            pairs.Add(new ResolvedColumnPair(leftColumn, rightColumn));
        }
        pairs.Sort(static (left, right) => left.LeftColumn.CompareTo(right.LeftColumn));
        return ResolvedColumnMapping.Create(pairs);
    }

    private static List<WorksheetRow> ApplyKeyColumns(
        IEnumerable<WorksheetRow> source,
        KeyColumnSpec rule,
        bool caseSensitive)
    {
        var result = new List<WorksheetRow>();
        foreach (var row in source)
        {
            var key = BuildCompositeKey(row, rule.Columns, caseSensitive);
            result.Add(row with
            {
                AlignmentKey = key,
                KeyAmbiguous = key is null,
                Signature = key is null
                    ? 0UL
                    : unchecked((uint)StringComparer.Ordinal.GetHashCode(key))
            });
        }
        return result;
    }

    private static string? BuildCompositeKey(
        WorksheetRow row,
        IReadOnlyList<int> columns,
        bool caseSensitive)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var column in columns)
        {
            CellSnapshot? keyCell = null;
            foreach (var cell in row.Cells)
            {
                if (!CellReferenceUtility.TryParse(cell.CellReference, out var cellColumn, out _))
                {
                    continue;
                }
                if (cellColumn == column)
                {
                    keyCell = cell;
                    break;
                }
                if (cellColumn > column)
                {
                    break;
                }
            }
            if (keyCell is null)
            {
                return null;
            }

            var identity = GetRowIdentityValue(keyCell);
            if (identity.Kind == CellValueKind.Blank ||
                identity.Value is null ||
                (identity.Kind == CellValueKind.Text && identity.Value.Length == 0))
            {
                return null;
            }
            var value = identity.Kind == CellValueKind.Text && !caseSensitive
                ? identity.Value.ToUpperInvariant()
                : identity.Value;
            builder.Append((int)identity.Kind)
                .Append(':')
                .Append(value.Length)
                .Append(':')
                .Append(value)
                .Append(';');
        }
        return builder.ToString();
    }

    private static void MarkDuplicateKeysAmbiguous(
        List<WorksheetRow> leftRows,
        List<WorksheetRow> rightRows)
    {
        var leftCounts = CountKeys(leftRows);
        var rightCounts = CountKeys(rightRows);
        Mark(leftRows);
        Mark(rightRows);
        return;

        static Dictionary<string, int> CountKeys(IReadOnlyList<WorksheetRow> rows)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (row.AlignmentKey is { } key)
                {
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
            }
            return counts;
        }

        void Mark(List<WorksheetRow> rows)
        {
            for (var index = 0; index < rows.Count; index++)
            {
                var row = rows[index];
                if (row.AlignmentKey is not { } key ||
                    leftCounts.GetValueOrDefault(key) > 1 ||
                    rightCounts.GetValueOrDefault(key) > 1)
                {
                    rows[index] = row with { KeyAmbiguous = true };
                }
            }
        }
    }

    private static WorksheetCompareResult BuildAlignedWorksheetResult(
        IWorkbookAccess leftWorkbook,
        IWorkbookAccess rightWorkbook,
        AccessSheet leftSheet,
        ComparisonOptions options,
        List<Difference> differences,
        RowReadResult leftRead,
        RowReadResult rightRead,
        IReadOnlyList<AlignedWorksheetRow> alignedRows,
        ResolvedColumnMapping columnMapping,
        CancellationToken cancellationToken)
    {
        var rowAlignments = new List<RowAlignment>(alignedRows.Count);
        var previousDisplayRow = 0;

        for (var index = 0; index < alignedRows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var aligned = alignedRows[index];
            var physicalCandidate = Math.Min(
                aligned.Left?.RowNumber ?? int.MaxValue,
                aligned.Right?.RowNumber ?? int.MaxValue);
            var displayRow = Math.Max(
                previousDisplayRow + 1,
                physicalCandidate == int.MaxValue ? previousDisplayRow + 1 : physicalCandidate);
            previousDisplayRow = displayRow;
            var effectiveStatus = aligned.Status;
            var differencesBefore = differences.Count;

            switch (aligned.Status)
            {
                case RowAlignmentStatus.Matched:
                    CompareAlignedRowCells(
                        leftSheet.Name,
                        displayRow,
                        aligned.Left,
                        aligned.Right,
                        leftWorkbook.DefaultFormat,
                        rightWorkbook.DefaultFormat,
                        options,
                        differences,
                        columnMapping);
                    if (differences.Count > differencesBefore)
                    {
                        effectiveStatus = RowAlignmentStatus.Modified;
                    }
                    break;
                case RowAlignmentStatus.Inserted:
                    if (!HasIdentityValues(aligned.Right!))
                    {
                        CompareAlignedRowCells(
                            leftSheet.Name,
                            displayRow,
                            null,
                            aligned.Right,
                            leftWorkbook.DefaultFormat,
                            rightWorkbook.DefaultFormat,
                            options,
                            differences,
                            columnMapping);
                        effectiveStatus = differences.Count > differencesBefore
                            ? RowAlignmentStatus.Modified
                            : RowAlignmentStatus.Matched;
                        break;
                    }
                    if (aligned.Message is { } insertedWarning)
                    {
                        AddRowDifference(
                            differences,
                            DifferenceKind.RowAlignmentWarning,
                            leftSheet.Name,
                            displayRow,
                            aligned.Left,
                            aligned.Right,
                            insertedWarning,
                            aligned.Left?.RowNumber.ToString(),
                            aligned.Right?.RowNumber.ToString(),
                            columnMapping);
                    }
                    AddRowDifference(
                        differences,
                        DifferenceKind.RowInserted,
                        leftSheet.Name,
                        displayRow,
                        aligned.Left,
                        aligned.Right,
                        $"右侧插入了第 {aligned.Right!.RowNumber} 行。",
                        null,
                        aligned.Right.RowNumber.ToString(),
                        columnMapping);
                    break;
                case RowAlignmentStatus.Deleted:
                    if (!HasIdentityValues(aligned.Left!))
                    {
                        CompareAlignedRowCells(
                            leftSheet.Name,
                            displayRow,
                            aligned.Left,
                            null,
                            leftWorkbook.DefaultFormat,
                            rightWorkbook.DefaultFormat,
                            options,
                            differences,
                            columnMapping);
                        effectiveStatus = differences.Count > differencesBefore
                            ? RowAlignmentStatus.Modified
                            : RowAlignmentStatus.Matched;
                        break;
                    }
                    if (aligned.Message is { } deletedWarning)
                    {
                        AddRowDifference(
                            differences,
                            DifferenceKind.RowAlignmentWarning,
                            leftSheet.Name,
                            displayRow,
                            aligned.Left,
                            aligned.Right,
                            deletedWarning,
                            aligned.Left?.RowNumber.ToString(),
                            aligned.Right?.RowNumber.ToString(),
                            columnMapping);
                    }
                    AddRowDifference(
                        differences,
                        DifferenceKind.RowDeleted,
                        leftSheet.Name,
                        displayRow,
                        aligned.Left,
                        aligned.Right,
                        $"右侧缺少左侧第 {aligned.Left!.RowNumber} 行。",
                        aligned.Left.RowNumber.ToString(),
                        null,
                        columnMapping);
                    break;
                case RowAlignmentStatus.Ambiguous:
                    AddRowDifference(
                        differences,
                        DifferenceKind.RowAlignmentWarning,
                        leftSheet.Name,
                        displayRow,
                        aligned.Left,
                        aligned.Right,
                        aligned.Message ?? "该区间缺少唯一的精确行锚点，已按局部位置保守展示。",
                        aligned.Left?.RowNumber.ToString(),
                        aligned.Right?.RowNumber.ToString(),
                        columnMapping);
                    CompareAlignedRowCells(
                        leftSheet.Name,
                        displayRow,
                        aligned.Left,
                        aligned.Right,
                        leftWorkbook.DefaultFormat,
                        rightWorkbook.DefaultFormat,
                        options,
                        differences,
                        columnMapping);
                    break;
                default:
                    CompareAlignedRowCells(
                        leftSheet.Name,
                        displayRow,
                        aligned.Left,
                        aligned.Right,
                        leftWorkbook.DefaultFormat,
                        rightWorkbook.DefaultFormat,
                        options,
                        differences,
                        columnMapping);
                    break;
            }

            rowAlignments.Add(new RowAlignment(
                displayRow,
                aligned.Left?.RowNumber,
                aligned.Right?.RowNumber,
                effectiveStatus,
                aligned.Message));
        }

        var status = DetermineStatus(differences);
        return new WorksheetCompareResult(
            leftSheet.Name,
            status,
            differences.Count,
            differences,
            leftRead.CellCount,
            rightRead.CellCount,
            rowAlignments,
            leftRead.Rows.Count,
            rightRead.Rows.Count,
            AppliedColumnPairs: columnMapping.PublicPairs);
    }

    private static async Task<RowReadResult> ReadRowsAsync(
        IWorkbookAccess workbook,
        AccessSheet sheet,
        ComparisonOptions options,
        CancellationToken cancellationToken)
    {
        var rows = new List<WorksheetRow>();
        List<CellSnapshot>? currentCells = null;
        var currentRow = -1;
        var cellCount = 0;

        await foreach (var cell in workbook.ReadCellsAsync(sheet, cancellationToken).ConfigureAwait(false))
        {
            cellCount++;
            if (!CellReferenceUtility.TryParse(cell.CellReference, out _, out var row))
            {
                throw new InvalidDataException(
                    $"工作表“{sheet.Name}”包含无效单元格地址“{cell.CellReference}”，无法执行行对齐。");
            }
            if (row != currentRow)
            {
                if (currentCells is { Count: > 0 })
                {
                    rows.Add(CreateWorksheetRow(currentRow, currentCells, options));
                }
                if (row < currentRow)
                {
                    throw new InvalidDataException(
                        $"工作表“{sheet.Name}”中的单元格未按行排序，无法执行行对齐。");
                }
                currentRow = row;
                currentCells = [];
            }
            currentCells!.Add(cell);
        }

        if (currentCells is { Count: > 0 })
        {
            rows.Add(CreateWorksheetRow(currentRow, currentCells, options));
        }
        return new RowReadResult(rows, cellCount);
    }

    private static WorksheetRow CreateWorksheetRow(
        int rowNumber,
        IReadOnlyList<CellSnapshot> cells,
        ComparisonOptions options) =>
        new(rowNumber, cells, ComputeRowSignature(cells, options.CaseSensitive));

    private static IReadOnlyList<WorksheetRow> ApplyColumnMapping(
        IReadOnlyList<WorksheetRow> rows,
        ResolvedColumnMapping columnMapping,
        bool isLeft,
        bool caseSensitive)
    {
        if (columnMapping.IsEmpty)
        {
            return rows;
        }

        var result = new WorksheetRow[rows.Count];
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var identities = new List<RowIdentityCell>(row.Cells.Count);
            foreach (var cell in row.Cells)
            {
                if (!CellReferenceUtility.TryParse(cell.CellReference, out var actualColumn, out _) ||
                    !columnMapping.TryGetDisplayColumn(actualColumn, isLeft, out var displayColumn))
                {
                    continue;
                }
                var identity = GetRowIdentityValue(cell);
                if (identity.Kind == CellValueKind.Blank && identity.Value is null)
                {
                    continue;
                }
                identities.Add(new RowIdentityCell(displayColumn, identity.Kind, identity.Value));
            }
            identities.Sort(static (left, right) => left.DisplayColumn.CompareTo(right.DisplayColumn));
            result[rowIndex] = row with
            {
                Signature = ComputeMappedRowSignature(identities, caseSensitive),
                MappedIdentityCells = identities
            };
        }
        return result;
    }

    private static ulong ComputeMappedRowSignature(
        IReadOnlyList<RowIdentityCell> identities,
        bool caseSensitive)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var identity in identities)
        {
            Mix(identity.DisplayColumn);
            Mix((int)identity.Kind);
            Mix(identity.Value is null
                ? int.MinValue
                : (identity.Kind == CellValueKind.Text && !caseSensitive
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal).GetHashCode(identity.Value));
        }
        return hash;

        void Mix(int value)
        {
            hash ^= unchecked((uint)value);
            hash *= prime;
        }
    }

    private static ulong ComputeRowSignature(
        IReadOnlyList<CellSnapshot> cells,
        bool caseSensitive)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var cell in cells)
        {
            var identity = GetRowIdentityValue(cell);
            if (identity.Kind == CellValueKind.Blank && identity.Value is null)
            {
                continue;
            }
            CellReferenceUtility.TryParse(cell.CellReference, out var column, out _);
            Mix(column);
            Mix((int)identity.Kind);
            MixString(identity.Value, identity.Kind == CellValueKind.Text && !caseSensitive);
        }
        return hash;

        void Mix(int value)
        {
            hash ^= unchecked((uint)value);
            hash *= prime;
        }

        void MixString(string? value, bool ignoreCase = false)
        {
            Mix(value is null
                ? int.MinValue
                : (ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).GetHashCode(value));
        }
    }

    private static bool RowsIdentityEquivalent(
        WorksheetRow left,
        WorksheetRow right,
        bool caseSensitive)
    {
        if (left.MappedIdentityCells is not null || right.MappedIdentityCells is not null)
        {
            if (left.MappedIdentityCells is null || right.MappedIdentityCells is null ||
                left.MappedIdentityCells.Count != right.MappedIdentityCells.Count)
            {
                return false;
            }
            for (var index = 0; index < left.MappedIdentityCells.Count; index++)
            {
                var leftIdentity = left.MappedIdentityCells[index];
                var rightIdentity = right.MappedIdentityCells[index];
                if (leftIdentity.DisplayColumn != rightIdentity.DisplayColumn ||
                    leftIdentity.Kind != rightIdentity.Kind)
                {
                    return false;
                }
                var comparison = leftIdentity.Kind == CellValueKind.Text && !caseSensitive
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (!string.Equals(leftIdentity.Value, rightIdentity.Value, comparison))
                {
                    return false;
                }
            }
            return true;
        }

        var leftIndex = 0;
        var rightIndex = 0;
        while (true)
        {
            var leftCell = NextIdentityCell(left.Cells, ref leftIndex, out var leftIdentity);
            var rightCell = NextIdentityCell(right.Cells, ref rightIndex, out var rightIdentity);
            if (leftCell is null || rightCell is null)
            {
                return leftCell is null && rightCell is null;
            }
            if (!CellReferenceUtility.TryParse(leftCell.CellReference, out var leftColumn, out _) ||
                !CellReferenceUtility.TryParse(rightCell.CellReference, out var rightColumn, out _) ||
                leftColumn != rightColumn ||
                leftIdentity.Kind != rightIdentity.Kind)
            {
                return false;
            }
            var comparison = leftIdentity.Kind == CellValueKind.Text && !caseSensitive
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!string.Equals(leftIdentity.Value, rightIdentity.Value, comparison))
            {
                return false;
            }
        }

        static CellSnapshot? NextIdentityCell(
            IReadOnlyList<CellSnapshot> cells,
            ref int index,
            out (CellValueKind Kind, string? Value) identity)
        {
            while (index < cells.Count)
            {
                var cell = cells[index++];
                identity = GetRowIdentityValue(cell);
                if (identity.Kind != CellValueKind.Blank || identity.Value is not null)
                {
                    return cell;
                }
            }
            identity = (CellValueKind.Blank, null);
            return null;
        }
    }

    private static (CellValueKind Kind, string? Value) GetRowIdentityValue(CellSnapshot cell)
    {
        if (cell.ValueKind is CellValueKind.Number or CellValueKind.Date &&
            ExactNumber.TryParse(cell.RawValue, out var number))
        {
            return (CellValueKind.Number, "number:" + number.ToCanonicalString());
        }
        return (cell.ValueKind, cell.NormalizedValue ?? cell.RawValue);
    }

    private static bool HasIdentityValues(WorksheetRow row) => row.MappedIdentityCells is not null
        ? row.MappedIdentityCells.Count > 0
        : row.Cells.Any(static cell =>
        {
            var identity = GetRowIdentityValue(cell);
            return identity.Kind != CellValueKind.Blank || identity.Value is not null;
        });

    private static List<AlignedWorksheetRow> AlignRows(
        IReadOnlyList<WorksheetRow> leftRows,
        IReadOnlyList<WorksheetRow> rightRows,
        Func<WorksheetRow, WorksheetRow, bool> rowsEquivalent,
        Func<WorksheetRow, bool> rowIsAmbiguous,
        bool changedRowsAreDeleteInsert,
        CancellationToken cancellationToken)
    {
        var result = new List<AlignedWorksheetRow>(Math.Max(leftRows.Count, rightRows.Count));
        AlignRange(0, leftRows.Count, 0, rightRows.Count, result);
        return result;

        void AlignRange(
            int leftStart,
            int leftEnd,
            int rightStart,
            int rightEnd,
            List<AlignedWorksheetRow> target)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorksheetRow? lastPrefixLeft = null;
            WorksheetRow? lastPrefixRight = null;
            while (leftStart < leftEnd && rightStart < rightEnd &&
                rowsEquivalent(leftRows[leftStart], rightRows[rightStart]))
            {
                lastPrefixLeft = leftRows[leftStart];
                lastPrefixRight = rightRows[rightStart];
                target.Add(new AlignedWorksheetRow(
                    leftRows[leftStart++],
                    rightRows[rightStart++],
                    RowAlignmentStatus.Matched,
                    null));
            }

            var suffix = new Stack<AlignedWorksheetRow>();
            WorksheetRow? firstSuffixLeft = null;
            WorksheetRow? firstSuffixRight = null;
            while (leftStart < leftEnd && rightStart < rightEnd &&
                rowsEquivalent(leftRows[leftEnd - 1], rightRows[rightEnd - 1]))
            {
                firstSuffixLeft = leftRows[leftEnd - 1];
                firstSuffixRight = rightRows[rightEnd - 1];
                suffix.Push(new AlignedWorksheetRow(
                    leftRows[--leftEnd],
                    rightRows[--rightEnd],
                    RowAlignmentStatus.Matched,
                    null));
            }

            if (leftStart == leftEnd || rightStart == rightEnd)
            {
                var repeatedBoundaryAmbiguous =
                    lastPrefixLeft is not null &&
                    lastPrefixRight is not null &&
                    firstSuffixLeft is not null &&
                    firstSuffixRight is not null &&
                    rowsEquivalent(lastPrefixLeft, firstSuffixLeft) &&
                    rowsEquivalent(lastPrefixRight, firstSuffixRight);
                var warningAssigned = false;
                for (var index = leftStart; index < leftEnd; index++)
                {
                    var ambiguousRow = rowIsAmbiguous(leftRows[index]);
                    var needsWarning = ambiguousRow || repeatedBoundaryAmbiguous;
                    var message = needsWarning && !warningAssigned
                        ? repeatedBoundaryAmbiguous
                            ? "额外行位于重复行之间，无法唯一确定插入或删除位置。"
                            : "关键列缺失或重复，无法可靠对齐该行。"
                        : null;
                    warningAssigned |= needsWarning;
                    target.Add(new AlignedWorksheetRow(
                        leftRows[index],
                        null,
                        RowAlignmentStatus.Deleted,
                        message));
                }
                for (var index = rightStart; index < rightEnd; index++)
                {
                    var ambiguousRow = rowIsAmbiguous(rightRows[index]);
                    var needsWarning = ambiguousRow || repeatedBoundaryAmbiguous;
                    var message = needsWarning && !warningAssigned
                        ? repeatedBoundaryAmbiguous
                            ? "额外行位于重复行之间，无法唯一确定插入或删除位置。"
                            : "关键列缺失或重复，无法可靠对齐该行。"
                        : null;
                    warningAssigned |= needsWarning;
                    target.Add(new AlignedWorksheetRow(
                        null,
                        rightRows[index],
                        RowAlignmentStatus.Inserted,
                        message));
                }
                target.AddRange(suffix);
                return;
            }

            var anchors = FindUniqueAnchors(leftStart, leftEnd, rightStart, rightEnd);
            if (anchors.Count == 0)
            {
                AddFallbackRows(leftStart, leftEnd, rightStart, rightEnd, target);
                target.AddRange(suffix);
                return;
            }

            var previousLeft = leftStart;
            var previousRight = rightStart;
            foreach (var anchor in anchors)
            {
                AlignRange(previousLeft, anchor.LeftIndex, previousRight, anchor.RightIndex, target);
                target.Add(new AlignedWorksheetRow(
                    leftRows[anchor.LeftIndex],
                    rightRows[anchor.RightIndex],
                    RowAlignmentStatus.Matched,
                    null));
                previousLeft = anchor.LeftIndex + 1;
                previousRight = anchor.RightIndex + 1;
            }
            AlignRange(previousLeft, leftEnd, previousRight, rightEnd, target);
            target.AddRange(suffix);
        }

        bool RowsEquivalent(WorksheetRow left, WorksheetRow right)
        {
            return left.Signature == right.Signature &&
                rowsEquivalent(left, right);
        }

        List<RowAnchor> FindUniqueAnchors(
            int leftStart,
            int leftEnd,
            int rightStart,
            int rightEnd)
        {
            var leftOccurrences = CountSignatures(leftRows, leftStart, leftEnd);
            var rightOccurrences = CountSignatures(rightRows, rightStart, rightEnd);
            var candidates = new List<RowAnchor>();
            foreach (var pair in leftOccurrences)
            {
                if (pair.Value.Count != 1 ||
                    !rightOccurrences.TryGetValue(pair.Key, out var rightOccurrence) ||
                    rightOccurrence.Count != 1 ||
                    !RowsEquivalent(leftRows[pair.Value.Index], rightRows[rightOccurrence.Index]))
                {
                    continue;
                }
                candidates.Add(new RowAnchor(pair.Value.Index, rightOccurrence.Index));
            }
            candidates.Sort(static (left, right) => left.LeftIndex.CompareTo(right.LeftIndex));
            return LongestIncreasingAnchors(candidates);
        }

        static Dictionary<ulong, SignatureOccurrence> CountSignatures(
            IReadOnlyList<WorksheetRow> rows,
            int start,
            int end)
        {
            var result = new Dictionary<ulong, SignatureOccurrence>();
            for (var index = start; index < end; index++)
            {
                var signature = rows[index].Signature;
                result[signature] = result.TryGetValue(signature, out var occurrence)
                    ? occurrence with { Count = occurrence.Count + 1 }
                    : new SignatureOccurrence(index, 1);
            }
            return result;
        }

        void AddFallbackRows(
            int leftStart,
            int leftEnd,
            int rightStart,
            int rightEnd,
            List<AlignedWorksheetRow> target)
        {
            var leftLength = leftEnd - leftStart;
            var rightLength = rightEnd - rightStart;
            var containsAmbiguousRows = leftRows
                .Skip(leftStart)
                .Take(leftLength)
                .Any(rowIsAmbiguous) || rightRows
                .Skip(rightStart)
                .Take(rightLength)
                .Any(rowIsAmbiguous);
            if (changedRowsAreDeleteInsert && !containsAmbiguousRows)
            {
                for (var index = leftStart; index < leftEnd; index++)
                {
                    target.Add(new AlignedWorksheetRow(
                        leftRows[index],
                        null,
                        RowAlignmentStatus.Deleted,
                        null));
                }
                for (var index = rightStart; index < rightEnd; index++)
                {
                    target.Add(new AlignedWorksheetRow(
                        null,
                        rightRows[index],
                        RowAlignmentStatus.Inserted,
                        null));
                }
                return;
            }

            var paired = Math.Min(leftLength, rightLength);
            var ambiguous = containsAmbiguousRows || leftLength != rightLength;
            var message = ambiguous
                ? "该区间存在重复行或缺少唯一精确锚点，无法可靠判定具体插入或删除位置。"
                : null;
            for (var index = 0; index < paired; index++)
            {
                var left = leftRows[leftStart + index];
                var right = rightRows[rightStart + index];
                target.Add(new AlignedWorksheetRow(
                    left,
                    right,
                    ambiguous && index == 0
                        ? RowAlignmentStatus.Ambiguous
                        : rowsEquivalent(left, right)
                            ? RowAlignmentStatus.Matched
                            : RowAlignmentStatus.Modified,
                    ambiguous && index == 0 ? message : null));
            }
            for (var index = paired; index < leftLength; index++)
            {
                target.Add(new AlignedWorksheetRow(
                    leftRows[leftStart + index],
                    null,
                    RowAlignmentStatus.Deleted,
                    null));
            }
            for (var index = paired; index < rightLength; index++)
            {
                target.Add(new AlignedWorksheetRow(
                    null,
                    rightRows[rightStart + index],
                    RowAlignmentStatus.Inserted,
                    null));
            }
        }
    }

    private static List<RowAnchor> LongestIncreasingAnchors(IReadOnlyList<RowAnchor> candidates)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var tails = new int[candidates.Count];
        var previous = new int[candidates.Count];
        Array.Fill(previous, -1);
        var length = 0;
        for (var index = 0; index < candidates.Count; index++)
        {
            var low = 0;
            var high = length;
            while (low < high)
            {
                var middle = low + ((high - low) / 2);
                if (candidates[tails[middle]].RightIndex < candidates[index].RightIndex)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            if (low > 0)
            {
                previous[index] = tails[low - 1];
            }
            tails[low] = index;
            if (low == length)
            {
                length++;
            }
        }

        var result = new RowAnchor[length];
        var current = tails[length - 1];
        for (var index = length - 1; index >= 0; index--)
        {
            result[index] = candidates[current];
            current = previous[current];
        }
        return [.. result];
    }

    private static void CompareAlignedRowCells(
        string sheetName,
        int displayRow,
        WorksheetRow? leftRow,
        WorksheetRow? rightRow,
        CellFormatSnapshot leftDefaultFormat,
        CellFormatSnapshot rightDefaultFormat,
        ComparisonOptions options,
        List<Difference> differences,
        ResolvedColumnMapping columnMapping)
    {
        var leftCells = leftRow?.Cells ?? Array.Empty<CellSnapshot>();
        var rightCells = rightRow?.Cells ?? Array.Empty<CellSnapshot>();
        if (!columnMapping.IsEmpty)
        {
            CompareMappedCells();
            return;
        }

        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < leftCells.Count || rightIndex < rightCells.Count)
        {
            var left = leftIndex < leftCells.Count ? leftCells[leftIndex] : null;
            var right = rightIndex < rightCells.Count ? rightCells[rightIndex] : null;
            var leftColumn = left is null ? int.MaxValue : ReadColumn(left.CellReference);
            var rightColumn = right is null ? int.MaxValue : ReadColumn(right.CellReference);
            if (leftColumn < rightColumn)
            {
                CompareCell(
                    sheetName,
                    left,
                    null,
                    leftDefaultFormat,
                    rightDefaultFormat,
                    options,
                    differences,
                    CellReferenceUtility.ToColumnName(leftColumn) + displayRow.ToString());
                leftIndex++;
            }
            else if (rightColumn < leftColumn)
            {
                CompareCell(
                    sheetName,
                    null,
                    right,
                    leftDefaultFormat,
                    rightDefaultFormat,
                    options,
                    differences,
                    CellReferenceUtility.ToColumnName(rightColumn) + displayRow.ToString());
                rightIndex++;
            }
            else
            {
                CompareCell(
                    sheetName,
                    left,
                    right,
                    leftDefaultFormat,
                    rightDefaultFormat,
                    options,
                    differences,
                    CellReferenceUtility.ToColumnName(leftColumn) + displayRow.ToString());
                leftIndex++;
                rightIndex++;
            }
        }

        void CompareMappedCells()
        {
            var leftByColumn = IndexCells(leftCells);
            var rightByColumn = IndexCells(rightCells);
            foreach (var pair in leftByColumn)
            {
                if (columnMapping.LeftToRight.ContainsKey(pair.Key) ||
                    !columnMapping.RightToLeft.ContainsKey(pair.Key) ||
                    !columnMapping.MarkConflictReported(isLeft: true, pair.Key))
                {
                    continue;
                }
                AddDifference(
                    differences,
                    DifferenceKind.Warning,
                    sheetName,
                    CellReferenceUtility.ToColumnName(pair.Key) + displayRow.ToString(),
                    "左侧有内容的列与已映射的右侧列冲突，无法自动比较；请补充显式列配对。",
                    pair.Value,
                    null,
                    pair.Value.RawValue,
                    null);
            }
            foreach (var pair in rightByColumn)
            {
                if (columnMapping.RightToLeft.ContainsKey(pair.Key) ||
                    !columnMapping.LeftToRight.ContainsKey(pair.Key) ||
                    !columnMapping.MarkConflictReported(isLeft: false, pair.Key))
                {
                    continue;
                }
                AddDifference(
                    differences,
                    DifferenceKind.Warning,
                    sheetName,
                    CellReferenceUtility.ToColumnName(pair.Key) + displayRow.ToString(),
                    "右侧有内容的列与已映射的左侧列冲突，无法自动比较；请补充显式列配对。",
                    null,
                    pair.Value,
                    null,
                    pair.Value.RawValue);
            }

            foreach (var pair in columnMapping.Pairs)
            {
                leftByColumn.TryGetValue(pair.LeftColumn, out var left);
                rightByColumn.TryGetValue(pair.RightColumn, out var right);
                if (left is null && right is null)
                {
                    continue;
                }
                CompareCell(
                    sheetName,
                    left,
                    right,
                    leftDefaultFormat,
                    rightDefaultFormat,
                    options,
                    differences,
                    CellReferenceUtility.ToColumnName(pair.LeftColumn) + displayRow.ToString());
            }

            var fallbackColumns = leftByColumn.Keys
                .Concat(rightByColumn.Keys)
                .Where(column =>
                    !columnMapping.LeftToRight.ContainsKey(column) &&
                    !columnMapping.RightToLeft.ContainsKey(column))
                .Distinct()
                .Order();
            foreach (var column in fallbackColumns)
            {
                leftByColumn.TryGetValue(column, out var left);
                rightByColumn.TryGetValue(column, out var right);
                CompareCell(
                    sheetName,
                    left,
                    right,
                    leftDefaultFormat,
                    rightDefaultFormat,
                    options,
                    differences,
                    CellReferenceUtility.ToColumnName(column) + displayRow.ToString());
            }
        }

        static Dictionary<int, CellSnapshot> IndexCells(IReadOnlyList<CellSnapshot> cells)
        {
            var result = new Dictionary<int, CellSnapshot>(cells.Count);
            foreach (var cell in cells)
            {
                result[ReadColumn(cell.CellReference)] = cell;
            }
            return result;
        }

        static int ReadColumn(string reference)
        {
            if (!CellReferenceUtility.TryParse(reference, out var column, out _))
            {
                throw new InvalidDataException($"无效单元格地址“{reference}”。");
            }
            return column;
        }
    }

    private static void AddRowDifference(
        List<Difference> differences,
        DifferenceKind kind,
        string sheetName,
        int displayRow,
        WorksheetRow? left,
        WorksheetRow? right,
        string description,
        string? leftDetail,
        string? rightDetail,
        ResolvedColumnMapping columnMapping)
    {
        var (firstCell, displayColumn) = FindFirstDisplayCell(left, isLeft: true);
        if (firstCell is null)
        {
            (firstCell, displayColumn) = FindFirstDisplayCell(right, isLeft: false);
        }
        var reference = displayColumn is null
            ? null
            : CellReferenceUtility.ToColumnName(displayColumn.Value) + displayRow.ToString();
        AddDifference(
            differences,
            kind,
            sheetName,
            reference,
            description,
            left?.Cells.FirstOrDefault(),
            right?.Cells.FirstOrDefault(),
            leftDetail,
            rightDetail);

        (CellSnapshot? Cell, int? DisplayColumn) FindFirstDisplayCell(WorksheetRow? row, bool isLeft)
        {
            CellSnapshot? selected = null;
            int? selectedColumn = null;
            foreach (var cell in row?.Cells ?? Array.Empty<CellSnapshot>())
            {
                if (!CellReferenceUtility.TryParse(cell.CellReference, out var actualColumn, out _) ||
                    !columnMapping.TryGetDisplayColumn(actualColumn, isLeft, out var candidateColumn))
                {
                    continue;
                }
                if (selectedColumn is null || candidateColumn < selectedColumn.Value)
                {
                    selected = cell;
                    selectedColumn = candidateColumn;
                }
            }
            return (selected, selectedColumn);
        }
    }

    private static bool TryParseColumnIdentifier(string? identifier, out int column)
    {
        column = 0;
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }
        var value = identifier.Trim();
        if (value.Length is < 1 or > 3 || value.Any(static character => !char.IsAsciiLetter(character)))
        {
            return false;
        }
        column = CellReferenceUtility.FromColumnName(value);
        return column is >= 1 and <= 16_384;
    }

    private sealed record WorksheetRow(
        int RowNumber,
        IReadOnlyList<CellSnapshot> Cells,
        ulong Signature,
        string? AlignmentKey = null,
        bool KeyAmbiguous = false,
        IReadOnlyList<RowIdentityCell>? MappedIdentityCells = null);

    private readonly record struct RowIdentityCell(
        int DisplayColumn,
        CellValueKind Kind,
        string? Value);

    private readonly record struct ResolvedColumnPair(int LeftColumn, int RightColumn);

    private sealed class ResolvedColumnMapping
    {
        private readonly HashSet<(bool IsLeft, int Column)> _reportedConflicts = [];

        public static ResolvedColumnMapping Empty { get; } = new(
            [],
            new Dictionary<int, int>(),
            new Dictionary<int, int>(),
            []);

        private ResolvedColumnMapping(
            IReadOnlyList<ResolvedColumnPair> pairs,
            IReadOnlyDictionary<int, int> leftToRight,
            IReadOnlyDictionary<int, int> rightToLeft,
            IReadOnlyList<ColumnPair> publicPairs)
        {
            Pairs = pairs;
            LeftToRight = leftToRight;
            RightToLeft = rightToLeft;
            PublicPairs = publicPairs;
        }

        public IReadOnlyList<ResolvedColumnPair> Pairs { get; }
        public IReadOnlyDictionary<int, int> LeftToRight { get; }
        public IReadOnlyDictionary<int, int> RightToLeft { get; }
        public IReadOnlyList<ColumnPair> PublicPairs { get; }
        public bool IsEmpty => Pairs.Count == 0;

        public bool MarkConflictReported(bool isLeft, int column) =>
            _reportedConflicts.Add((isLeft, column));

        public static ResolvedColumnMapping Create(IReadOnlyList<ResolvedColumnPair> pairs)
        {
            var leftToRight = pairs.ToDictionary(static pair => pair.LeftColumn, static pair => pair.RightColumn);
            var rightToLeft = pairs.ToDictionary(static pair => pair.RightColumn, static pair => pair.LeftColumn);
            var publicPairs = pairs
                .Select(static pair => new ColumnPair(
                    CellReferenceUtility.ToColumnName(pair.LeftColumn),
                    CellReferenceUtility.ToColumnName(pair.RightColumn)))
                .ToArray();
            return new ResolvedColumnMapping(pairs, leftToRight, rightToLeft, publicPairs);
        }

        public bool TryGetDisplayColumn(int actualColumn, bool isLeft, out int displayColumn)
        {
            if (isLeft)
            {
                if (LeftToRight.ContainsKey(actualColumn))
                {
                    displayColumn = actualColumn;
                    return true;
                }
                if (RightToLeft.ContainsKey(actualColumn))
                {
                    displayColumn = 0;
                    return false;
                }
            }
            else
            {
                if (RightToLeft.TryGetValue(actualColumn, out displayColumn))
                {
                    return true;
                }
                if (LeftToRight.ContainsKey(actualColumn))
                {
                    displayColumn = 0;
                    return false;
                }
            }

            displayColumn = actualColumn;
            return true;
        }
    }

    private sealed record AlignedWorksheetRow(
        WorksheetRow? Left,
        WorksheetRow? Right,
        RowAlignmentStatus Status,
        string? Message);

    private sealed record RowReadResult(IReadOnlyList<WorksheetRow> Rows, int CellCount);
    private sealed record KeyColumnSpec(int HeaderRow, IReadOnlyList<int> Columns);
    private sealed record WorksheetPairPlan(AccessSheet? Left, AccessSheet? Right, string DisplayName);
    private readonly record struct SignatureOccurrence(int Index, int Count);
    private readonly record struct RowAnchor(int LeftIndex, int RightIndex);

    private static void CompareCell(
        string sheetName,
        CellSnapshot? left,
        CellSnapshot? right,
        CellFormatSnapshot leftDefaultFormat,
        CellFormatSnapshot rightDefaultFormat,
        ComparisonOptions options,
        List<Difference> differences,
        string? displayReference = null)
    {
        var reference = displayReference ?? left?.CellReference ?? right!.CellReference;
        var leftFormulaKind = left?.FormulaKind ?? FormulaKind.None;
        var rightFormulaKind = right?.FormulaKind ?? FormulaKind.None;
        var leftHasFormula = !string.IsNullOrEmpty(left?.Formula) || leftFormulaKind != FormulaKind.None;
        var rightHasFormula = !string.IsNullOrEmpty(right?.Formula) || rightFormulaKind != FormulaKind.None;

        if (HasUnusableFormulaCache(left, leftHasFormula) ||
            HasUnusableFormulaCache(right, rightHasFormula))
        {
            AddDifference(
                differences,
                DifferenceKind.Warning,
                sheetName,
                reference,
                "公式缓存结果缺失，无法判定保存结果是否相同。",
                left,
                right,
                left?.RawValue,
                right?.RawValue);
        }

        var leftIsArray = leftFormulaKind == FormulaKind.Array;
        var rightIsArray = rightFormulaKind == FormulaKind.Array;
        if (options.CompareFormulas && (leftHasFormula || rightHasFormula) &&
            (!TextEquals(left?.Formula, right?.Formula, options.CaseSensitive) ||
             leftIsArray != rightIsArray ||
             ((leftIsArray || rightIsArray) &&
              !string.Equals(left?.FormulaReference, right?.FormulaReference, StringComparison.OrdinalIgnoreCase))))
        {
            AddDifference(
                differences,
                DifferenceKind.Formula,
                sheetName,
                reference,
                "公式文本或公式范围不同。",
                left,
                right,
                left?.Formula,
                right?.Formula);
        }

        var identicalRawNumbers = !options.CompareFormatting &&
            left?.ValueKind == CellValueKind.Number &&
            right?.ValueKind == CellValueKind.Number &&
            left.RawValue is not null &&
            string.Equals(left.RawValue, right.RawValue, StringComparison.Ordinal);
        var (leftKind, leftValue) = identicalRawNumbers
            ? (CellValueKind.Number, left!.RawValue)
            : GetComparisonValue(left, options.CompareFormatting);
        var (rightKind, rightValue) = identicalRawNumbers
            ? (CellValueKind.Number, right!.RawValue)
            : GetComparisonValue(right, options.CompareFormatting);
        if (leftKind != rightKind)
        {
            AddDifference(
                differences,
                DifferenceKind.CellType,
                sheetName,
                reference,
                "单元格类型不同。",
                left,
                right,
                leftKind.ToString(),
                rightKind.ToString());
        }
        var valuesEqual = leftKind == CellValueKind.Text && rightKind == CellValueKind.Text
            ? TextEquals(leftValue, rightValue, options.CaseSensitive)
            : string.Equals(leftValue, rightValue, StringComparison.Ordinal);
        if (!valuesEqual)
        {
            var isWhitespaceOnlyDifference = IsWhitespaceOnlyDifference(left, right);
            AddDifference(
                differences,
                leftHasFormula || rightHasFormula ? DifferenceKind.FormulaResult : DifferenceKind.Value,
                sheetName,
                reference,
                isWhitespaceOnlyDifference ? "空白字符不同。" : "单元格值不同。",
                left,
                right,
                isWhitespaceOnlyDifference ? VisualizeWhitespace(left?.RawValue) : left?.RawValue,
                isWhitespaceOnlyDifference ? VisualizeWhitespace(right?.RawValue) : right?.RawValue);
        }

        if (!options.CompareFormatting &&
            !options.CompareFonts &&
            !options.CompareComments &&
            !options.CompareHyperlinks)
        {
            return;
        }

        var leftFormat = left?.Format ?? leftDefaultFormat;
        var rightFormat = right?.Format ?? rightDefaultFormat;
        if (options.CompareFormatting)
        {
            CompareFormatProperty(DifferenceKind.NumberFormat, "数字格式不同。", leftFormat.NumberFormatCode, rightFormat.NumberFormatCode);
            CompareFormatProperty(DifferenceKind.Fill, "填充格式不同。", leftFormat.FillFingerprint, rightFormat.FillFingerprint);
            CompareFormatProperty(DifferenceKind.Border, "边框格式不同。", leftFormat.BorderFingerprint, rightFormat.BorderFingerprint);
            CompareFormatProperty(DifferenceKind.Alignment, "对齐格式不同。", leftFormat.AlignmentFingerprint, rightFormat.AlignmentFingerprint);
        }
        if (options.CompareFonts)
        {
            CompareFormatProperty(
                DifferenceKind.Font,
                "字体格式不同。",
                FontDetail(leftFormat, left),
                FontDetail(rightFormat, right));
        }
        if (options.CompareComments)
        {
            CompareTextProperty(DifferenceKind.Comment, "批注不同。", CommentDetail(left), CommentDetail(right));
        }
        if (options.CompareHyperlinks)
        {
            CompareTextProperty(
                DifferenceKind.Hyperlink,
                "超链接不同。",
                left?.HyperlinkFingerprint ?? left?.Hyperlink,
                right?.HyperlinkFingerprint ?? right?.Hyperlink);
        }

        void CompareFormatProperty(DifferenceKind kind, string description, string? leftDetail, string? rightDetail)
        {
            if (!string.Equals(leftDetail, rightDetail, StringComparison.Ordinal))
            {
                AddDifference(differences, kind, sheetName, reference, description, left, right, leftDetail, rightDetail);
            }
        }

        void CompareTextProperty(DifferenceKind kind, string description, string? leftDetail, string? rightDetail)
        {
            if (!TextEquals(leftDetail, rightDetail, options.CaseSensitive))
            {
                AddDifference(differences, kind, sheetName, reference, description, left, right, leftDetail, rightDetail);
            }
        }
    }

    private static bool TextEquals(string? left, string? right, bool caseSensitive) =>
        string.Equals(left, right, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static bool HasUnusableFormulaCache(CellSnapshot? cell, bool hasFormula)
    {
        if (!hasFormula || cell is null)
        {
            return false;
        }

        return cell.FormulaCacheState switch
        {
            FormulaCacheState.Missing or FormulaCacheState.Empty => true,
            FormulaCacheState.Present or FormulaCacheState.ValidEmptyString => false,
            _ => cell.RawValue is null
        };
    }

    private static string FontDetail(CellFormatSnapshot format, CellSnapshot? cell) =>
        cell?.RichTextFingerprint is { Length: > 0 } richText
            ? format.FontFingerprint + "\n富文本字体：" + richText
            : format.FontFingerprint;

    private static string? CommentDetail(CellSnapshot? cell) => cell?.Comment is null
        ? null
        : (cell.CommentAuthor ?? string.Empty) + "\n" + cell.Comment;

    private static (CellValueKind Kind, string? Value) GetComparisonValue(
        CellSnapshot? cell,
        bool compareFormatting)
    {
        if (cell is null)
        {
            return (CellValueKind.Blank, null);
        }
        if (!compareFormatting &&
            cell.ValueKind is CellValueKind.Number or CellValueKind.Date &&
            cell.NormalizedValue is { } normalized &&
            normalized.StartsWith("number:", StringComparison.Ordinal))
        {
            return (CellValueKind.Number, normalized);
        }
        if (!compareFormatting &&
            cell.ValueKind is CellValueKind.Number or CellValueKind.Date &&
            ExactNumber.TryParse(cell.RawValue, out var number))
        {
            return (CellValueKind.Number, "number:" + number.ToCanonicalString());
        }
        return (cell.ValueKind, cell.NormalizedValue);
    }

    private static bool IsWhitespaceOnlyDifference(CellSnapshot? left, CellSnapshot? right)
    {
        var leftKind = left?.ValueKind ?? CellValueKind.Blank;
        var rightKind = right?.ValueKind ?? CellValueKind.Blank;
        if (leftKind is not (CellValueKind.Text or CellValueKind.Blank) ||
            rightKind is not (CellValueKind.Text or CellValueKind.Blank))
        {
            return false;
        }

        var leftValue = left?.RawValue ?? string.Empty;
        var rightValue = right?.RawValue ?? string.Empty;
        return !string.Equals(leftValue, rightValue, StringComparison.Ordinal) &&
            string.Equals(RemoveWhitespace(leftValue), RemoveWhitespace(rightValue), StringComparison.Ordinal);
    }

    private static string RemoveWhitespace(string value) =>
        new(value.Where(static character => !char.IsWhiteSpace(character)).ToArray());

    private static string VisualizeWhitespace(string? value)
    {
        if (value is null)
        {
            return "⟦无值⟧";
        }
        if (value.Length == 0)
        {
            return "⟦空字符串⟧";
        }

        var result = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            result.Append(character switch
            {
                ' ' => "␠",
                '\t' => "⇥",
                '\r' => "␍",
                '\n' => "␊",
                _ when char.IsWhiteSpace(character) => $"⟦U+{(int)character:X4}⟧",
                _ => character.ToString()
            });
        }
        return result.ToString();
    }

    private static void CompareLayout(
        string sheetName,
        WorksheetLayout? left,
        WorksheetLayout? right,
        List<Difference> differences)
    {
        if (left is null || right is null)
        {
            return;
        }

        CompareSets(DifferenceKind.Merge, "合并区域", left.MergedRanges, right.MergedRanges);
        CompareSets(
            DifferenceKind.RowHidden,
            "隐藏行",
            left.HiddenRows.Select(static row => row.ToString()),
            right.HiddenRows.Select(static row => row.ToString()));
        CompareSets(
            DifferenceKind.ColumnHidden,
            "隐藏列",
            left.HiddenColumns.Select(static column => column.DisplayRange),
            right.HiddenColumns.Select(static column => column.DisplayRange));

        foreach (var row in left.ExplicitEmptyRows.Except(right.ExplicitEmptyRows).Order())
        {
            AddDifference(
                differences,
                DifferenceKind.RowDeleted,
                sheetName,
                "A" + row.ToString(),
                "显式存储的空行仅存在于左侧。",
                null,
                null,
                row.ToString(),
                null);
        }
        foreach (var row in right.ExplicitEmptyRows.Except(left.ExplicitEmptyRows).Order())
        {
            AddDifference(
                differences,
                DifferenceKind.RowInserted,
                sheetName,
                "A" + row.ToString(),
                "显式存储的空行仅存在于右侧。",
                null,
                null,
                null,
                row.ToString());
        }

        foreach (var item in left.UnhandledObjects.Concat(right.UnhandledObjects).Distinct(StringComparer.Ordinal))
        {
            AddDifference(
                differences,
                DifferenceKind.UncomparedObject,
                sheetName,
                null,
                $"检测到未比较对象：{item}。",
                null,
                null,
                left.UnhandledObjects.Contains(item) ? item : null,
                right.UnhandledObjects.Contains(item) ? item : null);
        }

        void CompareSets(DifferenceKind kind, string label, IEnumerable<string> leftItems, IEnumerable<string> rightItems)
        {
            var leftSet = new HashSet<string>(leftItems, StringComparer.OrdinalIgnoreCase);
            var rightSet = new HashSet<string>(rightItems, StringComparer.OrdinalIgnoreCase);
            foreach (var item in leftSet.Except(rightSet, StringComparer.OrdinalIgnoreCase))
            {
                AddDifference(differences, kind, sheetName, item, $"{label}仅存在于左侧。", null, null, item, null);
            }
            foreach (var item in rightSet.Except(leftSet, StringComparer.OrdinalIgnoreCase))
            {
                AddDifference(differences, kind, sheetName, item, $"{label}仅存在于右侧。", null, null, null, item);
            }
        }
    }

    private static void AddDifference(
        List<Difference> differences,
        DifferenceKind kind,
        string? sheetName,
        string? reference,
        string description,
        CellSnapshot? left,
        CellSnapshot? right,
        string? leftDetail,
        string? rightDetail) =>
        differences.Add(new Difference(kind, sheetName, reference, description, left, right, leftDetail, rightDetail));

    private static ComparisonStatus DetermineStatus(
        IReadOnlyList<Difference> workbookDifferences,
        IReadOnlyList<WorksheetCompareResult> worksheets)
    {
        if (worksheets.Any(static worksheet => worksheet.Status is ComparisonStatus.Different or ComparisonStatus.LeftOnly or ComparisonStatus.RightOnly) ||
            workbookDifferences.Any(static difference => difference.Kind is not (
                DifferenceKind.Warning or DifferenceKind.UncomparedObject or DifferenceKind.RowAlignmentWarning)))
        {
            return ComparisonStatus.Different;
        }
        if (workbookDifferences.Count > 0 || worksheets.Any(static worksheet => worksheet.Status == ComparisonStatus.Warning))
        {
            return ComparisonStatus.Warning;
        }
        return ComparisonStatus.Same;
    }

    private static ComparisonStatus DetermineStatus(IReadOnlyList<Difference> differences)
    {
        if (differences.Count == 0)
        {
            return ComparisonStatus.Same;
        }
        return differences.All(static difference => difference.Kind is
            DifferenceKind.Warning or DifferenceKind.UncomparedObject or DifferenceKind.RowAlignmentWarning)
            ? ComparisonStatus.Warning
            : ComparisonStatus.Different;
    }

    private static IEnumerable<string> ActiveWarnings(
        IEnumerable<string> warnings,
        ComparisonOptions options) => warnings.Where(warning =>
        {
            if (warning.Contains("现代批注", StringComparison.Ordinal))
            {
                return options.CompareComments || options.CompareLayout;
            }
            return options.CompareLayout || !IsUncomparedObjectWarning(warning);
        });

    private static bool IsUncomparedObjectWarning(string warning) =>
        warning.Contains("检测到名称；", StringComparison.Ordinal) ||
        warning.Contains("检测到外部链接；", StringComparison.Ordinal) ||
        warning.Contains("检测到图表工作表；", StringComparison.Ordinal) ||
        warning.Contains("检测到现代批注；", StringComparison.Ordinal);

    private async Task<IWorkbookAccess> CreateAccessAsync(
        string path,
        ComparisonOptions options,
        CancellationToken cancellationToken)
    {
        if (_reader is OpenXmlWorkbookReader)
        {
            return new DocumentWorkbookAccess(WorkbookDocument.Open(
                path,
                WorkbookReadProfile.ForComparison(options),
                cancellationToken));
        }

        var metadata = await _reader.ReadMetadataAsync(path, cancellationToken).ConfigureAwait(false);
        return new ReaderWorkbookAccess(_reader, metadata);
    }

    private async Task<IWorkbookAccess> CreateByteIdenticalProbeAccessAsync(
        string path,
        ComparisonOptions options,
        CancellationToken cancellationToken)
    {
        if (_reader is OpenXmlWorkbookReader)
        {
            return new DocumentWorkbookAccess(WorkbookDocument.Open(
                path,
                WorkbookReadProfile.ByteIdenticalProbe,
                cancellationToken));
        }

        return await CreateAccessAsync(path, options, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(IWorkbookAccess Left, IWorkbookAccess Right)> CreateAccessPairAsync(
        string leftPath,
        string rightPath,
        ComparisonOptions options,
        CancellationToken cancellationToken)
    {
        if (_reader is not OpenXmlWorkbookReader)
        {
            var left = await CreateAccessAsync(leftPath, options, cancellationToken).ConfigureAwait(false);
            try
            {
                var right = await CreateAccessAsync(rightPath, options, cancellationToken).ConfigureAwait(false);
                return (left, right);
            }
            catch
            {
                left.Dispose();
                throw;
            }
        }

        var profile = WorkbookReadProfile.ForComparison(options);
        var leftTask = Task.Run<IWorkbookAccess>(
            () => new DocumentWorkbookAccess(WorkbookDocument.Open(leftPath, profile, cancellationToken)),
            cancellationToken);
        var rightTask = Task.Run<IWorkbookAccess>(
            () => new DocumentWorkbookAccess(WorkbookDocument.Open(rightPath, profile, cancellationToken)),
            cancellationToken);
        try
        {
            await Task.WhenAll(leftTask, rightTask).ConfigureAwait(false);
            return (await leftTask.ConfigureAwait(false), await rightTask.ConfigureAwait(false));
        }
        catch
        {
            if (leftTask.Status == TaskStatus.RanToCompletion)
            {
                leftTask.Result.Dispose();
            }
            if (rightTask.Status == TaskStatus.RanToCompletion)
            {
                rightTask.Result.Dispose();
            }
            throw;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static async Task EnsureSourceHashesUnchangedAsync(
        string leftPath,
        string rightPath,
        string expectedLeftHash,
        string expectedRightHash,
        CancellationToken cancellationToken)
    {
        var currentLeftHashTask = ComputeSha256Async(leftPath, cancellationToken);
        var currentRightHashTask = ComputeSha256Async(rightPath, cancellationToken);
        await Task.WhenAll(currentLeftHashTask, currentRightHashTask).ConfigureAwait(false);
        var currentLeftHash = await currentLeftHashTask.ConfigureAwait(false);
        var currentRightHash = await currentRightHashTask.ConfigureAwait(false);
        if (!string.Equals(currentLeftHash, expectedLeftHash, StringComparison.Ordinal) ||
            !string.Equals(currentRightHash, expectedRightHash, StringComparison.Ordinal))
        {
            throw new IOException("比较期间源文件发生变化，本次结果已作废；请刷新后重新比较。");
        }
    }
}

internal interface IWorkbookAccess : IDisposable
{
    IReadOnlyList<AccessSheet> Sheets { get; }
    IReadOnlyList<string> Warnings { get; }
    CellFormatSnapshot DefaultFormat { get; }
    WorksheetLayout? GetLayout(AccessSheet sheet);
    IAsyncEnumerable<CellSnapshot> ReadCellsAsync(AccessSheet sheet, CancellationToken cancellationToken);
}

internal sealed record AccessSheet(string Name, int Index, string Visibility, object Handle);

internal sealed class DocumentWorkbookAccess : IWorkbookAccess
{
    private readonly WorkbookDocument _workbook;

    public DocumentWorkbookAccess(WorkbookDocument workbook)
    {
        _workbook = workbook;
        Sheets = workbook.Sheets
            .Select(static sheet => new AccessSheet(sheet.Name, sheet.Index, sheet.Visibility, sheet))
            .ToArray();
    }

    public IReadOnlyList<AccessSheet> Sheets { get; }
    public IReadOnlyList<string> Warnings => _workbook.Warnings;
    public CellFormatSnapshot DefaultFormat => _workbook.DefaultFormat;
    public WorksheetLayout GetLayout(AccessSheet sheet) => _workbook.ReadLayout((SheetEntry)sheet.Handle);
    public IAsyncEnumerable<CellSnapshot> ReadCellsAsync(AccessSheet sheet, CancellationToken cancellationToken) =>
        _workbook.ReadCellsAsync((SheetEntry)sheet.Handle, cancellationToken);
    public void Dispose() => _workbook.Dispose();
}

internal sealed class ReaderWorkbookAccess : IWorkbookAccess
{
    private readonly IWorkbookReader _reader;
    private readonly string _filePath;

    public ReaderWorkbookAccess(IWorkbookReader reader, WorkbookInfo metadata)
    {
        _reader = reader;
        _filePath = metadata.FilePath;
        Warnings = metadata.Warnings;
        Sheets = metadata.Worksheets
            .Select(static sheet => new AccessSheet(sheet.Name, sheet.Index, sheet.Visibility, sheet.Name))
            .ToArray();
    }

    public IReadOnlyList<AccessSheet> Sheets { get; }
    public IReadOnlyList<string> Warnings { get; }
    public CellFormatSnapshot DefaultFormat { get; } = new(
        "General",
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        "FF000000",
        "FFFFFFFF");
    public WorksheetLayout? GetLayout(AccessSheet sheet) => null;
    public IAsyncEnumerable<CellSnapshot> ReadCellsAsync(AccessSheet sheet, CancellationToken cancellationToken) =>
        _reader.ReadCellsAsync(_filePath, sheet.Name, cancellationToken);
    public void Dispose()
    {
    }
}
