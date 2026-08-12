using System.Collections;
using System.Windows;
using System.Windows.Media;
using ZCompare.App.ViewModels;
using ZCompare.Core;

namespace ZCompare.App.Tests;

public sealed class PresentationModelsTests
{
    private const string ClippedSuffix = "\u2026\uFF08\u53CC\u51FB\u67E5\u770B\u5B8C\u6574\u8BE6\u60C5\uFF09";

    [Fact]
    public void OneSidedWorksheetHeaderShowsPendingMarkerThenBackfilledCount()
    {
        var tab = new WorksheetTabViewModel
        {
            Name = "NewSheet",
            IsOneSided = true,
            DifferenceCount = null,
        };

        Assert.Equal("NewSheet (+)", tab.Header);
        tab.DifferenceCount = 7;
        Assert.Equal("NewSheet (7)", tab.Header);
    }

    [Fact]
    public void WorksheetDifferenceMarkerSeparatesChangedAndUnchangedTabs()
    {
        var unchanged = new WorksheetTabViewModel
        {
            Name = "SameSheet",
            IsOneSided = false,
            DifferenceCount = 0,
        };
        var changed = new WorksheetTabViewModel
        {
            Name = "ChangedSheet",
            IsOneSided = false,
            DifferenceCount = 2,
        };
        var oneSided = new WorksheetTabViewModel
        {
            Name = "NewSheet",
            IsOneSided = true,
            DifferenceCount = null,
        };

        Assert.False(unchanged.HasDifferences);
        Assert.Equal("SameSheet", unchanged.Header);
        Assert.True(changed.HasDifferences);
        Assert.Equal("ChangedSheet (2)", changed.Header);
        Assert.True(oneSided.HasDifferences);
    }

    [Fact]
    public void WorksheetDifferenceMarkerNotifiesWhenLoadedCountChanges()
    {
        var tab = new WorksheetTabViewModel
        {
            Name = "Sheet1",
            IsOneSided = false,
            DifferenceCount = 0,
        };
        var notifications = new List<string?>();
        tab.PropertyChanged += (_, eventArgs) => notifications.Add(eventArgs.PropertyName);

        tab.DifferenceCount = 3;

        Assert.True(tab.HasDifferences);
        Assert.Contains(nameof(WorksheetTabViewModel.HasDifferences), notifications);
        Assert.Contains(nameof(WorksheetTabViewModel.Header), notifications);
    }

    [Fact]
    public void TwoSidedDifferenceColorsWholeRowAndOnlyExactCellText()
    {
        var leftCell = TestViewModels.Cell("B2", "left");
        var rightCell = TestViewModels.Cell("B2", "right");
        var difference = new Difference(
            DifferenceKind.Value,
            "Sheet1",
            "B2",
            "Value differs.",
            leftCell,
            rightCell,
            "left",
            "right");
        var viewport = new GridViewportViewModel();

        viewport.SetPreviews(
            TestViewModels.Preview(TestViewModels.Cell("A2", "same"), leftCell),
            TestViewModels.Preview(TestViewModels.Cell("A2", "same"), rightCell),
            [difference],
            differencesOnly: false);

        var leftRows = Assert.IsAssignableFrom<IList>(viewport.LeftRows);
        var rightRows = Assert.IsAssignableFrom<IList>(viewport.RightRows);
        var leftRow = Assert.IsType<GridRowViewModel>(leftRows[1]);
        var rightRow = Assert.IsType<GridRowViewModel>(rightRows[1]);
        Assert.True(leftRow.IsDifferent);
        Assert.True(rightRow.IsDifferent);

        var unchangedLeft = leftRow.Cells[0];
        var changedLeft = leftRow.Cells[1];
        var changedRight = rightRow.Cells[1];
        AssertBrushColor(unchangedLeft.Background, 255, 241, 242);
        AssertBrushColor(changedLeft.Background, 252, 165, 165);
        Assert.Equal(FontWeights.SemiBold, changedLeft.FontWeight);
        Assert.False(unchangedLeft.IsDifferent);
        AssertBrushColor(unchangedLeft.Foreground, 0, 0, 0);
        Assert.True(changedLeft.IsDifferent);
        Assert.True(changedLeft.IsValueDifferent);
        Assert.True(changedRight.IsDifferent);
        AssertBrushColor(changedLeft.Foreground, 185, 28, 28);
        AssertBrushColor(changedRight.Foreground, 185, 28, 28);
    }

