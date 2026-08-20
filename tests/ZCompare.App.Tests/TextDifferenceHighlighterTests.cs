using ZCompare.App.ViewModels;
using ZCompare.Core;

namespace ZCompare.App.Tests;

public sealed class TextDifferenceHighlighterTests
{
    [Fact]
    public void HighlightsOnlyChangedCharacters()
    {
        var left = TextDifferenceHighlighter.CreateSegments("1002", "1004", true);
        var right = TextDifferenceHighlighter.CreateSegments("1004", "1002", true);

        AssertSegments(left, ("100", false), ("2", true));
        AssertSegments(right, ("100", false), ("4", true));
    }

    [Fact]
    public void KeepsSeparateChangedRegionsPrecise()
    {
        var left = TextDifferenceHighlighter.CreateSegments("abcXdefYghi", "abcZdefWghi", true);

        AssertSegments(left,
            ("abc", false),
            ("X", true),
            ("def", false),
            ("Y", true),
            ("ghi", false));
    }

    [Fact]
    public void InsertionHighlightsOnlyTheSideContainingCharacters()
    {
        var left = TextDifferenceHighlighter.CreateSegments("abcd", "abXcd", true);
        var right = TextDifferenceHighlighter.CreateSegments("abXcd", "abcd", true);

        AssertSegments(left, ("abcd", false));
        AssertSegments(right, ("ab", false), ("X", true), ("cd", false));
    }

    [Fact]
    public void DisabledHighlightingDoesNotInventCaseDifferences()
    {
        var segments = TextDifferenceHighlighter.CreateSegments("Alpha", "alpha", false);

        AssertSegments(segments, ("Alpha", false));
    }

    [Fact]
    public void EmojiRemainValidTextWhenTheyDiffer()
    {
        var segments = TextDifferenceHighlighter.CreateSegments("a😀b", "a😁b", true);

        Assert.Equal("a😀b", string.Concat(segments.Select(static segment => segment.Text)));
        Assert.Contains(segments, static segment => segment.Text == "😀" && segment.IsDifferent);
    }

    [Fact]
    public void LongValuesWithSparseChangesRemainPrecise()
    {
        var sharedMiddle = new string('m', 8_000);
        var leftText = "start-A-" + sharedMiddle + "-B-end";
        var rightText = "start-X-" + sharedMiddle + "-Y-end";

        var segments = TextDifferenceHighlighter.CreateSegments(leftText, rightText, true);

        Assert.Equal(leftText, string.Concat(segments.Select(static segment => segment.Text)));
        Assert.Equal(["A", "B"], segments.Where(static segment => segment.IsDifferent).Select(static segment => segment.Text));
    }

    [Fact]
    public void FullCellDetailsContainUnclippedPlainValuesWithoutMarkerCharacters()
    {
        var prefix = new string('x', 240);
        var leftCell = TestViewModels.Cell("C7", prefix + "1002");
        var rightCell = TestViewModels.Cell("C7", prefix + "1004");
        var difference = new Difference(
            DifferenceKind.Value,
            "Sheet1",
            "C7",
            "Value differs.",
            leftCell,
            rightCell,
            leftCell.RawValue,
            rightCell.RawValue);
        var viewModel = TestViewModels.CreateMainWindow();
        viewModel.GridViewport.SetPreviews(
            TestViewModels.Preview(leftCell),
            TestViewModels.Preview(rightCell),
            [difference],
            differencesOnly: false);

        viewModel.SelectGridCell(6, 2);
        var details = viewModel.GetCellDialogDetails(6, 2);

        Assert.True(viewModel.HasSelectedCell);
        Assert.Contains(prefix + "1002", details, StringComparison.Ordinal);
        Assert.Contains(prefix + "1004", details, StringComparison.Ordinal);
        Assert.DoesNotContain("⟦", details, StringComparison.Ordinal);
        Assert.DoesNotContain("⟧", details, StringComparison.Ordinal);
        Assert.Contains("【差异说明】", details, StringComparison.Ordinal);
    }

    [Fact]
    public void RawAndDisplayValuesReconstructFromColoredSegmentsForBothSides()
    {
        AssertColoredPair("raw-1002", "raw-1004", "2", "4");
        AssertColoredPair("¥1,002.00", "¥1,004.00", "2", "4");
    }

