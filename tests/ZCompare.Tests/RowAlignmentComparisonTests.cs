using ZCompare.Core;
using ZCompare.Tests.Fixtures;

namespace ZCompare.Tests;

public sealed class RowAlignmentComparisonTests : ComparisonTestBase
{
    private static readonly string[] BaselineRows =
        ["alpha", "bravo", "charlie", "delta", "echo"];

    [Fact]
    public void DefaultRowAlignmentModeIsConservative()
    {
        Assert.Equal(RowAlignmentMode.Conservative, new ComparisonOptions().RowAlignmentMode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(5)]
    public async Task ConservativeModeReportsOneInsertedRowWithoutDownstreamCellCascade(
        int insertionIndex)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var rightRows = BaselineRows.ToList();
        rightRows.Insert(insertionIndex, "inserted");
        var pair = SaveRows(temporaryDirectory, BaselineRows, rightRows);

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right);
        var worksheet = Assert.Single(result.Worksheets);
        var rowDifference = Assert.Single(
            worksheet.Differences,
            difference => difference.Kind == DifferenceKind.RowInserted);

        Assert.Null(rowDifference.Left);
        Assert.Equal("inserted", rowDifference.Right?.RawValue);
        Assert.DoesNotContain(worksheet.Differences, IsCellCascadeDifference);