    [Fact]
    public void OneSidedWorksheetColorsExistingAndMissingCellsAsDifferences()
    {
        var viewport = new GridViewportViewModel();
        viewport.SetPreviews(
            TestViewModels.Preview(TestViewModels.Cell("A4", "new")),
            null,
            [],
            differencesOnly: false);

        var left = Assert.IsType<GridRowViewModel>(Assert.IsAssignableFrom<IList>(viewport.LeftRows)[3]);
        var right = Assert.IsType<GridRowViewModel>(Assert.IsAssignableFrom<IList>(viewport.RightRows)[3]);
        var existing = left.Cells[0];
        var missing = right.Cells[0];

        Assert.True(left.IsDifferent);
        Assert.True(right.IsDifferent);
        Assert.True(existing.IsDifferent);
        Assert.False(existing.IsMissing);
        Assert.True(missing.IsDifferent);
        Assert.True(missing.IsMissing);
        AssertBrushColor(existing.Background, 252, 165, 165);
        Assert.Equal(FontWeights.SemiBold, existing.FontWeight);
        AssertBrushColor(missing.Background, 248, 250, 252);
        AssertBrushColor(existing.Foreground, 185, 28, 28);
        AssertBrushColor(missing.Foreground, 185, 28, 28);
    }

    [Fact]
    public void AlignedRowsUseDisplaySlotsAndExposeOneSidedPlaceholders()
    {
        var leftAlpha = TestViewModels.Cell("A1", "alpha");
        var leftBravo = TestViewModels.Cell("A2", "bravo");
        var rightInserted = TestViewModels.Cell("A1", "inserted");
        var rightAlpha = TestViewModels.Cell("A2", "alpha");
        var rightBravo = TestViewModels.Cell("A3", "bravo");
        var insertedDifference = new Difference(
            DifferenceKind.RowInserted,
            "Sheet1",
            "A1",
            "Row inserted.",
            null,
            rightInserted,
            null,
            "row 1");
        RowAlignment[] alignments =
        [
            new(1, null, 1, RowAlignmentStatus.Inserted, "right row inserted"),
            new(2, 1, 2, RowAlignmentStatus.Matched),
            new(3, 2, 3, RowAlignmentStatus.Matched),
        ];
        var viewport = new GridViewportViewModel();

        viewport.SetPreviews(
            TestViewModels.Preview(leftAlpha, leftBravo),
            TestViewModels.Preview(rightInserted, rightAlpha, rightBravo),
            [insertedDifference],
            alignments,
            differencesOnly: false);

        var leftRows = Assert.IsAssignableFrom<IList>(viewport.LeftRows);
        var rightRows = Assert.IsAssignableFrom<IList>(viewport.RightRows);
        Assert.Equal(leftRows.Count, rightRows.Count);
        Assert.True(leftRows.Count >= alignments.Length);
        var leftPlaceholder = Assert.IsType<GridRowViewModel>(leftRows[0]);
        var rightInsertedRow = Assert.IsType<GridRowViewModel>(rightRows[0]);
        var alignedLeftAlpha = Assert.IsType<GridRowViewModel>(leftRows[1]);
        var alignedRightAlpha = Assert.IsType<GridRowViewModel>(rightRows[1]);

        Assert.True(leftPlaceholder.IsPlaceholder);
        Assert.Equal(0, leftPlaceholder.RowNumber);
        Assert.Null(leftPlaceholder.LeftRowNumber);
        Assert.Equal(1, leftPlaceholder.RightRowNumber);
        Assert.Equal(RowAlignmentStatus.Inserted, leftPlaceholder.AlignmentStatus);
        Assert.False(rightInsertedRow.IsPlaceholder);
        Assert.Equal("inserted", rightInsertedRow.Cells[0].RawValue);
        Assert.Equal(1, alignedLeftAlpha.RowNumber);
        Assert.Equal(2, alignedLeftAlpha.DisplayRowNumber);
        Assert.Equal("alpha", alignedLeftAlpha.Cells[0].RawValue);
        Assert.Equal(2, alignedRightAlpha.RowNumber);
        Assert.Equal("alpha", alignedRightAlpha.Cells[0].RawValue);
        Assert.Equal(0, viewport.GetDisplayRowIndex(1));
    }

