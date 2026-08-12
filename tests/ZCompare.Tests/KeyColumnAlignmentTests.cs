using ZCompare.Core;
using ZCompare.Tests.Fixtures;

namespace ZCompare.Tests;

public sealed class KeyColumnAlignmentTests : ComparisonTestBase
{
    [Fact]
    public async Task SingleKeyColumnAlignsInsertedRowWithoutCellCascade()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SaveDataPair(
            temporaryDirectory,
            sheet =>
            {
                Header(sheet, "ID", "Name");
                Row(sheet, 2, "1", "alpha");
                Row(sheet, 3, "2", "bravo");
            },
            sheet =>
            {
                Header(sheet, "ID", "Name");
                Row(sheet, 2, "9", "inserted");
                Row(sheet, 3, "1", "alpha");
                Row(sheet, 4, "2", "bravo");
            });

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right, KeyOptions("A"));
        var worksheet = Assert.Single(result.Worksheets);

        Assert.Single(worksheet.Differences, difference => difference.Kind == DifferenceKind.RowInserted);
        Assert.DoesNotContain(worksheet.Differences, IsCascadedCellDifference);
        Assert.Single(worksheet.Alignment, alignment =>
            alignment.Status == RowAlignmentStatus.Inserted && alignment.RightRow == 2);
    }

    [Fact]
    public async Task CompositeKeyUsesAllConfiguredColumns()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SaveDataPair(
            temporaryDirectory,
            sheet =>
            {
                Header(sheet, "Region", "ID", "Value");
                Row(sheet, 2, "US", "1", "alpha");
                Row(sheet, 3, "US", "2", "bravo");
            },
            sheet =>
            {
                Header(sheet, "Region", "ID", "Value");
                Row(sheet, 2, "US", "1", "alpha");
                Row(sheet, 3, "US", "9", "inserted");
                Row(sheet, 4, "US", "2", "bravo");
            });

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right, KeyOptions("a", "B"));
        var worksheet = Assert.Single(result.Worksheets);

        Assert.Single(worksheet.Differences, difference => difference.Kind == DifferenceKind.RowInserted);
        Assert.DoesNotContain(
            worksheet.Differences,
            difference => difference.Kind == DifferenceKind.RowAlignmentWarning);
        Assert.DoesNotContain(worksheet.Differences, IsCascadedCellDifference);
    }

    [Fact]
    public async Task DuplicateCompleteKeysAreReportedAsAmbiguous()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SaveDataPair(
            temporaryDirectory,
            sheet =>
            {
                Header(sheet, "ID", "Value");
                Row(sheet, 2, "1", "alpha");
                Row(sheet, 3, "1", "bravo");
            },
            sheet =>
            {
                Header(sheet, "ID", "Value");
                Row(sheet, 2, "1", "alpha");
                Row(sheet, 3, "1", "bravo");
            });

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right, KeyOptions("A"));
        var worksheet = Assert.Single(result.Worksheets);

        Assert.Contains(worksheet.Alignment, alignment => alignment.Status == RowAlignmentStatus.Ambiguous);
        Assert.Contains(worksheet.Differences, difference => difference.Kind == DifferenceKind.RowAlignmentWarning);
    }

    [Fact]
    public async Task MissingKeyValueIsReportedAsAmbiguous()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SaveDataPair(
            temporaryDirectory,
            sheet =>
            {
                Header(sheet, "ID", "Value");
                sheet.Cell("B2", "payload", TestCellType.InlineString);
                Row(sheet, 3, "2", "tail");
            },
            sheet =>
            {
                Header(sheet, "ID", "Value");
                sheet.Cell("B2", "payload", TestCellType.InlineString);
                Row(sheet, 3, "2", "tail");
            });

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right, KeyOptions("A"));
        var worksheet = Assert.Single(result.Worksheets);

        Assert.Contains(worksheet.Alignment, alignment =>
            alignment.Status == RowAlignmentStatus.Ambiguous &&
            alignment.LeftRow == 2 &&
            alignment.RightRow == 2);
        Assert.Contains(worksheet.Differences, difference => difference.Kind == DifferenceKind.RowAlignmentWarning);
    }

    [Fact]
    public async Task ChangedKeyIsDeletionAndInsertionRatherThanValueModification()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SaveDataPair(
            temporaryDirectory,
            sheet =>
            {
                Header(sheet, "ID", "Value");
                Row(sheet, 2, "1", "same payload");
            },
            sheet =>
            {
                Header(sheet, "ID", "Value");
                Row(sheet, 2, "2", "same payload");
            });

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right, KeyOptions("A"));
        var worksheet = Assert.Single(result.Worksheets);

        Assert.Single(worksheet.Differences, difference => difference.Kind == DifferenceKind.RowDeleted);
        Assert.Single(worksheet.Differences, difference => difference.Kind == DifferenceKind.RowInserted);
        Assert.DoesNotContain(worksheet.Differences, IsCascadedCellDifference);
    }

    private static ComparisonOptions KeyOptions(params string[] columns) => new()
    {
        RowAlignmentMode = RowAlignmentMode.KeyColumns,
        KeyColumnRules = [new KeyColumnRule("Data", 1, columns)],
    };

    private static (string Left, string Right) SaveDataPair(
        TemporaryDirectory temporaryDirectory,
        Action<TestSheet> left,
        Action<TestSheet> right) =>
        SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Data", left),
            builder => builder.AddSheet("Data", right));

    private static void Header(TestSheet sheet, params string[] values) => Row(sheet, 1, values);

    private static void Row(TestSheet sheet, int row, params string[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            sheet.Cell(
                $"{ColumnName(index + 1)}{row}",
                values[index],
                TestCellType.InlineString);
        }
    }

    private static string ColumnName(int column) => ((char)('A' + column - 1)).ToString();

    private static bool IsCascadedCellDifference(Difference difference) => difference.Kind is
        DifferenceKind.Value or DifferenceKind.CellType or DifferenceKind.FormulaResult;
}
