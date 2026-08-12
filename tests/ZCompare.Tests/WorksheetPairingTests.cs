using ZCompare.Core;
using ZCompare.Tests.Fixtures;

namespace ZCompare.Tests;

public sealed class WorksheetPairingTests : ComparisonTestBase
{
    [Fact]
    public async Task DefaultNameModeTreatsDifferentNamesAsAddedAndRemoved()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("LeftName", sheet => sheet.Cell("A1", "same", TestCellType.InlineString)),
            builder => builder.AddSheet("RightName", sheet => sheet.Cell("A1", "same", TestCellType.InlineString)));

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right);

        Assert.Equal(WorksheetPairingMode.Name, new ComparisonOptions().WorksheetPairingMode);
        Assert.Equal(2, result.Worksheets.Count);
        Assert.Contains(
            result.Worksheets.SelectMany(static worksheet => worksheet.Differences),
            difference => difference.Kind == DifferenceKind.WorksheetRemoved);
        Assert.Contains(
            result.Worksheets.SelectMany(static worksheet => worksheet.Differences),
            difference => difference.Kind == DifferenceKind.WorksheetAdded);
    }

    [Fact]
    public async Task IndexModePairsWorksheetsAtTheSamePositionDespiteDifferentNames()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("LeftName", sheet => sheet.Cell("A1", "same", TestCellType.InlineString)),
            builder => builder.AddSheet("RightName", sheet => sheet.Cell("A1", "same", TestCellType.InlineString)));

        var result = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { WorksheetPairingMode = WorksheetPairingMode.Index });
        var worksheet = Assert.Single(result.Worksheets);

        Assert.Equal(ComparisonStatus.Same, worksheet.Status);
        Assert.Equal("LeftName ↔ RightName", worksheet.WorksheetName);
        Assert.Equal("LeftName", worksheet.EffectiveLeftWorksheetName);
        Assert.Equal("RightName", worksheet.EffectiveRightWorksheetName);
        Assert.DoesNotContain(
            worksheet.Differences,
            difference => difference.Kind is DifferenceKind.WorksheetAdded or DifferenceKind.WorksheetRemoved);
    }

    [Fact]
    public async Task ManualModeUsesConfiguredCrossNamePairs()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder
                .AddSheet("LeftOne", sheet => sheet.Cell("A1", "one", TestCellType.InlineString))
                .AddSheet("LeftTwo", sheet => sheet.Cell("A1", "two", TestCellType.InlineString)),
            builder => builder
                .AddSheet("RightOne", sheet => sheet.Cell("A1", "two", TestCellType.InlineString))
                .AddSheet("RightTwo", sheet => sheet.Cell("A1", "one", TestCellType.InlineString)));
        var options = new ComparisonOptions
        {
            WorksheetPairingMode = WorksheetPairingMode.Manual,
            ManualWorksheetPairs =
            [
                new WorksheetPair("LeftOne", "RightTwo"),
                new WorksheetPair("LeftTwo", "RightOne"),
            ],
        };

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right, options);

        Assert.Equal(ComparisonStatus.Same, result.Status);
        Assert.Collection(
            result.Worksheets,
            worksheet =>
            {
                Assert.Equal("LeftOne ↔ RightTwo", worksheet.WorksheetName);
                Assert.Equal("LeftOne", worksheet.EffectiveLeftWorksheetName);
                Assert.Equal("RightTwo", worksheet.EffectiveRightWorksheetName);
                Assert.Equal(ComparisonStatus.Same, worksheet.Status);
            },
            worksheet =>
            {
                Assert.Equal("LeftTwo ↔ RightOne", worksheet.WorksheetName);
                Assert.Equal("LeftTwo", worksheet.EffectiveLeftWorksheetName);
                Assert.Equal("RightOne", worksheet.EffectiveRightWorksheetName);
                Assert.Equal(ComparisonStatus.Same, worksheet.Status);
            });
    }

    [Theory]
    [InlineData(WorksheetPairingMode.Index)]
    [InlineData(WorksheetPairingMode.Manual)]
    public async Task CrossNamePairUsesDisplayNameForWorksheetAndDifferences(WorksheetPairingMode mode)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("LeftName", sheet => sheet.Cell("A1", "left", TestCellType.InlineString)),
            builder => builder.AddSheet("RightName", sheet => sheet.Cell("A1", "right", TestCellType.InlineString)));
        var options = new ComparisonOptions
        {
            WorksheetPairingMode = mode,
            ManualWorksheetPairs = mode == WorksheetPairingMode.Manual
                ? [new WorksheetPair("LeftName", "RightName")]
                : [],
        };

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right, options);
        var worksheet = Assert.Single(result.Worksheets);
        var difference = Assert.Single(
            worksheet.Differences,
            item => item.Kind == DifferenceKind.Value);

        Assert.Equal("LeftName ↔ RightName", worksheet.WorksheetName);
        Assert.Equal(worksheet.WorksheetName, difference.WorksheetName);
        Assert.Equal("LeftName", difference.Left?.WorksheetName);
        Assert.Equal("RightName", difference.Right?.WorksheetName);
    }
}
