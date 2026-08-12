using ZCompare.Core;
using ZCompare.Tests.Fixtures;

namespace ZCompare.Tests;

public sealed class WorkbookValueComparisonTests : ComparisonTestBase
{
    [Fact]
    public async Task BlankAndSingleSpaceDifferenceExplainsAndVisualizesWhitespace()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet.Cell("A1", null)),
            builder => builder.AddSheet("Sheet1", sheet => sheet.Cell("A1", " ", TestCellType.InlineString)));

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right);
        var difference = DifferenceAt(result, DifferenceKind.Value, "A1");

        Assert.Contains("空白字符", difference.Description, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(difference.LeftDetail));
        Assert.False(string.IsNullOrWhiteSpace(difference.RightDetail));
        Assert.NotEqual(difference.LeftDetail, difference.RightDetail);
    }

    [Fact]
    public async Task ExplicitBlankAndMissingCellAreSemanticallySame()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet.Cell("A1", null)),
            builder => builder.AddSheet("Sheet1"));

        var result = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { CompareFormatting = false });

        Assert.Equal(ComparisonStatus.Same, result.Status);
        Assert.Empty(AllDifferences(result));
    }

    [Fact]
    public async Task TextComparisonPreservesCaseAndWhitespace()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet.Cell("A1", " ABC ", TestCellType.InlineString)),
            builder => builder.AddSheet("Sheet1", sheet => sheet.Cell("A1", "abc", TestCellType.InlineString)));

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right);

        DifferenceAt(result, DifferenceKind.Value, "A1");
    }

    [Fact]
    public async Task DistinguishesExactLongNumbersAndNumericFromText()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "9007199254740993")
                .Cell("B1", "1")),
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "9007199254740994")
                .Cell("B1", "1", TestCellType.InlineString)));

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right);

        Assert.Equal(ComparisonStatus.Different, result.Status);
        var longIdDifference = DifferenceAt(result, DifferenceKind.Value, "A1");
        Assert.Equal("9007199254740993", longIdDifference.Left?.RawValue);
        Assert.Equal("9007199254740994", longIdDifference.Right?.RawValue);
        Assert.Contains(AllDifferences(result), difference =>
            difference.Kind == DifferenceKind.CellType && difference.CellReference == "B1");
    }

    [Fact]
    public async Task ComparesBooleanErrorAndRichTextValues()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "1", TestCellType.Boolean)
                .Cell("A2", "#DIV/0!", TestCellType.Error)
                .Cell("A3", "unused", TestCellType.InlineString, richTextRuns: ["相", "同文本"])),
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "0", TestCellType.Boolean)
                .Cell("A2", "#N/A", TestCellType.Error)
                .Cell("A3", "不同文本", TestCellType.InlineString)));

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right);

        Assert.Contains(AllDifferences(result), difference => difference.Kind == DifferenceKind.Value && difference.CellReference == "A1");
        Assert.Contains(AllDifferences(result), difference => difference.Kind == DifferenceKind.Value && difference.CellReference == "A2");
        Assert.Contains(AllDifferences(result), difference => difference.Kind == DifferenceKind.Value && difference.CellReference == "A3");
    }

    [Fact]
    public async Task PhoneticGuideTextDoesNotChangeTheCellValue()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "Base", TestCellType.InlineString, phoneticText: "LeftGuide")),
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "Base", TestCellType.InlineString, phoneticText: "RightGuide")));

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right);
        var preview = await CreateReader().LoadWorksheetPreviewAsync(pair.Left, "Sheet1");

        Assert.Equal(ComparisonStatus.Same, result.Status);
        Assert.Equal("Base", preview.Cells["A1"].RawValue);
    }

    [Fact]
    public async Task FontComparisonDetectsRichTextRunFormatting()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet.Cell(
                "A1",
                "unused",
                TestCellType.InlineString,
                richTextRuns: ["Same"],
                boldFirstRichTextRun: true)),
            builder => builder.AddSheet("Sheet1", sheet => sheet.Cell(
                "A1",
                "unused",
                TestCellType.InlineString,
                richTextRuns: ["Same"],
                boldFirstRichTextRun: false)));

        var result = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { CompareFonts = true });

        DifferenceAt(result, DifferenceKind.Font, "A1");
    }

    [Fact]
    public async Task DateSystemChangesDateMeaningEvenWhenSerialMatches()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet.Cell("A1", "45000", styleIndex: 1)),
            builder => builder.WithDate1904().AddSheet("Sheet1", sheet => sheet.Cell("A1", "45000", styleIndex: 1)));

        var result = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { CompareFormatting = true });

        var difference = DifferenceAt(result, DifferenceKind.Value, "A1");
        Assert.NotEqual(difference.Left?.NormalizedValue, difference.Right?.NormalizedValue);
        Assert.Equal(CellValueKind.Date, difference.Left?.ValueKind);
        Assert.Equal(CellValueKind.Date, difference.Right?.ValueKind);
    }

    [Fact]
    public async Task TimeOnlyAndDurationValuesIgnoreWorkbookDateEpoch()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "0.5", styleIndex: 5)
                .Cell("A2", "1.5", styleIndex: 6)),
            builder => builder.WithDate1904().AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "0.5", styleIndex: 5)
                .Cell("A2", "1.5", styleIndex: 6)));

        var comparerResult = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { CompareFormatting = true });
        var leftPreview = await CreateReader().LoadWorksheetPreviewAsync(pair.Left, "Sheet1");
        var rightPreview = await CreateReader().LoadWorksheetPreviewAsync(pair.Right, "Sheet1");

        Assert.Equal(ComparisonStatus.Same, comparerResult.Status);
        Assert.Equal("12:00:00", leftPreview.Cells["A1"].DisplayValue);
        Assert.Equal("12:00:00", rightPreview.Cells["A1"].DisplayValue);
        Assert.Equal(leftPreview.Cells["A1"].NormalizedValue, rightPreview.Cells["A1"].NormalizedValue);
        Assert.Equal("36:00:00", leftPreview.Cells["A2"].DisplayValue);
        Assert.Equal("36:00:00", rightPreview.Cells["A2"].DisplayValue);
        Assert.Equal(leftPreview.Cells["A2"].NormalizedValue, rightPreview.Cells["A2"].NormalizedValue);
    }

    [Fact]
    public async Task ByteDifferentButSemanticSameIsReportedSameAndSourcesRemainUntouched()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder
                .AddSheet("Sheet1", sheet => sheet.Cell("A1", "42"))
                .AddUnreferencedPart("ignored/left.txt", "left noise"),
            builder => builder
                .AddSheet("Sheet1", sheet => sheet.Cell("A1", "42"))
                .AddUnreferencedPart("ignored/right.txt", "right noise"));
        var leftBefore = Sha256(pair.Left);
        var rightBefore = Sha256(pair.Right);

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right);

        Assert.False(result.ByteIdentical);
        Assert.Equal(ComparisonStatus.Same, result.Status);
        Assert.Equal(0, result.DifferenceCount);
        Assert.Equal(leftBefore, Sha256(pair.Left));
        Assert.Equal(rightBefore, Sha256(pair.Right));
        Assert.Equal(leftBefore, result.LeftSha256, ignoreCase: true);
        Assert.Equal(rightBefore, result.RightSha256, ignoreCase: true);
    }

    [Fact]
    public async Task ByteIdenticalFastPathReturnsSame()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var left = new TestWorkbookBuilder()
            .AddSheet("Sheet1", sheet => sheet.Cell("A1", "42"))
            .Save(temporaryDirectory.File("left.xlsx"));
        var right = temporaryDirectory.File("right.xlsx");
        File.Copy(left, right);

        var result = await CreateComparer().CompareAsync(left, right);

        Assert.True(result.ByteIdentical);
        Assert.Equal(ComparisonStatus.Same, result.Status);
        Assert.Empty(AllDifferences(result));
    }
}
