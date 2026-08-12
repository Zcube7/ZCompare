using ZCompare.Core;
using ZCompare.Tests.Fixtures;

namespace ZCompare.Tests;

public sealed class AlignmentSuggestionServiceTests : ComparisonTestBase
{
    [Fact]
    public async Task ProducesExplainableKeyMappingAndReadOnlyGroupingSuggestions()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("LeftData", sheet => sheet
                .Cell("A1", "ID", TestCellType.InlineString)
                .Cell("B1", "Group", TestCellType.InlineString)
                .Cell("A2", "1001", TestCellType.InlineString).Cell("B2", "X", TestCellType.InlineString)
                .Cell("A3", "1002", TestCellType.InlineString).Cell("B3", "X", TestCellType.InlineString)
                .Cell("A4", "1003", TestCellType.InlineString).Cell("B4", "Y", TestCellType.InlineString)
                .Cell("A5", "1004", TestCellType.InlineString).Cell("B5", "Y", TestCellType.InlineString)),
            builder => builder.AddSheet("RightData", sheet => sheet
                .Cell("C1", "ID", TestCellType.InlineString)
                .Cell("D1", "Group", TestCellType.InlineString)
                .Cell("C2", "1001", TestCellType.InlineString).Cell("D2", "X", TestCellType.InlineString)
                .Cell("C3", "1002", TestCellType.InlineString).Cell("D3", "X", TestCellType.InlineString)
                .Cell("C4", "1003", TestCellType.InlineString).Cell("D4", "Y", TestCellType.InlineString)
                .Cell("C5", "1004", TestCellType.InlineString).Cell("D5", "Y", TestCellType.InlineString)));
        var leftSha = Sha256(pair.Left);
        var rightSha = Sha256(pair.Right);

        var result = await new AlignmentSuggestionService(CreateReader()).AnalyzeAsync(
            pair.Left,
            pair.Right,
            "LeftData",
            "RightData");

        var key = Assert.Single(result.Suggestions, suggestion =>
            suggestion.Kind == AlignmentSuggestionKind.KeyColumns &&
            suggestion.LeftColumns.SequenceEqual(["A"]) &&
            suggestion.RightColumns.SequenceEqual(["C"]));
        Assert.True(key.CanApply);
        Assert.Equal(100d, key.ConfidencePercent);
        Assert.Equal(100d, key.CrossCoveragePercent);
        Assert.NotEmpty(key.Samples);
        Assert.Contains("仅按保存值", key.Reason, StringComparison.Ordinal);

        Assert.Contains(result.Suggestions, suggestion =>
            suggestion.Kind == AlignmentSuggestionKind.ColumnMapping &&
            suggestion.ColumnPairs.SequenceEqual([new ColumnPair("A", "C")]) &&
            suggestion.CanApply);
        var grouping = Assert.Single(result.Suggestions, suggestion =>
            suggestion.Kind == AlignmentSuggestionKind.GroupingColumn &&
            suggestion.LeftColumns.SequenceEqual(["B"]) &&
            suggestion.RightColumns.SequenceEqual(["D"]));
        Assert.False(grouping.CanApply);
        Assert.Equal(leftSha, Sha256(pair.Left));
        Assert.Equal(rightSha, Sha256(pair.Right));
    }

    [Fact]
    public async Task SuggestsAtMostTwoExactColumnsForCompositeKey()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Data", sheet => sheet
                .Cell("A1", "Part", TestCellType.InlineString)
                .Cell("B1", "Code", TestCellType.InlineString)
                .Cell("A2", "1").Cell("B2", "10")
                .Cell("A3", "1").Cell("B3", "20")
                .Cell("A4", "2").Cell("B4", "10")
                .Cell("A5", "2").Cell("B5", "20")),
            builder => builder.AddSheet("Data", sheet => sheet
                .Cell("C1", "Part", TestCellType.InlineString)
                .Cell("D1", "Code", TestCellType.InlineString)
                .Cell("C2", "1").Cell("D2", "10")
                .Cell("C3", "1").Cell("D3", "20")
                .Cell("C4", "2").Cell("D4", "10")
                .Cell("C5", "2").Cell("D5", "20")));

        var result = await new AlignmentSuggestionService().AnalyzeAsync(
            pair.Left,
            pair.Right,
            "Data",
            "Data");

        var composite = Assert.Single(result.Suggestions, suggestion =>
            suggestion.Kind == AlignmentSuggestionKind.KeyColumns &&
            suggestion.LeftColumns.SequenceEqual(["A", "B"]) &&
            suggestion.RightColumns.SequenceEqual(["C", "D"]));
        Assert.True(composite.CanApply);
        Assert.Equal(2, composite.ColumnPairs.Count);
        Assert.DoesNotContain(result.Suggestions, suggestion =>
            suggestion.Kind == AlignmentSuggestionKind.KeyColumns &&
            suggestion.LeftColumns.Count == 1 &&
            suggestion.LeftColumns[0] is "A" or "B");
    }

    [Fact]
    public async Task DuplicateMissingCaseAndWhitespaceValuesAreNotTreatedAsSafeKeys()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Data", sheet => sheet
                .Cell("A1", "ID", TestCellType.InlineString).Cell("B1", "row", TestCellType.InlineString)
                .Cell("A2", "Key", TestCellType.InlineString).Cell("B2", "1")
                .Cell("A3", "Key", TestCellType.InlineString).Cell("B3", "2")
                .Cell("A4", " ", TestCellType.InlineString).Cell("B4", "3")
                .Cell("B5", "4")),
            builder => builder.AddSheet("Data", sheet => sheet
                .Cell("C1", "ID", TestCellType.InlineString).Cell("D1", "row", TestCellType.InlineString)
                .Cell("C2", "key", TestCellType.InlineString).Cell("D2", "1")
                .Cell("C3", "key", TestCellType.InlineString).Cell("D3", "2")
                .Cell("C4", "", TestCellType.InlineString).Cell("D4", "3")
                .Cell("C5", "Z", TestCellType.InlineString).Cell("D5", "4")));

        var result = await new AlignmentSuggestionService().AnalyzeAsync(
            pair.Left,
            pair.Right,
            "Data",
            "Data");

        Assert.DoesNotContain(result.Suggestions, suggestion =>
            suggestion.Kind == AlignmentSuggestionKind.KeyColumns &&
            suggestion.LeftColumns.Contains("A", StringComparer.Ordinal) &&
            suggestion.RightColumns.Contains("C", StringComparer.Ordinal));
        Assert.DoesNotContain(result.Suggestions, suggestion =>
            suggestion.Kind == AlignmentSuggestionKind.ColumnMapping &&
            suggestion.LeftColumns.SequenceEqual(["A"]) &&
            suggestion.RightColumns.SequenceEqual(["C"]));
    }

    [Fact]
    public async Task HonorsRowAndColumnBoundsAndReportsTruncation()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Data", sheet => PopulateSixRows(sheet, "A", "B")),
            builder => builder.AddSheet("Data", sheet => PopulateSixRows(sheet, "A", "B")));
        var options = new AlignmentSuggestionOptions
        {
            MaxRowsPerSheet = 2,
            MaxColumnsPerSheet = 1,
            MaxSuggestions = 5,
            MaxSamples = 1,
        };

        var result = await new AlignmentSuggestionService().AnalyzeAsync(
            pair.Left,
            pair.Right,
            "Data",
            "Data",
            options);

        Assert.Equal(2, result.LeftSampledRows);
        Assert.Equal(2, result.RightSampledRows);
        Assert.True(result.LeftRowsTruncated);
        Assert.True(result.RightRowsTruncated);
        Assert.True(result.LeftColumnsTruncated);
        Assert.True(result.RightColumnsTruncated);
        Assert.All(result.Suggestions, suggestion =>
        {
            Assert.DoesNotContain("B", suggestion.LeftColumns);
            Assert.DoesNotContain("B", suggestion.RightColumns);
            Assert.True(suggestion.Samples.Count <= 1);
        });
    }

    [Fact]
    public async Task ConstantColumnsAreNeverOfferedAsApplicableMappings()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Data", sheet => sheet
                .Cell("A1", "LeftFlag", TestCellType.InlineString)
                .Cell("A2", "1").Cell("A3", "1").Cell("A4", "1").Cell("A5", "1")),
            builder => builder.AddSheet("Data", sheet => sheet
                .Cell("C1", "RightFlag", TestCellType.InlineString)
                .Cell("C2", "1").Cell("C3", "1").Cell("C4", "1").Cell("C5", "1")));

        var result = await new AlignmentSuggestionService().AnalyzeAsync(
            pair.Left,
            pair.Right,
            "Data",
            "Data");

        Assert.DoesNotContain(result.Suggestions, suggestion =>
            suggestion.Kind == AlignmentSuggestionKind.ColumnMapping &&
            suggestion.LeftColumns.SequenceEqual(["A"]) &&
            suggestion.RightColumns.SequenceEqual(["C"]));
    }

    [Fact]
    public async Task ValueTypesStayDistinctAndWhitespaceIsVisibleInSamples()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Data", sheet => sheet
                .Cell("A1", "Typed", TestCellType.InlineString)
                .Cell("B1", "WhitespaceID", TestCellType.InlineString)
                .Cell("A2", "1").Cell("B2", " X", TestCellType.InlineString)
                .Cell("A3", "2").Cell("B3", "X ", TestCellType.InlineString)),
            builder => builder.AddSheet("Data", sheet => sheet
                .Cell("C1", "Typed", TestCellType.InlineString)
                .Cell("D1", "WhitespaceID", TestCellType.InlineString)
                .Cell("C2", "1", TestCellType.InlineString).Cell("D2", " X", TestCellType.InlineString)
                .Cell("C3", "2", TestCellType.InlineString).Cell("D3", "X ", TestCellType.InlineString)));

        var result = await new AlignmentSuggestionService().AnalyzeAsync(
            pair.Left,
            pair.Right,
            "Data",
            "Data");

        Assert.DoesNotContain(result.Suggestions, suggestion =>
            suggestion.LeftColumns.SequenceEqual(["A"]) &&
            suggestion.RightColumns.SequenceEqual(["C"]));
        var whitespaceKey = Assert.Single(result.Suggestions, suggestion =>
            suggestion.Kind == AlignmentSuggestionKind.KeyColumns &&
            suggestion.LeftColumns.SequenceEqual(["B"]) &&
            suggestion.RightColumns.SequenceEqual(["D"]));
        Assert.Contains(whitespaceKey.Samples, sample => sample.Contains('␠'));
    }

    [Fact]
    public async Task HonorsCancellationBeforeReading()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Data", sheet => sheet.Cell("A1", "ID", TestCellType.InlineString)),
            builder => builder.AddSheet("Data", sheet => sheet.Cell("A1", "ID", TestCellType.InlineString)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AlignmentSuggestionService().AnalyzeAsync(
                pair.Left,
                pair.Right,
                "Data",
                "Data",
                cancellationToken: cancellation.Token));
    }

    private static void PopulateSixRows(TestSheet sheet, string firstColumn, string secondColumn)
    {
        sheet
            .Cell($"{firstColumn}1", "ID", TestCellType.InlineString)
            .Cell($"{secondColumn}1", "Ignored", TestCellType.InlineString);
        for (var row = 2; row <= 7; row++)
        {
            sheet
                .Cell($"{firstColumn}{row}", row.ToString())
                .Cell($"{secondColumn}{row}", $"ignored-{row}", TestCellType.InlineString);
        }
    }
}
