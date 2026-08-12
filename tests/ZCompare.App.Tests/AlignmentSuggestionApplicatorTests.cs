using System.IO;
using ZCompare.Core;

namespace ZCompare.App.Tests;

public sealed class AlignmentSuggestionApplicatorTests
{
    [Fact]
    public void LegacySettingsWindowConstructorRemainsAvailable()
    {
        Assert.NotNull(typeof(WorksheetSettingsWindow).GetConstructor([typeof(ComparisonOptions)]));
    }

    [Fact]
    public void KeyColumnSuggestionUpsertsBothActualWorksheetRules()
    {
        var keyRules = new List<KeyRuleEditorRow>
        {
            new() { WorksheetName = "LeftData", HeaderRow = "1", Columns = "Z" },
            new() { WorksheetName = "Unrelated", HeaderRow = "4", Columns = "A" },
        };
        var mappings = new List<ColumnMappingEditorRow>();
        var suggestion = CreateSuggestion(
            AlignmentSuggestionKind.KeyColumns,
            leftColumns: ["a", "C"],
            rightColumns: ["b", "D"]);

        var result = AlignmentSuggestionApplicator.Apply(
            [suggestion],
            "LeftData",
            "RightData",
            2,
            3,
            keyRules,
            mappings);

        Assert.Equal(1, result.AppliedSuggestionCount);
        Assert.True(result.AppliedKeyColumns);
        Assert.False(result.AppliedColumnMappings);
        Assert.Collection(
            keyRules.OrderBy(static row => row.WorksheetName),
            row =>
            {
                Assert.Equal("LeftData", row.WorksheetName);
                Assert.Equal("2", row.HeaderRow);
                Assert.Equal("A,C", row.Columns);
            },
            row =>
            {
                Assert.Equal("RightData", row.WorksheetName);
                Assert.Equal("3", row.HeaderRow);
                Assert.Equal("B,D", row.Columns);
            },
            row => Assert.Equal("Unrelated", row.WorksheetName));
    }

    [Fact]
    public void ColumnMappingSuggestionMergesWithoutDuplicateLeftOrRightColumns()
    {
        var keyRules = new List<KeyRuleEditorRow>();
        var mappings = new List<ColumnMappingEditorRow>
        {
            new()
            {
                LeftWorksheetName = "LeftData",
                RightWorksheetName = "RightData",
                ColumnPairs = "A=A,C=D",
            },
        };
        var suggestion = CreateSuggestion(
            AlignmentSuggestionKind.ColumnMapping,
            columnPairs: [new ColumnPair("b", "c"), new ColumnPair("c", "e")]);

        var result = AlignmentSuggestionApplicator.Apply(
            [suggestion],
            "LeftData",
            "RightData",
            1,
            1,
            keyRules,
            mappings);

        Assert.Equal(1, result.AppliedSuggestionCount);
        Assert.True(result.AppliedColumnMappings);
        var mapping = Assert.Single(mappings);
        Assert.Equal("A=A,B=C,C=E", mapping.ColumnPairs);
    }

    [Fact]
    public void GroupingSuggestionRemainsReadOnlyEvenIfPassedToApplicator()
    {
        var suggestion = CreateSuggestion(
            AlignmentSuggestionKind.GroupingColumn,
            leftColumns: ["A"],
            rightColumns: ["A"],
            canApply: false);
        var keyRules = new List<KeyRuleEditorRow>();
        var mappings = new List<ColumnMappingEditorRow>();

        var result = AlignmentSuggestionApplicator.Apply(
            [suggestion],
            "Sheet1",
            "Sheet1",
            1,
            1,
            keyRules,
            mappings);

        Assert.Equal(0, result.AppliedSuggestionCount);
        Assert.Equal(1, result.SkippedSuggestionCount);
        Assert.Empty(keyRules);
        Assert.Empty(mappings);
    }

    [Fact]
    public void DifferentKeyColumnsForSameNamedWorksheetAreNotAppliedAmbiguously()
    {
        var suggestion = CreateSuggestion(
            AlignmentSuggestionKind.KeyColumns,
            leftColumns: ["A"],
            rightColumns: ["B"]);
        var keyRules = new List<KeyRuleEditorRow>();

        var result = AlignmentSuggestionApplicator.Apply(
            [suggestion],
            "Sheet1",
            "sheet1",
            1,
            1,
            keyRules,
            new List<ColumnMappingEditorRow>());

        Assert.Equal(0, result.AppliedSuggestionCount);
        Assert.Equal(1, result.SkippedSuggestionCount);
        Assert.Contains("无法无歧义表示", result.Message, StringComparison.Ordinal);
        Assert.Empty(keyRules);
    }

