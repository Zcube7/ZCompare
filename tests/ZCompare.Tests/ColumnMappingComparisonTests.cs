using ZCompare.Core;
using ZCompare.Tests.Fixtures;

namespace ZCompare.Tests;

public sealed class ColumnMappingComparisonTests : ComparisonTestBase
{
    [Fact]
    public async Task PartialMappingReportsPopulatedCrossColumnConflicts()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Data", sheet => sheet
                .Cell("A1", "same", TestCellType.InlineString)
                .Cell("B1", "left shadow", TestCellType.InlineString)
                .Cell("C1", "fallback", TestCellType.InlineString)),
            builder => builder.AddSheet("Data", sheet => sheet
                .Cell("A1", "right shadow", TestCellType.InlineString)
                .Cell("B1", "same", TestCellType.InlineString)
                .Cell("C1", "fallback", TestCellType.InlineString)));

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right, MappingOptions("a", "b"));
        var worksheet = Assert.Single(result.Worksheets);

        Assert.Equal(ComparisonStatus.Warning, result.Status);
        Assert.Equal(
            2,
            worksheet.Differences.Count(static difference => difference.Kind == DifferenceKind.Warning));
        var applied = Assert.Single(worksheet.AppliedColumnPairs);
        Assert.Equal("A", applied.LeftColumnIdentifier);
        Assert.Equal("B", applied.RightColumnIdentifier);
    }

    [Fact]
    public async Task CompleteColumnSwapDoesNotCreateConflictsOrDifferences()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Data", sheet => sheet
                .Cell("A1", "one", TestCellType.InlineString)
                .Cell("B1", "two", TestCellType.InlineString)),
            builder => builder.AddSheet("Data", sheet => sheet
                .Cell("A1", "two", TestCellType.InlineString)
                .Cell("B1", "one", TestCellType.InlineString)));
        var options = new ComparisonOptions
        {
            ColumnMappings =
            [
                new WorksheetColumnMapping(
                    "Data",
                    "Data",
                    [new ColumnPair("A", "B"), new ColumnPair("B", "A")]),
            ],
        };

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right, options);

        Assert.Equal(ComparisonStatus.Same, result.Status);
        Assert.Empty(Assert.Single(result.Worksheets).Differences);
    }

    [Fact]
    public async Task MappedValueDifferenceIsReportedOnceAtTheLeftDisplayColumn()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Data", sheet => sheet
                .Cell("A1", "left", TestCellType.InlineString)
                .Cell("B1", "ignored left", TestCellType.InlineString)),
            builder => builder.AddSheet("Data", sheet => sheet
                .Cell("A1", "ignored right", TestCellType.InlineString)
                .Cell("B1", "right", TestCellType.InlineString)));

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right, MappingOptions("A", "B"));
        var valueDifferences = Assert.Single(result.Worksheets).Differences
            .Where(static difference => difference.Kind == DifferenceKind.Value)
            .ToArray();

        var difference = Assert.Single(valueDifferences);
        Assert.Equal("A1", difference.CellReference);
        Assert.Equal("A1", difference.Left?.CellReference);
        Assert.Equal("B1", difference.Right?.CellReference);
    }

    [Fact]
    public async Task MappedCellsKeepBlankTypeAndCommentSemantics()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Data", sheet => sheet
                .Cell("A1", null)
                .Cell("C1", "anchor", TestCellType.InlineString)
                .Cell("A2", "1")
                .Cell("A3", "same", TestCellType.InlineString)
                .Comment("A3", "left note")),
            builder => builder.AddSheet("Data", sheet => sheet
                .Cell("C1", "anchor", TestCellType.InlineString)
                .Cell("B2", "1", TestCellType.InlineString)
                .Cell("B3", "same", TestCellType.InlineString)
                .Comment("B3", "right note")));
        var options = MappingOptions("A", "B") with { CompareComments = true };

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right, options);
        var differences = Assert.Single(result.Worksheets).Differences;

        Assert.DoesNotContain(differences, difference => difference.CellReference == "A1");
        Assert.Contains(differences, difference =>
            difference.Kind == DifferenceKind.CellType && difference.CellReference == "A2");
        Assert.Contains(differences, difference =>
            difference.Kind == DifferenceKind.Comment && difference.CellReference == "A3");
    }

    [Fact]
    public async Task MappingSupportsDifferentWorksheetNamesAndMappedRowInsertion()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("LeftData", sheet => sheet
                .Cell("A1", "one", TestCellType.InlineString)
                .Cell("A2", "two", TestCellType.InlineString)),
            builder => builder.AddSheet("RightData", sheet => sheet
                .Cell("B1", "one", TestCellType.InlineString)
                .Cell("B2", "inserted", TestCellType.InlineString)
                .Cell("B3", "two", TestCellType.InlineString)));
        var options = new ComparisonOptions
        {
            WorksheetPairingMode = WorksheetPairingMode.Index,
            ColumnMappings =
            [
                new WorksheetColumnMapping(
                    "LeftData",
                    "RightData",
                    [new ColumnPair("A", "B")]),
            ],
        };

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right, options);
        var worksheet = Assert.Single(result.Worksheets);

        Assert.Equal("LeftData", worksheet.EffectiveLeftWorksheetName);
        Assert.Equal("RightData", worksheet.EffectiveRightWorksheetName);
        Assert.Single(worksheet.Differences, difference => difference.Kind == DifferenceKind.RowInserted);
        Assert.DoesNotContain(worksheet.Differences, difference => difference.Kind is
            DifferenceKind.Value or DifferenceKind.CellType or DifferenceKind.FormulaResult);
        Assert.Equal(new ColumnPair("A", "B"), Assert.Single(worksheet.AppliedColumnPairs));
    }

    [Fact]
    public async Task DifferentKeyColumnsBecomeEffectiveColumnPairsWithoutDuplicateDifferences()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("LeftData", sheet => sheet
                .Cell("A1", "ID", TestCellType.InlineString)
                .Cell("B1", "Value", TestCellType.InlineString)
                .Cell("A2", "1", TestCellType.InlineString)
                .Cell("B2", "payload", TestCellType.InlineString)),
            builder => builder.AddSheet("RightData", sheet => sheet
                .Cell("C1", "ID", TestCellType.InlineString)
                .Cell("D1", "Value", TestCellType.InlineString)
                .Cell("C2", "1", TestCellType.InlineString)
                .Cell("D2", "payload", TestCellType.InlineString)));
        var options = new ComparisonOptions
        {
            WorksheetPairingMode = WorksheetPairingMode.Index,
            RowAlignmentMode = RowAlignmentMode.KeyColumns,
            KeyColumnRules =
            [
                new KeyColumnRule("LeftData", 1, ["A"]),
                new KeyColumnRule("RightData", 1, ["C"]),
            ],
            ColumnMappings =
            [
                new WorksheetColumnMapping("LeftData", "RightData", [new ColumnPair("B", "D")]),
            ],
        };

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right, options);
        var worksheet = Assert.Single(result.Worksheets);

        Assert.Equal(ComparisonStatus.Same, result.Status);
        Assert.Contains(new ColumnPair("A", "C"), worksheet.AppliedColumnPairs);
        Assert.Contains(new ColumnPair("B", "D"), worksheet.AppliedColumnPairs);
        Assert.Empty(worksheet.Differences);
    }

    [Fact]
    public async Task MappingStillRunsForByteIdenticalFiles()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var file = new TestWorkbookBuilder()
            .AddSheet("Data", sheet => sheet
                .Cell("A1", "left", TestCellType.InlineString)
                .Cell("B1", "right", TestCellType.InlineString))
            .Save(temporaryDirectory.File("same.xlsx"));

        var result = await CreateComparer().CompareAsync(file, file, MappingOptions("A", "B"));

        Assert.False(result.ByteIdentical);
        Assert.Equal(ComparisonStatus.Different, result.Status);
        Assert.Contains(
            Assert.Single(result.Worksheets).Differences,
            difference => difference.Kind == DifferenceKind.Value && difference.CellReference == "A1");
    }

    [Fact]
    public async Task InvalidDuplicateAndUnpairedMappingsFailClearly()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Data", sheet => sheet.Cell("A1", "1")),
            builder => builder.AddSheet("Data", sheet => sheet.Cell("A1", "1")));

        await Assert.ThrowsAsync<ArgumentException>(() => CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions
            {
                ColumnMappings =
                [
                    new WorksheetColumnMapping(
                        "Data",
                        "Data",
                        [new ColumnPair("A", "B"), new ColumnPair("A", "C")]),
                ],
            }));
        await Assert.ThrowsAsync<ArgumentException>(() => CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions
            {
                ColumnMappings =
                [
                    new WorksheetColumnMapping(
                        "Data",
                        "Data",
                        [new ColumnPair("A", "B"), new ColumnPair("C", "B")]),
                ],
            }));
        await Assert.ThrowsAsync<ArgumentException>(() => CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions
            {
                ColumnMappings =
                [
                    new WorksheetColumnMapping("Data", "Data", [new ColumnPair("XFE", "A")]),
                ],
            }));
        await Assert.ThrowsAsync<ArgumentException>(() => CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions
            {
                ColumnMappings =
                [
                    new WorksheetColumnMapping("Missing", "Data", [new ColumnPair("A", "B")]),
                ],
            }));
    }

    private static ComparisonOptions MappingOptions(string leftColumn, string rightColumn) => new()
    {
        ColumnMappings =
        [
            new WorksheetColumnMapping(
                "Data",
                "Data",
                [new ColumnPair(leftColumn, rightColumn)]),
        ],
    };
}
