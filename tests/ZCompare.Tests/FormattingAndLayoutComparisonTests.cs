using ZCompare.Core;
using ZCompare.Tests.Fixtures;

namespace ZCompare.Tests;

public sealed class FormattingAndLayoutComparisonTests : ComparisonTestBase
{
    [Fact]
    public async Task CellScopedDifferencesExposeReferencesForRowHighlighting()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A5", "1", styleIndex: 0)
                .Cell("B6", "1", styleIndex: 0)
                .Comment("A5", "左批注")
                .Hyperlink("A5", "https://left.example/")),
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A5", "2", styleIndex: 2)
                .Cell("B6", "1", styleIndex: 3)
                .Comment("A5", "右批注")
                .Hyperlink("A5", "https://right.example/")));

        var result = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions
            {
                CompareFormatting = true,
                CompareFonts = true,
                CompareComments = true,
                CompareHyperlinks = true,
            });

        foreach (var kind in new[]
        {
            DifferenceKind.Value,
            DifferenceKind.Font,
            DifferenceKind.Fill,
            DifferenceKind.Border,
            DifferenceKind.Alignment,
            DifferenceKind.Comment,
            DifferenceKind.Hyperlink,
        })
        {
            DifferenceAt(result, kind, "A5");
        }
        DifferenceAt(result, DifferenceKind.NumberFormat, "B6");
    }

    [Fact]
    public async Task DetectedUncomparedObjectsAreExplicitWarnings()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "1")
                .ConditionalFormatting()
                .DataValidation()),
            builder => builder
                .AddSheet("Sheet1", sheet => sheet.Cell("A1", "1"))
                .AddUnreferencedPart("ignored/noise.txt", "force semantic comparison"));

        var result = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { CompareLayout = true });
        var warnings = AllDifferences(result)
            .Where(difference => difference.Kind == DifferenceKind.UncomparedObject)
            .ToArray();

        Assert.Equal(ComparisonStatus.Warning, result.Status);
        Assert.Contains(warnings, difference => difference.Description.Contains("条件格式", StringComparison.Ordinal));
        Assert.Contains(warnings, difference => difference.Description.Contains("数据验证", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChartSheetsAreReportedAsUncomparedObjects()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder
                .AddSheet("Data", sheet => sheet.Cell("A1", "1"))
                .AddChartSheet("Chart1"),
            builder => builder
                .AddSheet("Data", sheet => sheet.Cell("A1", "1"))
                .AddUnreferencedPart("ignored/noise.txt", "force semantic comparison"));

        var result = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { CompareLayout = true });

        Assert.Contains(
            AllDifferences(result),
            difference => difference.Kind == DifferenceKind.UncomparedObject &&
                difference.Description.Contains("图表工作表", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ModernCommentsAreReportedAsUncomparedObjects()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "same", TestCellType.InlineString)
                .ThreadedComment("A1", "Modern comment")),
            builder => builder
                .AddSheet("Sheet1", sheet => sheet.Cell("A1", "same", TestCellType.InlineString))
                .AddUnreferencedPart("ignored/noise.txt", "force semantic comparison"));

        var result = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { CompareComments = true });

        Assert.Contains(
            AllDifferences(result),
            difference => difference.Kind == DifferenceKind.UncomparedObject &&
                difference.Description.Contains("现代批注", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FormattingOptionControlsNumberFillBorderAndAlignmentDifferences()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "1", styleIndex: 0)
                .Cell("A2", "1", styleIndex: 0)),
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "1", styleIndex: 2)
                .Cell("A2", "1", styleIndex: 3)));
        var comparer = CreateComparer();

        var enabled = await comparer.CompareAsync(pair.Left, pair.Right, new ComparisonOptions { CompareFormatting = true });
        var disabled = await comparer.CompareAsync(pair.Left, pair.Right, new ComparisonOptions { CompareFormatting = false });

        var kinds = AllDifferences(enabled).Select(static difference => difference.Kind).ToHashSet();
        Assert.Contains(DifferenceKind.Fill, kinds);
        Assert.Contains(DifferenceKind.Border, kinds);
        Assert.Contains(DifferenceKind.Alignment, kinds);
        Assert.Contains(DifferenceKind.NumberFormat, kinds);
        Assert.DoesNotContain(DifferenceKind.Font, kinds);
        Assert.Equal(ComparisonStatus.Same, disabled.Status);
    }

    [Fact]
    public async Task ComparesHiddenRowsHiddenColumnsAndMergedRanges()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "1")
                .Cell("B2", "2")
                .HideRow(2)
                .HideColumn(1)
                .Merge("B2:C2")),
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "1")
                .Cell("B2", "2")));

        var result = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { CompareLayout = true });
        var kinds = AllDifferences(result).Select(static difference => difference.Kind).ToHashSet();

        Assert.Contains(DifferenceKind.RowHidden, kinds);
        Assert.Contains(DifferenceKind.ColumnHidden, kinds);
        Assert.Contains(DifferenceKind.Merge, kinds);
    }

    [Fact]
    public async Task ComparesBlankAuthorCommentsAndHyperlinks()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "链接", TestCellType.InlineString)
                .Cell("A2", "批注", TestCellType.InlineString)
                .Hyperlink("A1", "https://left.example/")
                .Comment("A2", "左批注", author: string.Empty)),
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "链接", TestCellType.InlineString)
                .Cell("A2", "批注", TestCellType.InlineString)
                .Hyperlink("A1", "https://right.example/")
                .Comment("A2", "右批注", author: string.Empty)));

        var result = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { CompareComments = true, CompareHyperlinks = true });

        var hyperlink = DifferenceAt(result, DifferenceKind.Hyperlink, "A1");
        var comment = DifferenceAt(result, DifferenceKind.Comment, "A2");
        Assert.Equal(string.Empty, comment.Left?.CommentAuthor);
        Assert.Equal(string.Empty, comment.Right?.CommentAuthor);
        Assert.NotEqual(hyperlink.Left?.Hyperlink, hyperlink.Right?.Hyperlink);
    }

    [Theory]
    [InlineData("https://left.example/", "https://right.example/", "Open", "Open", "Tip", "Tip")]
    [InlineData("https://same.example/", "https://same.example/", "Open left", "Open right", "Tip", "Tip")]
    [InlineData("https://same.example/", "https://same.example/", "Open", "Open", "Left tip", "Right tip")]
    public async Task ComparesTargetDisplayAndTooltipOnEmptyRangeHyperlinks(
        string leftTarget,
        string rightTarget,
        string leftDisplay,
        string rightDisplay,
        string leftTooltip,
        string rightTooltip)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet.Hyperlink(
                "A1:A2",
                leftTarget,
                leftDisplay,
                leftTooltip)),
            builder => builder.AddSheet("Sheet1", sheet => sheet.Hyperlink(
                "A1:A2",
                rightTarget,
                rightDisplay,
                rightTooltip)));

        var result = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { CompareHyperlinks = true });
        var difference = Assert.Single(
            AllDifferences(result),
            difference => difference.Kind == DifferenceKind.Hyperlink);

        Assert.NotEqual(difference.LeftDetail, difference.RightDetail);
    }

    [Fact]
    public async Task EquivalentHiddenColumnSegmentationsDoNotProduceDifferences()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "1")
                .Cell("B1", "2")
                .HideColumns(1, 2)),
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "1")
                .Cell("B1", "2")
                .HideColumn(1)
                .HideColumn(2)));

        var result = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { CompareLayout = true });

        Assert.Equal(ComparisonStatus.Same, result.Status);
        Assert.DoesNotContain(
            AllDifferences(result),
            difference => difference.Kind == DifferenceKind.ColumnHidden);
    }

    [Fact]
    public async Task ComparesWorksheetAdditionRemovalOrderAndVisibility()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder
                .AddSheet("Alpha")
                .AddSheet("Beta", state: "hidden")
                .AddSheet("LeftOnly"),
            builder => builder
                .AddSheet("Beta")
                .AddSheet("Alpha")
                .AddSheet("RightOnly"));

        var result = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { CompareLayout = true });
        var kinds = AllDifferences(result).Select(static difference => difference.Kind).ToHashSet();

        Assert.Contains(DifferenceKind.WorksheetAdded, kinds);
        Assert.Contains(DifferenceKind.WorksheetRemoved, kinds);
        Assert.Contains(DifferenceKind.WorksheetOrder, kinds);
        Assert.Contains(DifferenceKind.WorksheetVisibility, kinds);
    }
}
