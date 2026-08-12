using ZCompare.Core;
using ZCompare.Tests.Fixtures;

namespace ZCompare.Tests;

public sealed class ComparisonDefaultsTests : ComparisonTestBase
{
    [Fact]
    public async Task DefaultComparisonReportsOnlySavedValueDifferences()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "2", formula: "1+1")
                .Cell("A2", "3", formula: "1+2")
                .Cell("A3", "10")
                .Cell("A4", "相同", TestCellType.InlineString, styleIndex: 0)
                .Comment("A4", "左批注")
                .Hyperlink("A4", "https://left.example/")
                .HideRow(4)
                .HideColumn(1)
                .Merge("B4:C4")),
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "2", formula: "4/2")
                .Cell("A2", "4", formula: "1+2")
                .Cell("A3", "11")
                .Cell("A4", "相同", TestCellType.InlineString, styleIndex: 2)
                .Comment("A4", "右批注")
                .Hyperlink("A4", "https://right.example/")));

        var defaults = new ComparisonOptions();
        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right);
        var differences = AllDifferences(result);

        Assert.False(defaults.CompareFormulas);
        Assert.False(defaults.CompareFormatting);
        Assert.False(defaults.CompareFonts);
        Assert.False(defaults.CompareComments);
        Assert.False(defaults.CompareHyperlinks);
        Assert.False(defaults.CompareLayout);
        Assert.True(defaults.CaseSensitive);
        Assert.Equal(ComparisonStatus.Different, result.Status);
        Assert.Equal(2, differences.Count);
        Assert.Contains(differences, difference =>
            difference.Kind == DifferenceKind.FormulaResult && difference.CellReference == "A2");
        Assert.Contains(differences, difference =>
            difference.Kind == DifferenceKind.Value && difference.CellReference == "A3");
        Assert.DoesNotContain(differences, difference => difference.Kind is
            DifferenceKind.Formula or
            DifferenceKind.NumberFormat or
            DifferenceKind.Font or
            DifferenceKind.Fill or
            DifferenceKind.Border or
            DifferenceKind.Alignment or
            DifferenceKind.Comment or
            DifferenceKind.Hyperlink or
            DifferenceKind.RowHidden or
            DifferenceKind.ColumnHidden or
            DifferenceKind.Merge);
    }
}
