using System.Globalization;

namespace ZCompare.Core;

public enum AlignmentSuggestionKind
{
    KeyColumns,
    ColumnMapping,
    GroupingColumn
}

public sealed record AlignmentSuggestionOptions
{
    public int LeftHeaderRow { get; init; } = 1;
    public int RightHeaderRow { get; init; } = 1;
    public int MaxRowsPerSheet { get; init; } = 5_000;
    public int MaxColumnsPerSheet { get; init; } = 64;
    public int MaxSuggestions { get; init; } = 20;
    public int MaxSamples { get; init; } = 5;
}

public sealed record AlignmentSuggestion(
    string Id,
    AlignmentSuggestionKind Kind,
    string Title,
    double ConfidencePercent,
    string Reason,
    IReadOnlyList<string> LeftColumns,
    IReadOnlyList<string> RightColumns,
    IReadOnlyList<ColumnPair> ColumnPairs,
    int LeftHeaderRow,
    int RightHeaderRow,
    int LeftSampledRows,
    int RightSampledRows,
    double LeftCoveragePercent,
    double RightCoveragePercent,
    double LeftUniquenessPercent,
    double RightUniquenessPercent,
    double CrossCoveragePercent,
    IReadOnlyList<string> Samples,
    bool CanApply);

public sealed record AlignmentSuggestionResult(
    string LeftWorksheetName,
    string RightWorksheetName,
    int LeftSampledRows,
    int RightSampledRows,
    bool LeftRowsTruncated,
    bool RightRowsTruncated,
    bool LeftColumnsTruncated,
    bool RightColumnsTruncated,
    IReadOnlyList<AlignmentSuggestion> Suggestions);

public sealed class AlignmentSuggestionService
{
    private const int MaximumRows = 20_000;
    private const int MaximumColumns = 128;
    private const int CompositePairCandidateLimit = 12;
    private readonly IWorkbookReader _reader;

    public AlignmentSuggestionService(IWorkbookReader? reader = null)
    {
        _reader = reader ?? new OpenXmlWorkbookReader();
    }

    public async Task<AlignmentSuggestionResult> AnalyzeAsync(
        string leftPath,
        string rightPath,
        string leftWorksheetName,
        string rightWorksheetName,
        AlignmentSuggestionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leftPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rightPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(leftWorksheetName);
        ArgumentException.ThrowIfNullOrWhiteSpace(rightWorksheetName);

        options ??= new AlignmentSuggestionOptions();
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        var left = await ReadSampleAsync(
            leftPath,
            leftWorksheetName,
            options.LeftHeaderRow,
            options,
            cancellationToken).ConfigureAwait(false);
        var right = await ReadSampleAsync(
            rightPath,
            rightWorksheetName,
            options.RightHeaderRow,
            options,
            cancellationToken).ConfigureAwait(false);

        var leftProfiles = BuildColumnProfiles(left, cancellationToken);
        var rightProfiles = BuildColumnProfiles(right, cancellationToken);
        var pairs = AnalyzeColumnPairs(leftProfiles, rightProfiles, cancellationToken);

        var suggestions = new List<AlignmentSuggestion>();
        suggestions.AddRange(BuildKeySuggestions(left, right, pairs, options, cancellationToken));
        suggestions.AddRange(BuildMappingSuggestions(left, right, pairs, options, cancellationToken));
        suggestions.AddRange(BuildGroupingSuggestions(left, right, pairs, options, cancellationToken));

        var limited = suggestions
            .OrderBy(static suggestion => SuggestionOrder(suggestion.Kind))
            .ThenByDescending(static suggestion => suggestion.ConfidencePercent)
            .ThenBy(static suggestion => suggestion.Id, StringComparer.Ordinal)
            .Take(options.MaxSuggestions)
            .ToArray();

        return new AlignmentSuggestionResult(
            leftWorksheetName,
            rightWorksheetName,
            left.Rows.Count,
            right.Rows.Count,
            left.RowsTruncated,
            right.RowsTruncated,
            left.ColumnsTruncated,
            right.ColumnsTruncated,
            limited);
    }

