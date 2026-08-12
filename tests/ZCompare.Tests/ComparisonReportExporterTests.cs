using System.IO.Compression;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ZCompare.Core;
using ZCompare.Tests.Fixtures;

namespace ZCompare.Tests;

public sealed class ComparisonReportExporterTests
{
    [Fact]
    public async Task WorkbookJsonContainsSummaryWorksheetAndDifferenceRows()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var outputPath = temporaryDirectory.File("report.json");

        await ComparisonReportExporter.ExportAsync(
            WorkbookResult(temporaryDirectory),
            outputPath,
            ComparisonReportFormat.Json);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
        var root = document.RootElement;
        Assert.Equal("0.1.0", root.GetProperty("ZCompareVersion").GetString());
        Assert.Equal("Different", root.GetProperty("Status").GetString());
        Assert.Equal(1, root.GetProperty("DifferenceCount").GetInt32());
        var worksheet = Assert.Single(root.GetProperty("Worksheets").EnumerateArray());
        Assert.Equal("Data", worksheet.GetProperty("WorksheetName").GetString());
        Assert.Equal(1, worksheet.GetProperty("CellDifferenceCount").GetInt32());
        var difference = Assert.Single(root.GetProperty("Differences").EnumerateArray());
        Assert.Equal("B2", difference.GetProperty("CellReference").GetString());
        Assert.Equal("Value", difference.GetProperty("Kind").GetString());
    }

    [Fact]
    public async Task WorkbookXlsxIsValidArchiveWithExpectedSheetsAndRows()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var outputPath = temporaryDirectory.File("report.xlsx");

        await ComparisonReportExporter.ExportAsync(
            WorkbookResult(temporaryDirectory),
            outputPath,
            ComparisonReportFormat.Xlsx);

        using (var archive = ZipFile.OpenRead(outputPath))
        {
            Assert.Contains(archive.Entries, entry => entry.FullName == "[Content_Types].xml");
            Assert.Contains(archive.Entries, entry => entry.FullName == "xl/workbook.xml");
        }

        using var document = SpreadsheetDocument.Open(outputPath, isEditable: false);
        Assert.Equal(
            new[] { "摘要", "工作表", "差异", "警告" },
            SheetNames(document));
        Assert.Equal(10, RowCount(document, "摘要"));
        Assert.Equal(2, RowCount(document, "工作表"));
        Assert.Equal(2, RowCount(document, "差异"));
        Assert.Equal(2, RowCount(document, "警告"));
    }

    [Fact]
    public async Task WorkbookJsonPreservesActualSheetsMappingsAndNonTrivialRowAlignments()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var outputPath = temporaryDirectory.File("mapped-report.json");

        await ComparisonReportExporter.ExportAsync(
            MappedWorkbookResult(temporaryDirectory),
            outputPath,
            ComparisonReportFormat.Json);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
        var worksheets = document.RootElement.GetProperty("Worksheets").EnumerateArray().ToArray();
        Assert.Equal(2, worksheets.Length);
        var mapped = worksheets[0];
        Assert.Equal("MappedData", mapped.GetProperty("WorksheetName").GetString());
        Assert.Equal("LeftData", mapped.GetProperty("LeftWorksheetName").GetString());
        Assert.Equal("RightData", mapped.GetProperty("RightWorksheetName").GetString());
        var mapping = Assert.Single(mapped.GetProperty("AppliedColumnPairs").EnumerateArray());
        Assert.Equal("A", mapping.GetProperty("LeftColumnIdentifier").GetString());
        Assert.Equal("C", mapping.GetProperty("RightColumnIdentifier").GetString());
        var alignment = Assert.Single(mapped.GetProperty("RowAlignments").EnumerateArray());
        Assert.Equal("Inserted", alignment.GetProperty("Status").GetString());
        Assert.Equal(2, alignment.GetProperty("DisplayRow").GetInt32());
        Assert.Equal(JsonValueKind.Null, alignment.GetProperty("LeftRow").ValueKind);
        Assert.Equal(2, alignment.GetProperty("RightRow").GetInt32());

        var oneSided = worksheets[1];
        Assert.Equal("OnlyLeft", oneSided.GetProperty("LeftWorksheetName").GetString());
        Assert.Equal(JsonValueKind.Null, oneSided.GetProperty("RightWorksheetName").ValueKind);

        var valueDifference = document.RootElement.GetProperty("Differences")
            .EnumerateArray()
            .Single(difference => difference.GetProperty("Kind").GetString() == "Value");
        Assert.Equal("LeftData", valueDifference.GetProperty("LeftWorksheetName").GetString());
        Assert.Equal("RightData", valueDifference.GetProperty("RightWorksheetName").GetString());
    }

    [Fact]
    public async Task WorkbookXlsxIncludesActualSheetsMappingsAndRowAlignmentTable()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var outputPath = temporaryDirectory.File("mapped-report.xlsx");

        await ComparisonReportExporter.ExportAsync(
            MappedWorkbookResult(temporaryDirectory),
            outputPath,
            ComparisonReportFormat.Xlsx);

        using var document = SpreadsheetDocument.Open(outputPath, isEditable: false);
        Assert.Equal(
            new[] { "摘要", "工作表", "行对齐", "差异" },
            SheetNames(document));
        var worksheets = SheetRows(document, "工作表");
        Assert.Equal(3, worksheets.Length);
        Assert.Equal("LeftData", worksheets[1][1]);
        Assert.Equal("RightData", worksheets[1][2]);
        Assert.Equal("A=C", worksheets[1][9]);
        Assert.Equal("OnlyLeft", worksheets[2][1]);
        Assert.Equal(string.Empty, worksheets[2][2]);

        var alignments = SheetRows(document, "行对齐");
        Assert.Equal(2, alignments.Length);
        Assert.Equal("MappedData", alignments[1][0]);
        Assert.Equal(string.Empty, alignments[1][4]);
        Assert.Equal("2", alignments[1][5]);
        Assert.Equal("Inserted", alignments[1][6]);

        var differences = SheetRows(document, "差异");
        Assert.Equal("左侧实际工作表", differences[0][2]);
        Assert.Equal("右侧实际工作表", differences[0][3]);
        Assert.Equal("LeftData", differences[1][2]);
        Assert.Equal("RightData", differences[1][3]);
    }

    [Theory]
    [InlineData(ComparisonReportFormat.Json, "folder.json")]
    [InlineData(ComparisonReportFormat.Xlsx, "folder.xlsx")]
    public async Task FolderReportContainsEveryFile(
        ComparisonReportFormat format,
        string fileName)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var result = FolderResult(temporaryDirectory);
        var outputPath = temporaryDirectory.File(fileName);

        await ComparisonReportExporter.ExportAsync(result, outputPath, format);

        if (format == ComparisonReportFormat.Json)
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
            Assert.Equal(2, document.RootElement.GetProperty("FileCount").GetInt32());
            Assert.Equal(1, document.RootElement.GetProperty("DifferentFileCount").GetInt32());
            Assert.Equal(0, document.RootElement.GetProperty("ErrorFileCount").GetInt32());
            Assert.Equal(2, document.RootElement.GetProperty("Files").GetArrayLength());
        }
        else
        {
            using var document = SpreadsheetDocument.Open(outputPath, isEditable: false);
            Assert.Equal(new[] { "摘要", "文件" }, SheetNames(document));
            Assert.Equal(11, RowCount(document, "摘要"));
            Assert.Equal(3, RowCount(document, "文件"));
        }
    }

    [Theory]
    [InlineData(ComparisonReportFormat.Json, "pending-folder.json")]
    [InlineData(ComparisonReportFormat.Xlsx, "pending-folder.xlsx")]
    public async Task PendingFilesAreNotCountedAsDifferentFiles(
        ComparisonReportFormat format,
        string fileName)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var result = new FolderCompareResult(
            temporaryDirectory.Directory("pending-left"),
            temporaryDirectory.Directory("pending-right"),
            ComparisonStatus.Pending,
            [
                new FolderFileResult("same.xlsx", "left-same.xlsx", "right-same.xlsx", ComparisonStatus.Same, 0, null, null),
                new FolderFileResult("waiting.xlsx", "left-waiting.xlsx", "right-waiting.xlsx", ComparisonStatus.Pending, 0, null, null),
            ],
            TimeSpan.Zero);
        var outputPath = temporaryDirectory.File(fileName);

        await ComparisonReportExporter.ExportAsync(result, outputPath, format);

        if (format == ComparisonReportFormat.Json)
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
            Assert.Equal(0, document.RootElement.GetProperty("DifferentFileCount").GetInt32());
        }
        else
        {
            using var document = SpreadsheetDocument.Open(outputPath, isEditable: false);
            Assert.Equal("0", SheetRows(document, "摘要")[6][1]);
        }
    }

    [Theory]
    [InlineData(ComparisonReportFormat.Json, "cancelled.json")]
    [InlineData(ComparisonReportFormat.Xlsx, "cancelled.xlsx")]
    public async Task PreCancelledExportPreservesExistingOutputAndRemovesTemporaryFile(
        ComparisonReportFormat format,
        string fileName)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var outputPath = temporaryDirectory.File(fileName);
        await File.WriteAllTextAsync(outputPath, "existing report");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ComparisonReportExporter.ExportAsync(
                WorkbookResult(temporaryDirectory),
                outputPath,
                format,
                cancellation.Token));

        Assert.Equal("existing report", await File.ReadAllTextAsync(outputPath));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(temporaryDirectory.Path),
            path => Path.GetFileName(path).StartsWith(fileName + ".", StringComparison.Ordinal) &&
                path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ComparisonReportFormat.Json)]
    [InlineData(ComparisonReportFormat.Xlsx)]
    public async Task ExportCannotOverwriteComparedSource(ComparisonReportFormat format)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var result = WorkbookResult(temporaryDirectory);
        await File.WriteAllTextAsync(result.LeftPath, "source workbook bytes");
        var originalBytes = await File.ReadAllBytesAsync(result.LeftPath);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ComparisonReportExporter.ExportAsync(result, result.LeftPath, format));

        Assert.Contains("不能覆盖", exception.Message, StringComparison.Ordinal);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(result.LeftPath));
    }

    [Theory]
    [InlineData(ComparisonReportFormat.Json)]
    [InlineData(ComparisonReportFormat.Xlsx)]
    public async Task FolderExportCannotOverwriteAnyComparedWorkbook(ComparisonReportFormat format)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var sourcePath = temporaryDirectory.File("source.xlsx");
        await File.WriteAllTextAsync(sourcePath, "source workbook bytes");
        var originalBytes = await File.ReadAllBytesAsync(sourcePath);
        var result = new FolderCompareResult(
            temporaryDirectory.Directory("folder-left"),
            temporaryDirectory.Directory("folder-right"),
            ComparisonStatus.Same,
            [new FolderFileResult("source.xlsx", sourcePath, "right.xlsx", ComparisonStatus.Same, 0, null, null)],
            TimeSpan.Zero);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ComparisonReportExporter.ExportAsync(result, sourcePath, format));

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sourcePath));
    }

    private static WorkbookCompareResult WorkbookResult(TemporaryDirectory temporaryDirectory)
    {
        var difference = new Difference(
            DifferenceKind.Value,
            "Data",
            "B2",
            "保存值不同。",
            null,
            null,
            "left",
            "right");
        return new WorkbookCompareResult(
            temporaryDirectory.File("left.xlsx"),
            temporaryDirectory.File("right.xlsx"),
            ComparisonStatus.Different,
            [new WorksheetCompareResult("Data", ComparisonStatus.Different, 1, [difference], 10, 11)],
            [],
            ["公式缓存可能过期。"],
            false,
            new string('A', 64),
            new string('B', 64),
            TimeSpan.FromSeconds(1));
    }

    private static WorkbookCompareResult MappedWorkbookResult(TemporaryDirectory temporaryDirectory)
    {
        var left = Snapshot("LeftData", "A1", "left");
        var right = Snapshot("RightData", "C1", "right");
        var valueDifference = new Difference(
            DifferenceKind.Value,
            "MappedData",
            "A1",
            "保存值不同。",
            left,
            right,
            "left",
            "right");
        var removedDifference = new Difference(
            DifferenceKind.WorksheetRemoved,
            "OnlyLeft",
            null,
            "工作表仅存在于左侧。",
            null,
            null,
            "OnlyLeft",
            null);
        return new WorkbookCompareResult(
            temporaryDirectory.File("mapped-left.xlsx"),
            temporaryDirectory.File("mapped-right.xlsx"),
            ComparisonStatus.Different,
            [
                new WorksheetCompareResult(
                    "MappedData",
                    ComparisonStatus.Different,
                    1,
                    [valueDifference],
                    1,
                    1,
                    [
                        new RowAlignment(1, 1, 1, RowAlignmentStatus.Matched),
                        new RowAlignment(2, null, 2, RowAlignmentStatus.Inserted, "右侧插入行。"),
                    ],
                    1,
                    2,
                    "LeftData",
                    "RightData",
                    [new ColumnPair("A", "C")]),
                new WorksheetCompareResult(
                    "OnlyLeft",
                    ComparisonStatus.LeftOnly,
                    1,
                    [removedDifference],
                    0,
                    0,
                    LeftWorksheetName: "OnlyLeft",
                    RightWorksheetName: null),
            ],
            [],
            [],
            false,
            new string('C', 64),
            new string('D', 64),
            TimeSpan.Zero);
    }

    private static CellSnapshot Snapshot(string worksheetName, string address, string value) => new(
        worksheetName,
        address,
        CellValueKind.Text,
        value,
        value,
        value,
        null,
        FormulaKind.None,
        null,
        null,
        null,
        null,
        null,
        false,
        false);

    private static FolderCompareResult FolderResult(TemporaryDirectory temporaryDirectory) => new(
        temporaryDirectory.Directory("left-folder"),
        temporaryDirectory.Directory("right-folder"),
        ComparisonStatus.Different,
        [
            new FolderFileResult("same.xlsx", "left-same.xlsx", "right-same.xlsx", ComparisonStatus.Same, 0, null, null),
            new FolderFileResult("new.xlsx", null, "right-new.xlsx", ComparisonStatus.RightOnly, 1, null, null),
        ],
        TimeSpan.FromSeconds(2));

    private static string[] SheetNames(SpreadsheetDocument document)
    {
        var workbook = document.WorkbookPart?.Workbook ??
            throw new InvalidDataException("报告缺少工作簿部件。");
        return workbook.GetFirstChild<Sheets>()!
            .Elements<Sheet>()
            .Select(static sheet => sheet.Name!.Value!)
            .ToArray();
    }

    private static int RowCount(SpreadsheetDocument document, string sheetName)
    {
        var workbookPart = document.WorkbookPart ??
            throw new InvalidDataException("报告缺少工作簿部件。");
        var workbook = workbookPart.Workbook ??
            throw new InvalidDataException("报告缺少工作簿内容。");
        var sheet = workbook.GetFirstChild<Sheets>()!
            .Elements<Sheet>()
            .Single(sheet => string.Equals(sheet.Name?.Value, sheetName, StringComparison.Ordinal));
        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);
        return (worksheetPart.Worksheet ?? throw new InvalidDataException("报告缺少工作表内容。"))
            .Descendants<Row>()
            .Count();
    }

    private static string[][] SheetRows(SpreadsheetDocument document, string sheetName)
    {
        var workbookPart = document.WorkbookPart ??
            throw new InvalidDataException("报告缺少工作簿部件。");
        var workbook = workbookPart.Workbook ??
            throw new InvalidDataException("报告缺少工作簿内容。");
        var sheet = workbook.GetFirstChild<Sheets>()!
            .Elements<Sheet>()
            .Single(sheet => string.Equals(sheet.Name?.Value, sheetName, StringComparison.Ordinal));
        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);
        return (worksheetPart.Worksheet ?? throw new InvalidDataException("报告缺少工作表内容。"))
            .Descendants<Row>()
            .Select(static row => row.Elements<Cell>()
                .Select(static cell => cell.InlineString?.InnerText ?? cell.CellValue?.Text ?? string.Empty)
                .ToArray())
            .ToArray();
    }
}