        var alignment = Assert.Single(
            worksheet.Alignment,
            item => item.Status == RowAlignmentStatus.Inserted);
        Assert.Null(alignment.LeftRow);
        Assert.Equal(insertionIndex + 1, alignment.RightRow);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task ConservativeModeReportsOneDeletedRowWithoutDownstreamCellCascade(
        int deletionIndex)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var rightRows = BaselineRows.ToList();
        var deletedValue = rightRows[deletionIndex];
        rightRows.RemoveAt(deletionIndex);
        var pair = SaveRows(temporaryDirectory, BaselineRows, rightRows);

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right);
        var worksheet = Assert.Single(result.Worksheets);
        var rowDifference = Assert.Single(
            worksheet.Differences,
            difference => difference.Kind == DifferenceKind.RowDeleted);

        Assert.Equal(deletedValue, rowDifference.Left?.RawValue);
        Assert.Null(rowDifference.Right);
        Assert.DoesNotContain(worksheet.Differences, IsCellCascadeDifference);

        var alignment = Assert.Single(
            worksheet.Alignment,
            item => item.Status == RowAlignmentStatus.Deleted);
        Assert.Equal(deletionIndex + 1, alignment.LeftRow);
        Assert.Null(alignment.RightRow);
    }

    [Fact]
    public async Task StrictRowNumberModeRetainsAddressBasedCellCascade()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var rightRows = BaselineRows.Prepend("inserted").ToArray();
        var pair = SaveRows(temporaryDirectory, BaselineRows, rightRows);

        var result = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { RowAlignmentMode = RowAlignmentMode.StrictRowNumber });
        var worksheet = Assert.Single(result.Worksheets);

        Assert.DoesNotContain(
            worksheet.Differences,
            difference => difference.Kind is DifferenceKind.RowInserted or DifferenceKind.RowDeleted);
        Assert.True(worksheet.Differences.Count(IsCellCascadeDifference) >= BaselineRows.Length);
        Assert.All(
            worksheet.Alignment,
            alignment => Assert.Equal(RowAlignmentStatus.NotApplied, alignment.Status));
    }

    [Fact]
    public async Task ConservativeModeSeparatesInsertedRowFromModificationOfAlignedRow()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var rightRows = new[]
        {
            "alpha",
            "inserted",
            "bravo",
            "charlie changed",
            "delta",
            "echo",
        };
        var pair = SaveRows(temporaryDirectory, BaselineRows, rightRows);

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right);
        var worksheet = Assert.Single(result.Worksheets);

        Assert.Single(worksheet.Differences, difference => difference.Kind == DifferenceKind.RowInserted);
        var valueDifference = Assert.Single(
            worksheet.Differences,
            difference => difference.Kind == DifferenceKind.Value);
        Assert.Equal("charlie", valueDifference.Left?.RawValue);
        Assert.Equal("charlie changed", valueDifference.Right?.RawValue);
        Assert.DoesNotContain(
            worksheet.Differences,
            difference => difference.Kind is DifferenceKind.CellType or DifferenceKind.FormulaResult);
        Assert.Single(
            worksheet.Alignment,
            alignment => alignment.Status == RowAlignmentStatus.Modified &&
                alignment.LeftRow == 3 &&
                alignment.RightRow == 4);
        Assert.Equal(1, worksheet.RowDifferenceCount);
        Assert.Equal(1, worksheet.CellDifferenceCount);
        Assert.Equal(2, worksheet.DistinctDifferenceCount);
    }

    [Fact]
    public async Task DuplicateRowsWithoutUniqueAnchorsAreReportedAsAmbiguous()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SaveRows(
            temporaryDirectory,
            ["left edge", "duplicate", "duplicate", "left tail"],
            ["right edge", "duplicate", "inserted", "duplicate", "right tail"]);

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right);
        var worksheet = Assert.Single(result.Worksheets);

        Assert.Contains(
            worksheet.Alignment,
            alignment => alignment.Status == RowAlignmentStatus.Ambiguous);
        Assert.Single(
            worksheet.Differences,
            difference => difference.Kind == DifferenceKind.RowAlignmentWarning);
        var inserted = Assert.Single(
            worksheet.Alignment,
            alignment => alignment.Status == RowAlignmentStatus.Inserted);
        Assert.Single(
            worksheet.Differences,
            difference => difference.Kind == DifferenceKind.RowInserted);
        Assert.DoesNotContain(
            worksheet.Differences,
            difference => IsCellCascadeDifference(difference) &&
                difference.Left is null &&
                difference.Right?.CellReference == $"A{inserted.RightRow}");
    }

    [Fact]
    public async Task ExtraMultiColumnRowWithoutUniqueAnchorsIsOneRowEventWithoutCellCascade()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet =>
            {
                AddMultiColumnRow(sheet, 1, "left-1");
                AddMultiColumnRow(sheet, 2, "left-2");
            }),
            builder => builder.AddSheet("Sheet1", sheet =>
            {
                AddMultiColumnRow(sheet, 1, "right-1");
                AddMultiColumnRow(sheet, 2, "right-2");
                AddMultiColumnRow(sheet, 3, "extra");
            }));

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right);
        var worksheet = Assert.Single(result.Worksheets);

        Assert.Single(
            worksheet.Differences,
            difference => difference.Kind == DifferenceKind.RowInserted);
        Assert.Single(
            worksheet.Differences,
            difference => difference.Kind == DifferenceKind.RowAlignmentWarning);
        Assert.DoesNotContain(
            worksheet.Differences,
            difference => IsCellCascadeDifference(difference) &&
                difference.Left is null &&
                difference.Right?.CellReference is "A3" or "B3" or "C3");
    }

    [Fact]
    public async Task InsertionBetweenEquivalentDuplicateRowsIsOneWarnedRowEvent()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SaveRows(
            temporaryDirectory,
            ["duplicate", "duplicate"],
            ["duplicate", "inserted", "duplicate"]);

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right);
        var worksheet = Assert.Single(result.Worksheets);

        Assert.Single(
            worksheet.Differences,
            difference => difference.Kind == DifferenceKind.RowInserted);
        Assert.Single(
            worksheet.Differences,
            difference => difference.Kind == DifferenceKind.RowAlignmentWarning);
        Assert.DoesNotContain(
            worksheet.Differences,
            difference => IsCellCascadeDifference(difference) &&
                difference.Left is null &&
                difference.Right?.CellReference == "A2");
        Assert.Contains(
            worksheet.Alignment,
            alignment => alignment.Status == RowAlignmentStatus.Inserted &&
                alignment.RightRow == 2 &&
                alignment.Message is not null);
    }

    [Fact]
    public async Task ConservativeDefaultKeepsOrdinarySameAddressValueSemantics()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SaveRows(
            temporaryDirectory,
            ["same", "left value", "same tail"],
            ["same", "right value", "same tail"]);

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right);
        var worksheet = Assert.Single(result.Worksheets);
        var difference = Assert.Single(
            worksheet.Differences,
            item => item.Kind == DifferenceKind.Value);

        Assert.Equal("A2", difference.Left?.CellReference);
        Assert.Equal("A2", difference.Right?.CellReference);
        Assert.DoesNotContain(
            worksheet.Differences,
            item => item.Kind is DifferenceKind.RowInserted or DifferenceKind.RowDeleted);
    }

    private static bool IsCellCascadeDifference(Difference difference) => difference.Kind is
        DifferenceKind.Value or
        DifferenceKind.CellType or
        DifferenceKind.FormulaResult;

    private static (string Left, string Right) SaveRows(
        TemporaryDirectory temporaryDirectory,
        IReadOnlyList<string> leftRows,
        IReadOnlyList<string> rightRows) =>
        SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => AddRows(sheet, leftRows)),
            builder => builder.AddSheet("Sheet1", sheet => AddRows(sheet, rightRows)));

    private static void AddRows(TestSheet sheet, IReadOnlyList<string> rows)
    {
        for (var index = 0; index < rows.Count; index++)
        {
            sheet.Cell($"A{index + 1}", rows[index], TestCellType.InlineString);
        }
    }

    private static void AddMultiColumnRow(TestSheet sheet, int row, string prefix)
    {
        sheet.Cell($"A{row}", prefix + "-a", TestCellType.InlineString);
        sheet.Cell($"B{row}", prefix + "-b", TestCellType.InlineString);
        sheet.Cell($"C{row}", prefix + "-c", TestCellType.InlineString);
    }
}