    [Fact]
    public void CellDetailsKeepRealBracketsAndTrailingSpacesWithoutSyntheticMarkers()
    {
        const string leftRaw = "raw⟦1002⟧  ";
        const string rightRaw = "raw⟦1004⟧ ";
        const string leftDisplay = "display⟦1,002⟧  ";
        const string rightDisplay = "display⟦1,004⟧ ";
        var leftCell = TestViewModels.Cell("C7", leftRaw) with { DisplayValue = leftDisplay };
        var rightCell = TestViewModels.Cell("C7", rightRaw) with { DisplayValue = rightDisplay };
        var content = CreateDetailsContent(leftCell, rightCell);
        var displayText = string.Concat(content.Segments.Select(static segment => segment.DisplayText));

        Assert.Equal(
            content.ClipboardText,
            string.Concat(content.Segments.Select(static segment => segment.ClipboardText)));
        Assert.Contains(leftRaw + Environment.NewLine + "显示值：", content.ClipboardText, StringComparison.Ordinal);
        Assert.Contains(rightRaw + Environment.NewLine + "显示值：", content.ClipboardText, StringComparison.Ordinal);
        Assert.Contains(leftDisplay, content.ClipboardText, StringComparison.Ordinal);
        Assert.Contains(rightDisplay, content.ClipboardText, StringComparison.Ordinal);
        Assert.Contains(leftRaw, displayText, StringComparison.Ordinal);
        Assert.Contains(rightDisplay, displayText, StringComparison.Ordinal);
        Assert.DoesNotContain("100⟦2⟧", content.ClipboardText, StringComparison.Ordinal);
        Assert.DoesNotContain("100⟦4⟧", content.ClipboardText, StringComparison.Ordinal);
        Assert.Contains(content.Segments, static segment => segment.IsDifferent && segment.ClipboardText == "2");
        Assert.Contains(content.Segments, static segment => segment.IsDifferent && segment.ClipboardText == "4");
    }

    [Fact]
    public void CellDetailsVisualizeWhitespaceWithoutChangingClipboardText()
    {
        var leftCell = TestViewModels.Cell("C7", " \t ");
        var rightCell = TestViewModels.Cell("C7", " \t");
        var content = CreateDetailsContent(leftCell, rightCell);
        var displayText = string.Concat(content.Segments.Select(static segment => segment.DisplayText));
        var lineBreak = Environment.NewLine;
        var clipboardValueText = content.ClipboardText.Split("【差异说明】", StringSplitOptions.None)[0];

        Assert.Contains($"原始值：{lineBreak} \t {lineBreak}显示值：{lineBreak} \t ", content.ClipboardText, StringComparison.Ordinal);
        Assert.Contains($"原始值：{lineBreak}␠⇥␠{lineBreak}显示值：{lineBreak}␠⇥␠", displayText, StringComparison.Ordinal);
        Assert.DoesNotContain("␠", clipboardValueText, StringComparison.Ordinal);
        Assert.DoesNotContain("⇥", clipboardValueText, StringComparison.Ordinal);
        Assert.Contains(content.Segments, static segment =>
            segment.IsDifferent && segment.DisplayText == "␠" && segment.ClipboardText == " ");
    }

    private static void AssertSegments(
        IReadOnlyList<TextDifferenceSegment> actual,
        params (string Text, bool IsDifferent)[] expected)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Text, actual[index].Text);
            Assert.Equal(expected[index].IsDifferent, actual[index].IsDifferent);
        }
    }

    private static void AssertColoredPair(
        string left,
        string right,
        string leftDifference,
        string rightDifference)
    {
        var leftSegments = TextDifferenceHighlighter.CreateSegments(left, right, true);
        var rightSegments = TextDifferenceHighlighter.CreateSegments(right, left, true);

        Assert.Equal(left, string.Concat(leftSegments.Select(static segment => segment.Text)));
        Assert.Equal(right, string.Concat(rightSegments.Select(static segment => segment.Text)));
        Assert.Equal(leftDifference, string.Concat(
            leftSegments.Where(static segment => segment.IsDifferent).Select(static segment => segment.Text)));
        Assert.Equal(rightDifference, string.Concat(
            rightSegments.Where(static segment => segment.IsDifferent).Select(static segment => segment.Text)));
    }

    private static CellDetailsContent CreateDetailsContent(CellSnapshot leftCell, CellSnapshot rightCell)
    {
        var difference = new Difference(
            DifferenceKind.Value,
            "Sheet1",
            "C7",
            "Value differs.",
            leftCell,
            rightCell,
            leftCell.RawValue,
            rightCell.RawValue);
        var viewModel = TestViewModels.CreateMainWindow();
        viewModel.GridViewport.SetPreviews(
            TestViewModels.Preview(leftCell),
            TestViewModels.Preview(rightCell),
            [difference],
            differencesOnly: false);

        return Assert.IsType<CellDetailsContent>(viewModel.GetCellDetailsContent(6, 2));
    }
}
