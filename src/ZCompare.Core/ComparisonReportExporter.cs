using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Reflection;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace ZCompare.Core;

public enum ComparisonReportFormat
{
    Json,
    Xlsx,
}

public static class ComparisonReportExporter
{
    private const int ExcelMaximumRows = 1_048_576;
    private const int ExcelMaximumCellTextLength = 32_767;
    private static readonly string ProductVersion =
        typeof(ComparisonReportExporter).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Task ExportAsync(
        WorkbookCompareResult result,
        string outputPath,
        ComparisonReportFormat format,
        CancellationToken cancellationToken = default)
    {
        EnsureOutputIsNotSource(outputPath, [result.LeftPath, result.RightPath]);
        return format switch
        {
            ComparisonReportFormat.Json => WriteJsonAtomicallyAsync(
                CreateWorkbookDocument(result),
                outputPath,
                cancellationToken),
            ComparisonReportFormat.Xlsx => WriteXlsxAtomicallyAsync(
                outputPath,
                builder => WriteWorkbookReport(builder, result, cancellationToken),
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "未知的报告格式。"),
        };
    }

    public static Task ExportAsync(
        FolderCompareResult result,
        string outputPath,
        ComparisonReportFormat format,
        CancellationToken cancellationToken = default)
    {
        EnsureOutputIsNotSource(
            outputPath,
            result.Files.SelectMany(static file => new[] { file.LeftPath, file.RightPath })
                .OfType<string>());
        return format switch
        {
            ComparisonReportFormat.Json => WriteJsonAtomicallyAsync(
                CreateFolderDocument(result),
                outputPath,
                cancellationToken),
            ComparisonReportFormat.Xlsx => WriteXlsxAtomicallyAsync(
                outputPath,
                builder => WriteFolderReport(builder, result, cancellationToken),
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "未知的报告格式。"),
        };
    }

    public static Task ExportFatalErrorAsync(
        string? leftPath,
        string? rightPath,
        string error,
        string outputPath,
        ComparisonReportFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        EnsureOutputIsNotSource(outputPath, new[] { leftPath, rightPath }.OfType<string>());
        return format switch
        {
            ComparisonReportFormat.Json => WriteJsonAtomicallyAsync(
                new FatalErrorReportDocument(ProductVersion, leftPath, rightPath, ComparisonStatus.Error, error),
                outputPath,
                cancellationToken),
            ComparisonReportFormat.Xlsx => WriteXlsxAtomicallyAsync(
                outputPath,
                builder => builder.AddSheet("错误", new[]
                {
                    Row("项目", "值"),
                    Row("ZCompare 版本", ProductVersion),
                    Row("左侧", leftPath),
                    Row("右侧", rightPath),
                    Row("状态", ComparisonStatus.Error.ToString()),
                    Row("错误", error),
                }, cancellationToken),
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "未知的报告格式。"),
        };
    }

    private static WorkbookReportDocument CreateWorkbookDocument(WorkbookCompareResult result) => new(
        ProductVersion,
        result.LeftPath,
        result.RightPath,
        result.Status,
        result.DifferenceCount,
        result.ByteIdentical,
        result.LeftSha256,
        result.RightSha256,
        result.Elapsed,
        result.Warnings,
        result.Worksheets.Select(static sheet => new WorksheetReportRow(
            sheet.WorksheetName,
            ReportLeftWorksheetName(sheet),
            ReportRightWorksheetName(sheet),
            sheet.Status,
            sheet.DifferenceCount,
            sheet.LeftCellCount,
            sheet.RightCellCount,
            sheet.RowDifferenceCount,
            sheet.CellDifferenceCount,
            sheet.AppliedColumnPairs,
            sheet.RowAlignments
                .Where(static row => row.Status is not (RowAlignmentStatus.Matched or RowAlignmentStatus.NotApplied))
                .Select(static row => new RowAlignmentReportRow(
                    row.DisplayRow,
                    row.LeftRow,
                    row.RightRow,
                    row.Status,
                    row.Message))
                .ToArray())).ToArray(),
        result.WorkbookDifferences
            .Concat(result.Worksheets.SelectMany(static sheet => sheet.Differences))
            .Select(CreateDifferenceRow)
            .ToArray());

