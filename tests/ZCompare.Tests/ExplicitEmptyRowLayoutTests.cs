using ZCompare.Core;
using ZCompare.Tests.Fixtures;

namespace ZCompare.Tests;

public sealed class ExplicitEmptyRowLayoutTests : ComparisonTestBase
{
    [Fact]
    public async Task StoredEmptyRowIsIgnoredByDefaultButReportedAsLayoutWhenEnabled()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Data", sheet => sheet.Cell("A1", "same", TestCellType.InlineString)),
            builder => builder.AddSheet("Data", sheet => sheet
                .Cell("A1", "same", TestCellType.InlineString)
                .EmptyRow(5)));
        var comparer = CreateComparer();

        var valueOnly = await comparer.CompareAsync(pair.Left, pair.Right);
        var withLayout = await comparer.CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { CompareLayout = true });

        Assert.Equal(ComparisonStatus.Same, valueOnly.Status);
        var difference = Assert.Single(
            Assert.Single(withLayout.Worksheets).Differences,
            difference => difference.Kind == DifferenceKind.RowInserted);
        Assert.Equal("A5", difference.CellReference);
        Assert.Equal("5", difference.RightDetail);
    }
}