    [Fact]
    public void ToolTipClipsLongDetailsDisplayAndRawValues()
    {
        var cell = new GridCellViewModel
        {
            Address = "A1",
            DifferenceDetails = new string('D', 600),
            DisplayValue = new string('V', 220),
            RawValue = new string('R', 220),
        };

        var toolTip = cell.ToolTip;

        Assert.Contains(new string('D', 480) + ClippedSuffix, toolTip, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('D', 481), toolTip, StringComparison.Ordinal);
        Assert.Contains(new string('V', 160) + ClippedSuffix, toolTip, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('V', 161), toolTip, StringComparison.Ordinal);
        Assert.Contains(new string('R', 160) + ClippedSuffix, toolTip, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('R', 161), toolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void WarningRowExposesVisibleSummaryAndCompleteDistinctDetails()
    {
        var item = new FolderFileItemViewModel(new FolderFileResult(
            "book.xlsx",
            "left.xlsx",
            "right.xlsx",
            ComparisonStatus.Pending,
            0,
            null,
            null));
        var warningDifference = new Difference(
            DifferenceKind.Warning,
            null,
            null,
            "Workbook calculation mode may be stale.",
            null,
            null,
            null,
            null);
        var uncomparedDifference = new Difference(
            DifferenceKind.UncomparedObject,
            "Sheet1",
            null,
            "Chart objects were not compared.",
            null,
            null,
            null,
            null);
        var comparison = new WorkbookCompareResult(
            "left.xlsx",
            "right.xlsx",
            ComparisonStatus.Warning,
            [],
            [warningDifference, uncomparedDifference],
            ["Cached formula result may be stale.", "Cached formula result may be stale."],
            false,
            "left-sha",
            "right-sha",
            TimeSpan.Zero);

        item.ApplyComparison(comparison);

        Assert.True(item.HasIssueDetails);
        Assert.Equal("Cached formula result may be stale.", item.IssueSummary);
        Assert.NotNull(item.IssueDetails);
        Assert.Contains("Cached formula result may be stale.", item.IssueDetails, StringComparison.Ordinal);
        Assert.Contains("Workbook calculation mode may be stale.", item.IssueDetails, StringComparison.Ordinal);
        Assert.Contains("Chart objects were not compared.", item.IssueDetails, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(item.IssueDetails!, "Cached formula result may be stale."));
        Assert.Contains(item.IssueDetails!, item.StatusToolTip, StringComparison.Ordinal);
        Assert.Same(comparison, item.WorkbookComparison);
    }

    [Fact]
    public void ErrorRowExposesFirstLineSummaryAndFullErrorDetails()
    {
        var item = new FolderFileItemViewModel(new FolderFileResult(
            "book.xlsx",
            "left.xlsx",
            "right.xlsx",
            ComparisonStatus.Pending,
            0,
            null,
            null));

        item.ApplyError("Package is damaged.\r\nCentral directory cannot be read.");

        Assert.True(item.HasIssueDetails);
        Assert.Equal("Package is damaged.", item.IssueSummary);
        Assert.Equal(
            "Package is damaged.\r\nCentral directory cannot be read.",
            item.IssueDetails);
        Assert.Contains("Central directory cannot be read.", item.StatusToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void WarningRowIncludesWorksheetLevelWarningDetails()
    {
        var item = new FolderFileItemViewModel(new FolderFileResult(
            "book.xlsx",
            "left.xlsx",
            "right.xlsx",
            ComparisonStatus.Pending,
            0,
            null,
            null));
        var warning = new Difference(
            DifferenceKind.Warning,
            "Sheet1",
            "C8",
            "Formula cache is missing at C8.",
            null,
            null,
            null,
            null);
        var worksheet = new WorksheetCompareResult(
            "Sheet1",
            ComparisonStatus.Warning,
            1,
            [warning],
            1,
            1);
        var comparison = new WorkbookCompareResult(
            "left.xlsx",
            "right.xlsx",
            ComparisonStatus.Warning,
            [worksheet],
            [],
            [],
            false,
            "left-sha",
            "right-sha",
            TimeSpan.Zero);

        item.ApplyComparison(comparison);

        Assert.True(item.HasIssueDetails);
        Assert.Equal("Formula cache is missing at C8.", item.IssueSummary);
        Assert.Contains("Formula cache is missing at C8.", item.IssueDetails, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string expected)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(expected, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += expected.Length;
        }

        return count;
    }

    private static void AssertBrushColor(Brush brush, byte red, byte green, byte blue)
    {
        var solid = Assert.IsType<SolidColorBrush>(brush);
        Assert.Equal(Color.FromRgb(red, green, blue), solid.Color);
    }
}
