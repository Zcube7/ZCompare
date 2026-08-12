using ZCompare.Core;
using ZCompare.Tests.Fixtures;

namespace ZCompare.Tests;

public sealed class ColumnMappingAdvancedComparisonTests : ComparisonTestBase
{
    [Fact]
    public async Task MappedCellsStillReportEnabledFormattingDifferencesAtDisplayColumn()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Data", sheet => sheet
                .Cell("A1", "same", TestCellType.InlineString, styleIndex: 0)),
            builder => builder.AddSheet("Data", sheet => sheet
                .Cell("B1", "same", TestCellType.InlineString, styleIndex: 2)));
        var options = new ComparisonOptions
        {
            CompareFormatting = true,
            ColumnMappings =
            [
                new WorksheetColumnMapping(
                    "Data",
                    "Data",
                    [new ColumnPair("A", "B")]),
            ],
        };

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right, options);
        var differences = Assert.Single(result.Worksheets).Differences;

        Assert.Equal(
            new[] { DifferenceKind.Fill, DifferenceKind.Border, DifferenceKind.Alignment }.Order(),
            differences.Select(static difference => difference.Kind).Order());
        Assert.All(differences, difference =>
        {
            Assert.Equal("A1", difference.CellReference);
            Assert.Equal("A1", difference.Left?.CellReference);
            Assert.Equal("B1", difference.Right?.CellReference);
        });
        Assert.DoesNotContain(differences, difference => difference.Kind == DifferenceKind.Value);
    }
}
