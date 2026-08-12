using ZCompare.Core;
using ZCompare.Tests.Fixtures;

namespace ZCompare.Tests;

public sealed class GranularComparisonOptionsTests : ComparisonTestBase
{
    [Fact]
    public async Task DefaultsCompareOnlySavedValuesAndPreserveCase()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "Alpha", TestCellType.InlineString)
                .Cell("A2", "2", formula: "1+1")
                .Cell("A3", "same", TestCellType.InlineString, styleIndex: 0)
                .Comment("A3", "Left note")
                .Hyperlink("A3", "https://left.example/Path")
                .HideRow(3)
                .HideColumn(1)
                .Merge("B3:C3")
                .ConditionalFormatting()),
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "alpha", TestCellType.InlineString)
                .Cell("A2", "2", formula: "4/2")
                .Cell("A3", "same", TestCellType.InlineString, styleIndex: 2)
                .Comment("A3", "Right note")
                .Hyperlink("A3", "https://right.example/Path")));

        var options = new ComparisonOptions();
        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right, options);

        Assert.False(options.CompareFormulas);
        Assert.False(options.CompareFormatting);
        Assert.False(options.CompareFonts);
        Assert.False(options.CompareComments);
        Assert.False(options.CompareHyperlinks);
        Assert.False(options.CompareLayout);
        Assert.True(options.CaseSensitive);
        var difference = Assert.Single(AllDifferences(result));
        Assert.Equal(DifferenceKind.Value, difference.Kind);
        Assert.Equal("A1", difference.CellReference);
    }

    [Fact]
    public async Task FormulaOptionOnlyReportsFormulaText()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet.Cell("A1", "2", formula: "1+1")),
            builder => builder.AddSheet("Sheet1", sheet => sheet.Cell("A1", "2", formula: "4/2")));

        var result = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { CompareFormulas = true });

        Assert.Equal(DifferenceKind.Formula, Assert.Single(AllDifferences(result)).Kind);
    }

    [Fact]
    public async Task FormattingOptionOnlyReportsNumberFillBorderAndAlignment()
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

        var result = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { CompareFormatting = true });
        var kinds = AllDifferences(result).Select(static difference => difference.Kind).ToHashSet();

        Assert.True(kinds.SetEquals([
            DifferenceKind.NumberFormat,
            DifferenceKind.Fill,
            DifferenceKind.Border,
            DifferenceKind.Alignment,
        ]));
    }

    [Fact]
    public async Task FontOptionOnlyReportsFont()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet.Cell("A1", "same", TestCellType.InlineString, styleIndex: 0)),
            builder => builder.AddSheet("Sheet1", sheet => sheet.Cell("A1", "same", TestCellType.InlineString, styleIndex: 4)));

        var result = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { CompareFonts = true });

        Assert.Equal(DifferenceKind.Font, Assert.Single(AllDifferences(result)).Kind);
    }

    [Fact]
    public async Task CommentOptionOnlyReportsComment()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "same", TestCellType.InlineString)
                .Comment("A1", "Left note")),
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "same", TestCellType.InlineString)
                .Comment("A1", "Right note")));

        var result = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { CompareComments = true });

        Assert.Equal(DifferenceKind.Comment, Assert.Single(AllDifferences(result)).Kind);
    }

    [Fact]
    public async Task HyperlinkOptionOnlyReportsHyperlink()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "same", TestCellType.InlineString)
                .Hyperlink("A1", "https://left.example/Path")),
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "same", TestCellType.InlineString)
                .Hyperlink("A1", "https://right.example/Path")));

        var result = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { CompareHyperlinks = true });

        Assert.Equal(DifferenceKind.Hyperlink, Assert.Single(AllDifferences(result)).Kind);
    }

    [Fact]
    public async Task LayoutOptionOnlyReportsLayoutAndUncomparedObjects()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder
                .AddSheet("Alpha", sheet => sheet
                    .Cell("A1", "same", TestCellType.InlineString)
                    .HideRow(1)
                    .HideColumn(1)
                    .Merge("A1:B1")
                    .ConditionalFormatting()
                    .DataValidation())
                .AddSheet("Beta", state: "hidden"),
            builder => builder
                .AddSheet("Beta")
                .AddSheet("Alpha", sheet => sheet.Cell("A1", "same", TestCellType.InlineString)));

        var result = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { CompareLayout = true });
        var differences = AllDifferences(result);

        Assert.Contains(differences, difference => difference.Kind == DifferenceKind.RowHidden);
        Assert.Contains(differences, difference => difference.Kind == DifferenceKind.ColumnHidden);
        Assert.Contains(differences, difference => difference.Kind == DifferenceKind.Merge);
        Assert.Contains(differences, difference => difference.Kind == DifferenceKind.WorksheetOrder);
        Assert.Contains(differences, difference => difference.Kind == DifferenceKind.WorksheetVisibility);
        Assert.Contains(differences, difference => difference.Kind == DifferenceKind.UncomparedObject);
        Assert.All(differences, difference => Assert.Contains(difference.Kind, new[]
        {
            DifferenceKind.RowHidden,
            DifferenceKind.ColumnHidden,
            DifferenceKind.Merge,
            DifferenceKind.WorksheetOrder,
            DifferenceKind.WorksheetVisibility,
            DifferenceKind.UncomparedObject,
        }));
    }

    [Fact]
    public async Task CaseInsensitiveComparisonAppliesToTextFormulaCommentAndHyperlink()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "Alpha", TestCellType.InlineString)
                .Cell("A2", "2", formula: "SUM(1,1)")
                .Cell("A3", "same", TestCellType.InlineString)
                .Cell("A4", "same", TestCellType.InlineString)
                .Comment("A3", "Important Note")
                .Hyperlink("A4", "https://example.test/Path")),
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "alpha", TestCellType.InlineString)
                .Cell("A2", "2", formula: "sum(1,1)")
                .Cell("A3", "same", TestCellType.InlineString)
                .Cell("A4", "same", TestCellType.InlineString)
                .Comment("A3", "important note")
                .Hyperlink("A4", "https://example.test/path")));
        var comparer = CreateComparer();

        var caseSensitive = await comparer.CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions
            {
                CompareFormulas = true,
                CompareComments = true,
                CompareHyperlinks = true,
            });
        var caseInsensitive = await comparer.CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions
            {
                CompareFormulas = true,
                CompareComments = true,
                CompareHyperlinks = true,
                CaseSensitive = false,
            });

        var strictKinds = AllDifferences(caseSensitive)
            .Select(static difference => difference.Kind)
            .ToHashSet();
        Assert.True(strictKinds.SetEquals([
            DifferenceKind.Value,
            DifferenceKind.Formula,
            DifferenceKind.Comment,
            DifferenceKind.Hyperlink,
        ]));
        Assert.Equal(ComparisonStatus.Same, caseInsensitive.Status);
        Assert.Empty(AllDifferences(caseInsensitive));
    }

    [Fact]
    public async Task CaseInsensitiveComparisonStillPreservesWhitespace()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet.Cell("A1", "Alpha ", TestCellType.InlineString)),
            builder => builder.AddSheet("Sheet1", sheet => sheet.Cell("A1", "Alpha", TestCellType.InlineString)));

        var result = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { CaseSensitive = false });

        var difference = DifferenceAt(result, DifferenceKind.Value, "A1");
        Assert.NotEqual(difference.LeftDetail, difference.RightDetail);
    }

    [Fact]
    public async Task WorksheetAdditionsAndRemovalsAreAlwaysReportedAndCountedOnce()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Common").AddSheet("LeftOnly"),
            builder => builder.AddSheet("Common").AddSheet("RightOnly"));

        var result = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions
            {
                CompareFormulas = false,
                CompareFormatting = false,
                CompareFonts = false,
                CompareComments = false,
                CompareHyperlinks = false,
                CompareLayout = false,
            });
        var differences = AllDifferences(result);

        Assert.Equal(ComparisonStatus.Different, result.Status);
        Assert.Equal(2, result.DifferenceCount);
        Assert.Single(differences, difference => difference.Kind == DifferenceKind.WorksheetRemoved);
        Assert.Single(differences, difference => difference.Kind == DifferenceKind.WorksheetAdded);
    }

    [Fact]
    public async Task SafetyWarningsRemainEnabledWhenAllOptionalComparisonsAreDisabled()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var left = new TestWorkbookBuilder()
            .WithCalculation()
            .AddSheet("Sheet1", sheet => sheet.Cell("A1", null, formula: "1+1"))
            .Save(temporaryDirectory.File("left.xlsx"));
        var right = temporaryDirectory.File("right.xlsx");
        File.Copy(left, right);

        var result = await CreateComparer().CompareAsync(
            left,
            right,
            new ComparisonOptions
            {
                CompareFormulas = false,
                CompareFormatting = false,
                CompareFonts = false,
                CompareComments = false,
                CompareHyperlinks = false,
                CompareLayout = false,
            });

        Assert.Equal(ComparisonStatus.Warning, result.Status);
        Assert.Contains(AllDifferences(result), difference => difference.Kind == DifferenceKind.Warning);
    }
}