    private static FolderReportDocument CreateFolderDocument(FolderCompareResult result) => new(
        ProductVersion,
        result.LeftDirectory,
        result.RightDirectory,
        result.Status,
        result.Files.Count,
        result.Files.Count(CountsAsDifferentFile),
        result.Files.Count(static file => file.Status == ComparisonStatus.Warning),
        result.ErrorFileCount,
        result.Files.Count(static file => file.Status == ComparisonStatus.Pending),
        result.Elapsed,
        result.Files.Select(static file => new FolderFileReportRow(
            file.RelativePath,
            file.LeftPath,
            file.RightPath,
            file.Status,
            file.DifferenceCount,
            file.Error)).ToArray());

    private static DifferenceReportRow CreateDifferenceRow(Difference difference) => new(
        difference.WorksheetName,
        difference.CellReference,
        difference.Left?.WorksheetName,
        difference.Right?.WorksheetName,
        difference.Left?.CellReference,
        difference.Right?.CellReference,
        difference.Kind,
        difference.Description,
        difference.LeftDetail,
        difference.RightDetail);

    private static async Task WriteJsonAtomicallyAsync<T>(
        T document,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var (fullPath, temporaryPath) = PrepareOutput(outputPath);
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static async Task WriteXlsxAtomicallyAsync(
        string outputPath,
        Action<WorkbookReportBuilder> write,
        CancellationToken cancellationToken)
    {
        var (fullPath, temporaryPath) = PrepareOutput(outputPath);
        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var document = SpreadsheetDocument.Create(
                    temporaryPath,
                    SpreadsheetDocumentType.Workbook,
                    autoSave: true);
                var workbookPart = document.AddWorkbookPart();
                workbookPart.Workbook = new Workbook(new Sheets());
                var builder = new WorkbookReportBuilder(workbookPart);
                write(builder);
                workbookPart.Workbook.Save();
            }, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static void WriteWorkbookReport(
        WorkbookReportBuilder builder,
        WorkbookCompareResult result,
        CancellationToken cancellationToken)
    {
        builder.AddSheet("摘要", new[]
        {
            Row("项目", "值"),
            Row("ZCompare 版本", ProductVersion),
            Row("左侧文件", result.LeftPath),
            Row("右侧文件", result.RightPath),
            Row("状态", result.Status.ToString()),
            Row("差异数量", result.DifferenceCount.ToString()),
            Row("字节相同", result.ByteIdentical ? "是" : "否"),
            Row("左侧 SHA-256", result.LeftSha256),
            Row("右侧 SHA-256", result.RightSha256),
            Row("耗时", result.Elapsed.ToString()),
        }, cancellationToken);

        builder.AddSheet(
            "工作表",
            Prepend(
                Row("工作表", "左侧实际工作表", "右侧实际工作表", "状态", "差异项", "左侧单元格", "右侧单元格", "差异行", "差异单元格", "列映射"),
                result.Worksheets.Select(sheet => Row(
                    sheet.WorksheetName,
                    ReportLeftWorksheetName(sheet),
                    ReportRightWorksheetName(sheet),
                    sheet.Status.ToString(),
                    sheet.DifferenceCount.ToString(),
                    sheet.LeftCellCount.ToString(),
                    sheet.RightCellCount.ToString(),
                    sheet.RowDifferenceCount.ToString(),
                    sheet.CellDifferenceCount.ToString(),
                    FormatColumnPairs(sheet.AppliedColumnPairs)))),
            cancellationToken);

        var alignmentRows = result.Worksheets.SelectMany(sheet => sheet.RowAlignments
            .Where(static row => row.Status is not (RowAlignmentStatus.Matched or RowAlignmentStatus.NotApplied))
            .Select(row => Row(
                sheet.WorksheetName,
                ReportLeftWorksheetName(sheet),
                ReportRightWorksheetName(sheet),
                row.DisplayRow.ToString(),
                row.LeftRow?.ToString(),
                row.RightRow?.ToString(),
                row.Status.ToString(),
                row.Message)))
            .ToArray();
        if (alignmentRows.Length > 0)
        {
            builder.AddPagedSheets(
                "行对齐",
                Row("工作表", "左侧实际工作表", "右侧实际工作表", "展示行", "左侧原行", "右侧原行", "状态", "说明"),
                alignmentRows,
                cancellationToken);
        }

        var differences = result.WorkbookDifferences
            .Concat(result.Worksheets.SelectMany(static worksheet => worksheet.Differences));
        builder.AddPagedSheets(
            "差异",
            Row("工作表", "地址", "左侧实际工作表", "右侧实际工作表", "左侧地址", "右侧地址", "类型", "说明", "左侧详情", "右侧详情"),
            differences.Select(difference => Row(
                difference.WorksheetName,
                difference.CellReference,
                difference.Left?.WorksheetName,
                difference.Right?.WorksheetName,
                difference.Left?.CellReference,
                difference.Right?.CellReference,
                difference.Kind.ToString(),
                difference.Description,
                difference.LeftDetail,
                difference.RightDetail)),
            cancellationToken);

        if (result.Warnings.Count > 0)
        {
            builder.AddSheet(
                "警告",
                Prepend(Row("警告"), result.Warnings.Select(static warning => Row(warning))),
                cancellationToken);
        }
    }

    private static void WriteFolderReport(
        WorkbookReportBuilder builder,
        FolderCompareResult result,
        CancellationToken cancellationToken)
    {
        builder.AddSheet("摘要", new[]
        {
            Row("项目", "值"),
            Row("ZCompare 版本", ProductVersion),
            Row("左侧目录", result.LeftDirectory),
            Row("右侧目录", result.RightDirectory),
            Row("状态", result.Status.ToString()),
            Row("文件总数", result.Files.Count.ToString()),
            Row("差异文件", result.Files.Count(CountsAsDifferentFile).ToString()),
            Row("警告文件", result.Files.Count(static file => file.Status == ComparisonStatus.Warning).ToString()),
            Row("错误文件", result.ErrorFileCount.ToString()),
            Row("待比较文件", result.Files.Count(static file => file.Status == ComparisonStatus.Pending).ToString()),
            Row("耗时", result.Elapsed.ToString()),
        }, cancellationToken);

        builder.AddPagedSheets(
            "文件",
            Row("相对路径", "左侧路径", "右侧路径", "状态", "差异数量", "错误"),
            result.Files.Select(static file => Row(
                file.RelativePath,
                file.LeftPath,
                file.RightPath,
                file.Status.ToString(),
                file.DifferenceCount.ToString(),
                file.Error)),
            cancellationToken);
    }

    private static string?[] Row(params string?[] values) => values;

    private static string FormatColumnPairs(IReadOnlyList<ColumnPair> pairs) =>
        string.Join(",", pairs.Select(static pair =>
            $"{pair.LeftColumnIdentifier}={pair.RightColumnIdentifier}"));

    private static bool CountsAsDifferentFile(FolderFileResult file) =>
        file.Status is ComparisonStatus.Different or ComparisonStatus.LeftOnly or ComparisonStatus.RightOnly;

    private static string? ReportLeftWorksheetName(WorksheetCompareResult sheet) =>
        sheet.LeftWorksheetName ?? (sheet.RightWorksheetName is null ? sheet.WorksheetName : null);

    private static string? ReportRightWorksheetName(WorksheetCompareResult sheet) =>
        sheet.RightWorksheetName ?? (sheet.LeftWorksheetName is null ? sheet.WorksheetName : null);

    private static IEnumerable<string?[]> Prepend(string?[] header, IEnumerable<string?[]> rows)
    {
        yield return header;
        foreach (var row in rows)
        {
            yield return row;
        }
    }

    private static (string FullPath, string TemporaryPath) PrepareOutput(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return (fullPath, fullPath + $".{Guid.NewGuid():N}.tmp");
    }

    private static void EnsureOutputIsNotSource(string outputPath, IEnumerable<string> sourcePaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        if (sourcePaths.Any(sourcePath =>
            string.Equals(Path.GetFullPath(sourcePath), fullOutputPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("报告路径不能覆盖参与比较的源文件。");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class WorkbookReportBuilder(WorkbookPart workbookPart)
    {
        private uint _nextSheetId = 1;

        public void AddPagedSheets(
            string baseName,
            string?[] header,
            IEnumerable<string?[]> rows,
            CancellationToken cancellationToken)
        {
            using var enumerator = rows.GetEnumerator();
            var page = 1;
            var hasRow = enumerator.MoveNext();
            do
            {
                var currentPage = page;
                IEnumerable<string?[]> PageRows()
                {
                    yield return header;
                    var count = 1;
                    while (hasRow && count < ExcelMaximumRows)
                    {
                        yield return enumerator.Current;
                        count++;
                        hasRow = enumerator.MoveNext();
                    }
                }

                AddSheet(
                    currentPage == 1 ? baseName : $"{baseName} {currentPage}",
                    PageRows(),
                    cancellationToken);
                page++;
            }
            while (hasRow);
        }

        public void AddSheet(
            string requestedName,
            IEnumerable<string?[]> rows,
            CancellationToken cancellationToken)
        {
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            using (var writer = OpenXmlWriter.Create(worksheetPart))
            {
                writer.WriteStartElement(new Worksheet());
                writer.WriteStartElement(new SheetData());
                uint rowIndex = 1;
                foreach (var values in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.WriteStartElement(new Row { RowIndex = rowIndex++ });
                    foreach (var value in values)
                    {
                        writer.WriteElement(CreateCell(value));
                    }
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            var relationshipId = workbookPart.GetIdOfPart(worksheetPart);
            var workbook = workbookPart.Workbook ??
                throw new InvalidDataException("报告工作簿尚未初始化。");
            var sheets = workbook.GetFirstChild<Sheets>() ??
                throw new InvalidDataException("报告工作簿缺少工作表集合。");
            sheets.Append(new Sheet
            {
                Id = relationshipId,
                SheetId = _nextSheetId++,
                Name = MakeUniqueSheetName(sheets, requestedName),
            });
        }

        private static Cell CreateCell(string? value)
        {
            var sanitized = SanitizeCellText(value);
            return new Cell
            {
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new Text(sanitized)
                {
                    Space = SpaceProcessingModeValues.Preserve,
                }),
            };
        }

        private static string MakeUniqueSheetName(Sheets sheets, string requestedName)
        {
            var invalid = new HashSet<char>(['[', ']', ':', '*', '?', '/', '\\']);
            var cleaned = new string(requestedName.Where(character => !invalid.Contains(character)).ToArray());
            cleaned = string.IsNullOrWhiteSpace(cleaned) ? "报告" : cleaned;
            cleaned = cleaned[..Math.Min(31, cleaned.Length)];
            var existing = sheets.Elements<Sheet>()
                .Select(static sheet => sheet.Name?.Value ?? string.Empty)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (existing.Add(cleaned))
            {
                return cleaned;
            }

            for (var suffix = 2; ; suffix++)
            {
                var suffixText = $" ({suffix})";
                var prefixLength = Math.Min(cleaned.Length, 31 - suffixText.Length);
                var candidate = cleaned[..prefixLength] + suffixText;
                if (existing.Add(candidate))
                {
                    return candidate;
                }
            }
        }
    }

    private static string SanitizeCellText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sanitized = new string(value.Where(XmlConvert.IsXmlChar).ToArray());
        return sanitized.Length <= ExcelMaximumCellTextLength
            ? sanitized
            : sanitized[..ExcelMaximumCellTextLength];
    }

    private sealed record WorkbookReportDocument(
        string ZCompareVersion,
        string LeftPath,
        string RightPath,
        ComparisonStatus Status,
        int DifferenceCount,
        bool ByteIdentical,
        string LeftSha256,
        string RightSha256,
        TimeSpan Elapsed,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<WorksheetReportRow> Worksheets,
        IReadOnlyList<DifferenceReportRow> Differences);

    private sealed record WorksheetReportRow(
        string WorksheetName,
        string? LeftWorksheetName,
        string? RightWorksheetName,
        ComparisonStatus Status,
        int DifferenceCount,
        int LeftCellCount,
        int RightCellCount,
        int RowDifferenceCount,
        int CellDifferenceCount,
        IReadOnlyList<ColumnPair> AppliedColumnPairs,
        IReadOnlyList<RowAlignmentReportRow> RowAlignments);

    private sealed record RowAlignmentReportRow(
        int DisplayRow,
        int? LeftRow,
        int? RightRow,
        RowAlignmentStatus Status,
        string? Message);

    private sealed record DifferenceReportRow(
        string? WorksheetName,
        string? CellReference,
        string? LeftWorksheetName,
        string? RightWorksheetName,
        string? LeftCellReference,
        string? RightCellReference,
        DifferenceKind Kind,
        string Description,
        string? LeftDetail,
        string? RightDetail);

    private sealed record FolderReportDocument(
        string ZCompareVersion,
        string LeftDirectory,
        string RightDirectory,
        ComparisonStatus Status,
        int FileCount,
        int DifferentFileCount,
        int WarningFileCount,
        int ErrorFileCount,
        int PendingFileCount,
        TimeSpan Elapsed,
        IReadOnlyList<FolderFileReportRow> Files);

    private sealed record FatalErrorReportDocument(
        string ZCompareVersion,
        string? LeftPath,
        string? RightPath,
        ComparisonStatus Status,
        string Error);

    private sealed record FolderFileReportRow(
        string RelativePath,
        string? LeftPath,
        string? RightPath,
        ComparisonStatus Status,
        int DifferenceCount,
        string? Error);
}