    private static int SuggestionOrder(AlignmentSuggestionKind kind) => kind switch
    {
        AlignmentSuggestionKind.KeyColumns => 0,
        AlignmentSuggestionKind.ColumnMapping => 1,
        _ => 2,
    };

    private static void ValidateOptions(AlignmentSuggestionOptions options)
    {
        if (options.LeftHeaderRow is < 1 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "左侧表头行必须在 1 到 1048576 之间。");
        }
        if (options.RightHeaderRow is < 1 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "右侧表头行必须在 1 到 1048576 之间。");
        }
        if (options.MaxRowsPerSheet is < 1 or > MaximumRows)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"每张工作表的样本行数必须在 1 到 {MaximumRows} 之间。");
        }
        if (options.MaxColumnsPerSheet is < 1 or > MaximumColumns)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"每张工作表的样本列数必须在 1 到 {MaximumColumns} 之间。");
        }
        if (options.MaxSuggestions is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "建议数量必须在 1 到 100 之间。");
        }
        if (options.MaxSamples is < 0 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "展示样本数必须在 0 到 20 之间。");
        }
    }

    private async Task<SheetSample> ReadSampleAsync(
        string filePath,
        string worksheetName,
        int headerRow,
        AlignmentSuggestionOptions options,
        CancellationToken cancellationToken)
    {
        var header = new Dictionary<int, SampleValue>();
        var rows = new List<SampleRow>(Math.Min(options.MaxRowsPerSheet, 512));
        Dictionary<int, SampleValue>? currentCells = null;
        var currentRow = -1;
        var truncated = false;
        var columnsTruncated = false;

        await foreach (var cell in ReadValueCellsAsync(filePath, worksheetName, cancellationToken)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CellReferenceUtility.TryParse(cell.CellReference, out var column, out var row) || row < headerRow)
            {
                continue;
            }
            if (column > options.MaxColumnsPerSheet)
            {
                columnsTruncated = true;
                continue;
            }

            var value = new SampleValue(cell.ValueKind, cell.RawValue);
            if (row == headerRow)
            {
                header[column] = value;
                continue;
            }

            if (row != currentRow)
            {
                if (currentCells is not null)
                {
                    rows.Add(new SampleRow(currentCells));
                }
                if (rows.Count >= options.MaxRowsPerSheet)
                {
                    truncated = true;
                    break;
                }

                currentRow = row;
                currentCells = new Dictionary<int, SampleValue>();
            }

            currentCells![column] = value;
        }

        if (!truncated && currentCells is not null && rows.Count < options.MaxRowsPerSheet)
        {
            rows.Add(new SampleRow(currentCells));
        }

        return new SheetSample(headerRow, header, rows, truncated, columnsTruncated);
    }

    private IAsyncEnumerable<CellSnapshot> ReadValueCellsAsync(
        string filePath,
        string worksheetName,
        CancellationToken cancellationToken) =>
        _reader is OpenXmlWorkbookReader openXmlReader
            ? openXmlReader.ReadValueCellsAsync(filePath, worksheetName, cancellationToken)
            : _reader.ReadCellsAsync(filePath, worksheetName, cancellationToken);

    private static IReadOnlyList<ColumnProfile> BuildColumnProfiles(
        SheetSample sample,
        CancellationToken cancellationToken)
    {
        var columns = sample.Header.Keys
            .Concat(sample.Rows.SelectMany(static row => row.Cells.Keys))
            .Distinct()
            .Order()
            .ToArray();
        var profiles = new List<ColumnProfile>(columns.Length);
        foreach (var column in columns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var counts = new Dictionary<SampleValue, int>();
            var nonEmpty = 0;
            foreach (var row in sample.Rows)
            {
                if (!row.Cells.TryGetValue(column, out var value) || !value.IsUsable)
                {
                    continue;
                }

                nonEmpty++;
                counts.TryGetValue(value, out var count);
                counts[value] = count + 1;
            }

            sample.Header.TryGetValue(column, out var header);
            profiles.Add(new ColumnProfile(
                column,
                header,
                nonEmpty,
                sample.Rows.Count == 0 ? 0d : (double)nonEmpty / sample.Rows.Count,
                nonEmpty == 0 ? 0d : (double)counts.Count / nonEmpty,
                counts));
        }

        return profiles;
    }

    private static IReadOnlyList<PairAnalysis> AnalyzeColumnPairs(
        IReadOnlyList<ColumnProfile> leftProfiles,
        IReadOnlyList<ColumnProfile> rightProfiles,
        CancellationToken cancellationToken)
    {
        var pairs = new List<PairAnalysis>(leftProfiles.Count * rightProfiles.Count);
        var processed = 0;
        foreach (var left in leftProfiles)
        {
            foreach (var right in rightProfiles)
            {
                if ((processed++ & 255) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var shared = left.Counts.Keys.Intersect(right.Counts.Keys).ToArray();
                var denominator = Math.Max(left.Counts.Count, right.Counts.Count);
                var crossCoverage = denominator == 0 ? 0d : (double)shared.Length / denominator;
                var headerMatches = left.Header.IsUsable && left.Header.Equals(right.Header);
                var confidence =
                    crossCoverage * 0.55d +
                    Math.Min(left.Coverage, right.Coverage) * 0.20d +
                    Math.Min(left.Uniqueness, right.Uniqueness) * 0.20d +
                    (headerMatches ? 0.05d : 0d);
                pairs.Add(new PairAnalysis(
                    left,
                    right,
                    crossCoverage,
                    headerMatches,
                    confidence,
                    shared));
            }
        }

        return pairs
            .OrderByDescending(static pair => pair.Confidence)
            .ThenBy(static pair => pair.Left.Column)
            .ThenBy(static pair => pair.Right.Column)
            .ToArray();
    }

    private static IReadOnlyList<AlignmentSuggestion> BuildKeySuggestions(
        SheetSample left,
        SheetSample right,
        IReadOnlyList<PairAnalysis> pairs,
        AlignmentSuggestionOptions options,
        CancellationToken cancellationToken)
    {
        var suggestions = new List<AlignmentSuggestion>();
        var safeSingles = pairs.Where(IsSafeSingleKey).Take(8).ToArray();
        foreach (var pair in safeSingles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            suggestions.Add(CreateSingleSuggestion(
                AlignmentSuggestionKind.KeyColumns,
                pair,
                left,
                right,
                options,
                canApply: true,
                titlePrefix: "关键列建议"));
        }

        var compositeBases = pairs
            .Where(static pair =>
                pair.Left.NonEmpty >= 2 &&
                pair.Right.NonEmpty >= 2 &&
                Math.Min(pair.Left.Coverage, pair.Right.Coverage) >= 0.70d &&
                pair.CrossCoverage >= 0.50d)
            .Take(CompositePairCandidateLimit)
            .ToArray();
        var compositeIds = new HashSet<string>(StringComparer.Ordinal);
        for (var firstIndex = 0; firstIndex < compositeBases.Length; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < compositeBases.Length; secondIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var first = compositeBases[firstIndex];
                var second = compositeBases[secondIndex];
                if (first.Left.Column == second.Left.Column ||
                    first.Right.Column == second.Right.Column ||
                    IsSafeSingleKey(first) ||
                    IsSafeSingleKey(second))
                {
                    continue;
                }

                var ordered = new[] { first, second }
                    .OrderBy(static pair => pair.Left.Column)
                    .ToArray();
                var id = CreateId(
                    AlignmentSuggestionKind.KeyColumns,
                    ordered.Select(static pair => pair.Left.Column),
                    ordered.Select(static pair => pair.Right.Column));
                if (!compositeIds.Add(id))
                {
                    continue;
                }

                var leftStats = AnalyzeComposite(
                    left,
                    ordered.Select(static pair => pair.Left.Column).ToArray(),
                    cancellationToken);
                var rightStats = AnalyzeComposite(
                    right,
                    ordered.Select(static pair => pair.Right.Column).ToArray(),
                    cancellationToken);
                var sharedKeys = leftStats.Keys.Keys.Intersect(rightStats.Keys.Keys).ToArray();
                var denominator = Math.Max(leftStats.Keys.Count, rightStats.Keys.Count);
                var crossCoverage = denominator == 0 ? 0d : (double)sharedKeys.Length / denominator;
                if (!IsSafeCompositeKey(leftStats, rightStats, crossCoverage))
                {
                    continue;
                }

                var leftColumns = ordered
                    .Select(static pair => CellReferenceUtility.ToColumnName(pair.Left.Column))
                    .ToArray();
                var rightColumns = ordered
                    .Select(static pair => CellReferenceUtility.ToColumnName(pair.Right.Column))
                    .ToArray();
                var confidence =
                    crossCoverage * 0.40d +
                    Math.Min(leftStats.Coverage, rightStats.Coverage) * 0.25d +
                    Math.Min(leftStats.Uniqueness, rightStats.Uniqueness) * 0.35d;
                suggestions.Add(new AlignmentSuggestion(
                    id,
                    AlignmentSuggestionKind.KeyColumns,
                    $"复合关键列建议：{string.Join(" + ", leftColumns)} ↔ {string.Join(" + ", rightColumns)}",
                    Percent(confidence),
                    BuildReason(leftStats, rightStats, crossCoverage),
                    leftColumns,
                    rightColumns,
                    ordered.Select(static pair => new ColumnPair(
                        CellReferenceUtility.ToColumnName(pair.Left.Column),
                        CellReferenceUtility.ToColumnName(pair.Right.Column))).ToArray(),
                    left.HeaderRow,
                    right.HeaderRow,
                    left.Rows.Count,
                    right.Rows.Count,
                    Percent(leftStats.Coverage),
                    Percent(rightStats.Coverage),
                    Percent(leftStats.Uniqueness),
                    Percent(rightStats.Uniqueness),
                    Percent(crossCoverage),
                    sharedKeys
                        .Order(StringComparer.Ordinal)
                        .Take(options.MaxSamples)
                        .Select(key => leftStats.Keys[key])
                        .ToArray(),
                    true));
                if (suggestions.Count >= 12)
                {
                    return suggestions;
                }
            }
        }

        return suggestions;
    }

    private static bool IsSafeSingleKey(PairAnalysis pair) =>
        pair.Left.NonEmpty >= 2 &&
        pair.Right.NonEmpty >= 2 &&
        pair.Left.Coverage == 1d &&
        pair.Right.Coverage == 1d &&
        pair.Left.Uniqueness == 1d &&
        pair.Right.Uniqueness == 1d &&
        pair.CrossCoverage >= 0.80d;

    private static bool IsSafeCompositeKey(
        CompositeStats left,
        CompositeStats right,
        double crossCoverage) =>
        left.NonEmpty >= 2 &&
        right.NonEmpty >= 2 &&
        left.Coverage == 1d &&
        right.Coverage == 1d &&
        left.Uniqueness == 1d &&
        right.Uniqueness == 1d &&
        crossCoverage >= 0.80d;

    private static IReadOnlyList<AlignmentSuggestion> BuildMappingSuggestions(
        SheetSample left,
        SheetSample right,
        IReadOnlyList<PairAnalysis> pairs,
        AlignmentSuggestionOptions options,
        CancellationToken cancellationToken)
    {
        var suggestions = new List<AlignmentSuggestion>();
        var usedLeft = new HashSet<int>();
        var usedRight = new HashSet<int>();
        foreach (var pair in pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pair.Left.Column == pair.Right.Column ||
                pair.Left.NonEmpty < 2 ||
                pair.Right.NonEmpty < 2 ||
                pair.Left.Counts.Count < 2 ||
                pair.Right.Counts.Count < 2 ||
                Math.Min(pair.Left.Coverage, pair.Right.Coverage) < 0.50d ||
                pair.CrossCoverage < 0.60d ||
                (!pair.HeaderMatches && Math.Min(pair.Left.Uniqueness, pair.Right.Uniqueness) < 0.80d) ||
                usedLeft.Contains(pair.Left.Column) ||
                usedRight.Contains(pair.Right.Column))
            {
                continue;
            }

            usedLeft.Add(pair.Left.Column);
            usedRight.Add(pair.Right.Column);
            suggestions.Add(CreateSingleSuggestion(
                AlignmentSuggestionKind.ColumnMapping,
                pair,
                left,
                right,
                options,
                canApply: true,
                titlePrefix: "异位列映射建议"));
            if (suggestions.Count >= 8)
            {
                break;
            }
        }

        return suggestions;
    }

    private static IReadOnlyList<AlignmentSuggestion> BuildGroupingSuggestions(
        SheetSample left,
        SheetSample right,
        IReadOnlyList<PairAnalysis> pairs,
        AlignmentSuggestionOptions options,
        CancellationToken cancellationToken)
    {
        var suggestions = new List<AlignmentSuggestion>();
        var usedLeft = new HashSet<int>();
        var usedRight = new HashSet<int>();
        foreach (var pair in pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leftDistinct = pair.Left.Counts.Count;
            var rightDistinct = pair.Right.Counts.Count;
            if (pair.Left.NonEmpty < 4 ||
                pair.Right.NonEmpty < 4 ||
                Math.Min(pair.Left.Coverage, pair.Right.Coverage) < 0.70d ||
                pair.CrossCoverage < 0.60d ||
                leftDistinct is < 2 or > 50 ||
                rightDistinct is < 2 or > 50 ||
                pair.Left.Uniqueness > 0.50d ||
                pair.Right.Uniqueness > 0.50d ||
                usedLeft.Contains(pair.Left.Column) ||
                usedRight.Contains(pair.Right.Column))
            {
                continue;
            }

            usedLeft.Add(pair.Left.Column);
            usedRight.Add(pair.Right.Column);
            suggestions.Add(CreateSingleSuggestion(
                AlignmentSuggestionKind.GroupingColumn,
                pair,
                left,
                right,
                options,
                canApply: false,
                titlePrefix: "分组列参考"));
            if (suggestions.Count >= 6)
            {
                break;
            }
        }

        return suggestions;
    }

    private static AlignmentSuggestion CreateSingleSuggestion(
        AlignmentSuggestionKind kind,
        PairAnalysis pair,
        SheetSample left,
        SheetSample right,
        AlignmentSuggestionOptions options,
        bool canApply,
        string titlePrefix)
    {
        var leftColumn = CellReferenceUtility.ToColumnName(pair.Left.Column);
        var rightColumn = CellReferenceUtility.ToColumnName(pair.Right.Column);
        return new AlignmentSuggestion(
            CreateId(kind, [pair.Left.Column], [pair.Right.Column]),
            kind,
            $"{titlePrefix}：{leftColumn} ↔ {rightColumn}",
            Percent(pair.Confidence),
            BuildReason(pair.Left, pair.Right, pair.CrossCoverage),
            [leftColumn],
            [rightColumn],
            [new ColumnPair(leftColumn, rightColumn)],
            left.HeaderRow,
            right.HeaderRow,
            left.Rows.Count,
            right.Rows.Count,
            Percent(pair.Left.Coverage),
            Percent(pair.Right.Coverage),
            Percent(pair.Left.Uniqueness),
            Percent(pair.Right.Uniqueness),
            Percent(pair.CrossCoverage),
            pair.SharedValues
                .OrderBy(static value => value.Kind)
                .ThenBy(static value => value.RawValue, StringComparer.Ordinal)
                .Take(options.MaxSamples)
                .Select(FormatValue)
                .ToArray(),
            canApply);
    }

    private static CompositeStats AnalyzeComposite(
        SheetSample sample,
        IReadOnlyList<int> columns,
        CancellationToken cancellationToken)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        var nonEmpty = 0;
        foreach (var row in sample.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new SampleValue[columns.Count];
            var complete = true;
            for (var index = 0; index < columns.Count; index++)
            {
                if (!row.Cells.TryGetValue(columns[index], out var value) || !value.IsUsable)
                {
                    complete = false;
                    break;
                }
                values[index] = value;
            }
            if (!complete)
            {
                continue;
            }

            nonEmpty++;
            var encoded = string.Concat(values.Select(static value =>
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{(int)value.Kind}:{value.RawValue!.Length}:{value.RawValue};")));
            keys.TryAdd(encoded, string.Join(" | ", values.Select(FormatValue)));
        }

        return new CompositeStats(
            nonEmpty,
            sample.Rows.Count == 0 ? 0d : (double)nonEmpty / sample.Rows.Count,
            nonEmpty == 0 ? 0d : (double)keys.Count / nonEmpty,
            keys);
    }

    private static string BuildReason(ColumnProfile left, ColumnProfile right, double crossCoverage) =>
        BuildReason(
            new MetricSnapshot(left.Coverage, left.Uniqueness),
            new MetricSnapshot(right.Coverage, right.Uniqueness),
            crossCoverage);

    private static string BuildReason(CompositeStats left, CompositeStats right, double crossCoverage) =>
        BuildReason(
            new MetricSnapshot(left.Coverage, left.Uniqueness),
            new MetricSnapshot(right.Coverage, right.Uniqueness),
            crossCoverage);

    private static string BuildReason(MetricSnapshot left, MetricSnapshot right, double crossCoverage) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"左侧非空覆盖 {Percent(left.Coverage):0.0}%、唯一率 {Percent(left.Uniqueness):0.0}%；" +
            $"右侧非空覆盖 {Percent(right.Coverage):0.0}%、唯一率 {Percent(right.Uniqueness):0.0}%；" +
            $"跨侧精确覆盖 {Percent(crossCoverage):0.0}%。仅按保存值、值类型、大小写和空白精确分析，未做模糊匹配。");

    private static string CreateId(
        AlignmentSuggestionKind kind,
        IEnumerable<int> leftColumns,
        IEnumerable<int> rightColumns) =>
        $"{kind}:{string.Join("+", leftColumns.Select(CellReferenceUtility.ToColumnName))}=" +
        string.Join("+", rightColumns.Select(CellReferenceUtility.ToColumnName));

    private static double Percent(double ratio) => Math.Round(Math.Clamp(ratio, 0d, 1d) * 100d, 1);

    private static string FormatValue(SampleValue value)
    {
        var kind = value.Kind switch
        {
            CellValueKind.Number => "数字",
            CellValueKind.Text => "文本",
            CellValueKind.Boolean => "布尔",
            CellValueKind.Error => "错误",
            CellValueKind.Date => "日期",
            _ => "空值",
        };
        var visible = MakeWhitespaceVisible(value.RawValue ?? string.Empty);
        return $"{kind}：{visible}";
    }

    private static string MakeWhitespaceVisible(string value)
    {
        var visible = value
            .Replace(" ", "␠", StringComparison.Ordinal)
            .Replace("\t", "⇥", StringComparison.Ordinal)
            .Replace("\r", "␍", StringComparison.Ordinal)
            .Replace("\n", "␊", StringComparison.Ordinal);
        return visible.Length <= 80 ? visible : visible[..77] + "...";
    }

    private readonly record struct SampleValue(CellValueKind Kind, string? RawValue)
    {
        public bool IsUsable =>
            Kind is not (CellValueKind.Blank or CellValueKind.Error) &&
            !string.IsNullOrEmpty(RawValue);
    }

    private sealed record SampleRow(IReadOnlyDictionary<int, SampleValue> Cells);

    private sealed record SheetSample(
        int HeaderRow,
        IReadOnlyDictionary<int, SampleValue> Header,
        IReadOnlyList<SampleRow> Rows,
        bool RowsTruncated,
        bool ColumnsTruncated);

    private sealed record ColumnProfile(
        int Column,
        SampleValue Header,
        int NonEmpty,
        double Coverage,
        double Uniqueness,
        IReadOnlyDictionary<SampleValue, int> Counts);

    private sealed record PairAnalysis(
        ColumnProfile Left,
        ColumnProfile Right,
        double CrossCoverage,
        bool HeaderMatches,
        double Confidence,
        IReadOnlyList<SampleValue> SharedValues);

    private sealed record CompositeStats(
        int NonEmpty,
        double Coverage,
        double Uniqueness,
        IReadOnlyDictionary<string, string> Keys);

    private readonly record struct MetricSnapshot(double Coverage, double Uniqueness);
}