    [Fact]
    public void MultipleKeyColumnCandidatesApplyOnlyTheFirstAlternative()
    {
        var first = CreateSuggestion(
            AlignmentSuggestionKind.KeyColumns,
            leftColumns: ["A"],
            rightColumns: ["B"]);
        var second = CreateSuggestion(
            AlignmentSuggestionKind.KeyColumns,
            leftColumns: ["C"],
            rightColumns: ["D"]);
        var keyRules = new List<KeyRuleEditorRow>();

        var result = AlignmentSuggestionApplicator.Apply(
            [first, second],
            "LeftData",
            "RightData",
            1,
            1,
            keyRules,
            new List<ColumnMappingEditorRow>());

        Assert.Equal(1, result.AppliedSuggestionCount);
        Assert.Equal(1, result.SkippedSuggestionCount);
        Assert.Contains("互为替代方案", result.Message, StringComparison.Ordinal);
        Assert.Equal("A", Assert.Single(keyRules, row => row.WorksheetName == "LeftData").Columns);
        Assert.Equal("B", Assert.Single(keyRules, row => row.WorksheetName == "RightData").Columns);
    }

    [Fact]
    public void SuggestionRowsStartUnselectedAndExposeMetricsAndSamples()
    {
        var row = new AlignmentSuggestionEditorRow(CreateSuggestion(
            AlignmentSuggestionKind.ColumnMapping,
            columnPairs: [new ColumnPair("A", "B")],
            samples: ["id=42"]));

        Assert.False(row.IsSelected);
        Assert.True(row.CanApply);
        Assert.Equal("列映射", row.KindDisplay);
        Assert.Equal("92%", row.ConfidenceDisplay);
        Assert.Contains("样本行：左 10 / 右 11", row.Details, StringComparison.Ordinal);
        Assert.Contains("id=42", row.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void SuggestionPathValidationExplainsMissingAndFolderPaths()
    {
        Assert.False(WorksheetSettingsWindow.TryValidateSuggestionPath("", "左侧", out var emptyError));
        Assert.Contains("路径为空", emptyError, StringComparison.Ordinal);

        var directory = Path.Combine(Path.GetTempPath(), "ZCompare.App.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.False(WorksheetSettingsWindow.TryValidateSuggestionPath(directory, "右侧", out var folderError));
            Assert.Contains("当前是文件夹", folderError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void CompletedStatusWarnsWhenRowOrColumnSamplesAreTruncated()
    {
        var result = new AlignmentSuggestionResult(
            "LeftData",
            "RightData",
            5_000,
            4_200,
            true,
            false,
            false,
            true,
            []);

        var status = AlignmentSuggestionPresentation.FormatCompletedStatus(result);

        Assert.Contains("左侧前 5000 行", status, StringComparison.Ordinal);
        Assert.Contains("右侧列样本已截断", status, StringComparison.Ordinal);
        Assert.Contains("置信度不代表全表", status, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletedStatusOmitsTruncationWarningForCompleteSamples()
    {
        var result = new AlignmentSuggestionResult(
            "LeftData",
            "RightData",
            25,
            25,
            false,
            false,
            false,
            false,
            [CreateSuggestion(AlignmentSuggestionKind.KeyColumns)]);

        var status = AlignmentSuggestionPresentation.FormatCompletedStatus(result);

        Assert.Contains("共 1 条建议", status, StringComparison.Ordinal);
        Assert.DoesNotContain("截断", status, StringComparison.Ordinal);
        Assert.DoesNotContain("不代表全表", status, StringComparison.Ordinal);
    }

    private static AlignmentSuggestion CreateSuggestion(
        AlignmentSuggestionKind kind,
        IReadOnlyList<string>? leftColumns = null,
        IReadOnlyList<string>? rightColumns = null,
        IReadOnlyList<ColumnPair>? columnPairs = null,
        IReadOnlyList<string>? samples = null,
        bool canApply = true) =>
        new(
            "test",
            kind,
            "测试建议",
            92.4,
            "测试依据",
            leftColumns ?? [],
            rightColumns ?? [],
            columnPairs ?? [],
            1,
            1,
            10,
            11,
            95,
            96,
            100,
            99,
            94,
            samples ?? [],
            canApply);
}
