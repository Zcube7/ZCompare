using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using ZCompare.App.ViewModels;
using ZCompare.Core;

namespace ZCompare.App.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void ComparisonOptionsDefaultToSavedValuesAndCaseSensitiveText()
    {
        var viewModel = TestViewModels.CreateMainWindow();

        Assert.False(viewModel.CompareFormulas);
        Assert.False(viewModel.CompareFormatting);
        Assert.False(viewModel.CompareFonts);
        Assert.False(viewModel.CompareComments);
        Assert.False(viewModel.CompareHyperlinks);
        Assert.False(viewModel.CompareLayout);
        Assert.True(viewModel.CaseSensitive);
        Assert.False(viewModel.StrictRowNumberComparison);
        Assert.False(viewModel.IncludeSubdirectories);
    }

    [Fact]
    public void FolderMarksSurviveSearchAndAllDifferenceViewChanges()
    {
        var viewModel = TestViewModels.CreateMainWindow();
        viewModel.IsFolderMode = true;
        var same = FolderItem("alpha-book.xlsx", ComparisonStatus.Same);
        var different = FolderItem("beta-book.xlsx", ComparisonStatus.Different);
        var other = FolderItem("gamma.xlsx", ComparisonStatus.Pending);
        viewModel.FolderFiles.Add(same);
        viewModel.FolderFiles.Add(different);
        viewModel.FolderFiles.Add(other);

        same.IsMarkedForComparison = true;
        different.IsMarkedForComparison = true;
        viewModel.FolderSearchText = "book";
        viewModel.ShowFolderDifferencesOnly = true;
        viewModel.ShowAllFolderFiles = true;

        Assert.True(same.IsMarkedForComparison);
        Assert.True(different.IsMarkedForComparison);
        Assert.False(other.IsMarkedForComparison);
        Assert.Contains("已选 2 项", viewModel.FolderSelectionSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void FolderSearchFiltersRowsAndClearingRestoresAll()
    {
        var viewModel = TestViewModels.CreateMainWindow();
        viewModel.IsFolderMode = true;
        var first = FolderItem("alpha-book.xlsx", ComparisonStatus.Same);
        var second = FolderItem("beta-book.xlsx", ComparisonStatus.Different);
        var nonMatch = FolderItem("gamma.xlsx", ComparisonStatus.Pending);
        viewModel.FolderFiles.Add(first);
        viewModel.FolderFiles.Add(second);
        viewModel.FolderFiles.Add(nonMatch);

        viewModel.FolderSearchText = "book";

        Assert.Equal(
            ["alpha-book.xlsx", "beta-book.xlsx"],
            VisibleFolderPaths(viewModel));
        viewModel.FolderSearchText = string.Empty;

        Assert.Equal(
            ["alpha-book.xlsx", "beta-book.xlsx", "gamma.xlsx"],
            VisibleFolderPaths(viewModel));
    }

    [Fact]
    public void FolderSearchAndDifferenceViewUseTheirIntersection()
    {
        var viewModel = TestViewModels.CreateMainWindow();
        viewModel.IsFolderMode = true;
        viewModel.FolderFiles.Add(FolderItem("alpha-book.xlsx", ComparisonStatus.Same));
        viewModel.FolderFiles.Add(FolderItem("beta-book.xlsx", ComparisonStatus.Different));
        viewModel.FolderFiles.Add(FolderItem("gamma.xlsx", ComparisonStatus.Different));

        viewModel.FolderSearchText = "book";
        viewModel.ShowFolderDifferencesOnly = true;

        Assert.Equal(["beta-book.xlsx"], VisibleFolderPaths(viewModel));

        viewModel.FolderSearchText = string.Empty;

        Assert.Equal(
            ["beta-book.xlsx", "gamma.xlsx"],
            VisibleFolderPaths(viewModel));
    }

    [Fact]
    public void SearchAndUnmarkingOneMatchPreserveHiddenMarks()
    {
        var viewModel = TestViewModels.CreateMainWindow();
        viewModel.IsFolderMode = true;
        var firstMatch = FolderItem("alpha-book.xlsx", ComparisonStatus.Pending);
        var secondMatch = FolderItem("beta-book.xlsx", ComparisonStatus.Pending);
        var hidden = FolderItem("gamma.xlsx", ComparisonStatus.Pending);
        viewModel.FolderFiles.Add(firstMatch);
        viewModel.FolderFiles.Add(secondMatch);
        viewModel.FolderFiles.Add(hidden);

        viewModel.SelectAllFolderFiles();
        viewModel.FolderSearchText = "book";
        firstMatch.IsMarkedForComparison = false;

        Assert.Equal(
            ["alpha-book.xlsx", "beta-book.xlsx"],
            VisibleFolderPaths(viewModel));
        Assert.False(firstMatch.IsMarkedForComparison);
        Assert.True(secondMatch.IsMarkedForComparison);
        Assert.True(hidden.IsMarkedForComparison);
    }

    [Fact]
    public void SearchWithNoResultsIsEmptyAndDoesNotChangeMarks()
    {
        var viewModel = TestViewModels.CreateMainWindow();
        viewModel.IsFolderMode = true;
        var marked = FolderItem("alpha.xlsx", ComparisonStatus.Pending);
        var unmarked = FolderItem("beta.xlsx", ComparisonStatus.Pending);
        viewModel.FolderFiles.Add(marked);
        viewModel.FolderFiles.Add(unmarked);
        marked.IsMarkedForComparison = true;

        viewModel.FolderSearchText = "not-found";

        Assert.Empty(VisibleFolderPaths(viewModel));
        Assert.True(marked.IsMarkedForComparison);
        Assert.False(unmarked.IsMarkedForComparison);
    }

    [Theory]
    [InlineData(false, RowAlignmentMode.Conservative)]
    [InlineData(true, RowAlignmentMode.StrictRowNumber)]
    public async Task StrictRowCheckboxMapsToComparisonOptions(
        bool strictRowNumberComparison,
        RowAlignmentMode expectedMode)
    {
        var comparer = new CapturingWorkbookComparer();
        var viewModel = TestViewModels.CreateMainWindow(workbookComparer: comparer);
        viewModel.IsFolderMode = true;
        viewModel.StrictRowNumberComparison = strictRowNumberComparison;
        var file = FolderItem("book.xlsx", ComparisonStatus.Pending);
        viewModel.FolderFiles.Add(file);
        viewModel.SetSelectedFolderFiles([file]);

        viewModel.CompareSelectedCommand.Execute(null);

        await WaitUntilAsync(() => comparer.LastOptions is not null);
        Assert.Equal(expectedMode, comparer.LastOptions!.RowAlignmentMode);
        await WaitUntilAsync(() => !viewModel.IsBusy);
    }

    [Fact]
    public void ChangingAPathInvalidatesDisplayedFolderResults()
    {
        var viewModel = TestViewModels.CreateMainWindow();
        viewModel.FolderFiles.Add(new FolderFileItemViewModel(new FolderFileResult(
            "book.xlsx",
            "left.xlsx",
            "right.xlsx",
            ComparisonStatus.Same,
            0,
            null,
            null)));

        viewModel.LeftPath = "changed";

        Assert.Empty(viewModel.FolderFiles);
        Assert.Equal(
            "\u8DEF\u5F84\u5DF2\u66F4\u6539\uFF0C\u8BF7\u91CD\u65B0\u626B\u63CF\u6216\u6BD4\u8F83",
            viewModel.StatusText);
    }

    [Fact]
    public async Task ComparingSelectedFolderFilesReportsCompletedFileCount()
    {
        var comparer = new ControlledWorkbookComparer();
        var viewModel = TestViewModels.CreateMainWindow(workbookComparer: comparer);
        viewModel.IsFolderMode = true;
        var files = Enumerable.Range(1, 3)
            .Select(index => new FolderFileItemViewModel(new FolderFileResult(
                $"book-{index}.xlsx",
                $"left-{index}.xlsx",
                $"right-{index}.xlsx",
                ComparisonStatus.Pending,
                0,
                null,
                null)))
            .ToArray();
        foreach (var file in files)
        {
            viewModel.FolderFiles.Add(file);
        }

        viewModel.SetSelectedFolderFiles(files);
        viewModel.CompareSelectedCommand.Execute(null);

        try
        {
            await comparer.WaitForStartedCountAsync(2);
            await WaitUntilAsync(() => viewModel.StatusText.Contains("0/3", StringComparison.Ordinal));

            comparer.Complete("left-1.xlsx");

            await comparer.WaitForStartedCountAsync(3);
            await WaitUntilAsync(() => viewModel.StatusText.Contains("1/3", StringComparison.Ordinal));
        }
        finally
        {
            comparer.CompleteAll();
            await WaitUntilAsync(() => !viewModel.IsBusy);
        }
    }

    [Fact]
    public async Task BatchProgressSeparatesFileNameAndPreservesFinalElapsedText()
    {
        var comparer = new ControlledWorkbookComparer();
        var viewModel = TestViewModels.CreateMainWindow(workbookComparer: comparer);
        viewModel.IsFolderMode = true;
        const string fileName = "very-long-configuration-workbook-name.xlsx";
        var relativePath = Path.Combine("nested", "configuration", fileName);
        var leftPath = Path.Combine("C:\\left-source", relativePath);
        var rightPath = Path.Combine("C:\\right-source", relativePath);
        var file = new FolderFileItemViewModel(new FolderFileResult(
            relativePath,
            leftPath,
            rightPath,
            ComparisonStatus.Pending,
            0,
            null,
            null));
        viewModel.FolderFiles.Add(file);
        viewModel.SetSelectedFolderFiles([file]);

        viewModel.CompareSelectedCommand.Execute(null);

        try
        {
            await comparer.WaitForStartedCountAsync(1);
            await WaitUntilAsync(() =>
                viewModel.StatusText.Contains("0/1", StringComparison.Ordinal) &&
                viewModel.StatusText.Contains("reading", StringComparison.Ordinal));

            Assert.Equal(fileName, viewModel.CurrentProgressFileName);
            Assert.DoesNotContain(relativePath, viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(fileName, viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(leftPath, viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Matches("^耗时 [0-9]{2}:[0-9]{2}:[0-9]{2}$", viewModel.OperationElapsedText);

            await Task.Delay(TimeSpan.FromMilliseconds(1_100));
            comparer.Complete(leftPath);
            await WaitUntilAsync(() => !viewModel.IsBusy);

            Assert.Equal("\u2014", viewModel.CurrentProgressFileName);
            Assert.Matches("^耗时 [0-9]{2}:[0-9]{2}:[0-9]{2}$", viewModel.OperationElapsedText);
            Assert.NotEqual("耗时 00:00:00", viewModel.OperationElapsedText);
        }
        finally
        {
            comparer.CompleteAll();
            await WaitUntilAsync(() => !viewModel.IsBusy);
        }
    }

    [Fact]
    public async Task BatchProgressDoesNotRegressOrBecomeIndeterminateAcrossInnerStages()
    {
        var comparer = new ControlledWorkbookComparer();
        var viewModel = TestViewModels.CreateMainWindow(workbookComparer: comparer);
        viewModel.IsFolderMode = true;
        var file = new FolderFileItemViewModel(new FolderFileResult(
            "book.xlsx",
            "left.xlsx",
            "right.xlsx",
            ComparisonStatus.Pending,
            0,
            null,
            null));
        viewModel.FolderFiles.Add(file);
        viewModel.SetSelectedFolderFiles([file]);
        var percentages = new ConcurrentQueue<int>();
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(MainWindowViewModel.ProgressPercent))
            {
                percentages.Enqueue(viewModel.ProgressPercent);
            }
        };

        viewModel.CompareSelectedCommand.Execute(null);

        try
        {
            await comparer.WaitForStartedCountAsync(1);

            comparer.Report("left.xlsx", ComparisonStage.Hashing, 1, 2, "hashing-half");
            await WaitUntilAsync(() =>
                viewModel.StatusText.Contains("hashing-half", StringComparison.Ordinal) &&
                viewModel.ProgressPercent == 50);
            Assert.Equal(50, viewModel.ProgressPercent);
            Assert.False(viewModel.ProgressIsIndeterminate);

            comparer.Report("left.xlsx", ComparisonStage.Reading, 0, 0, "reading-unknown");
            await WaitUntilAsync(() =>
                viewModel.StatusText.Contains("reading-unknown", StringComparison.Ordinal) &&
                viewModel.ProgressPercent == 50);
            Assert.Equal(50, viewModel.ProgressPercent);
            Assert.False(viewModel.ProgressIsIndeterminate);

            comparer.Report("left.xlsx", ComparisonStage.Comparing, 1, 4, "comparing-quarter");
            await WaitUntilAsync(() =>
                viewModel.StatusText.Contains("comparing-quarter", StringComparison.Ordinal) &&
                viewModel.ProgressPercent == 50);
            Assert.Equal(50, viewModel.ProgressPercent);
            Assert.False(viewModel.ProgressIsIndeterminate);

            comparer.Report("left.xlsx", ComparisonStage.Comparing, 3, 4, "comparing-three-quarters");
            await WaitUntilAsync(() =>
                viewModel.StatusText.Contains("comparing-three-quarters", StringComparison.Ordinal) &&
                viewModel.ProgressPercent == 75);
            Assert.Equal(75, viewModel.ProgressPercent);
            Assert.False(viewModel.ProgressIsIndeterminate);

            comparer.Complete("left.xlsx");
            await WaitUntilAsync(() => !viewModel.IsBusy);

            var observed = percentages.ToArray();
            Assert.NotEmpty(observed);
            Assert.True(
                observed.Zip(observed.Skip(1), static (left, right) => right >= left).All(static value => value),
                $"Batch progress regressed: {string.Join(", ", observed)}");
            Assert.Equal(100, viewModel.ProgressPercent);
        }
        finally
        {
            comparer.CompleteAll();
            await WaitUntilAsync(() => !viewModel.IsBusy);
        }
    }

    [Fact]
    public async Task WorksheetPreviewBusyStateCoversTheEntireDeferredLoad()
    {
        var reader = new ControlledPreviewReader();
        var viewModel = TestViewModels.CreateMainWindow(workbookReader: reader);
        var item = new FolderFileItemViewModel(new FolderFileResult(
            "book.xlsx",
            "left.xlsx",
            null,
            ComparisonStatus.LeftOnly,
            1,
            null,
            null));

        viewModel.OpenFolderItemCommand.Execute(item);

        try
        {
            await reader.WaitUntilPreviewStartedAsync();
            Assert.True(viewModel.IsPreviewBusy);

            reader.CompletePreview();

            await WaitUntilAsync(() => !viewModel.IsPreviewBusy);
            Assert.True(viewModel.IsWorkbookOpen);
        }
        finally
        {
            reader.CompletePreview();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for the expected view-model state.");
            }

            await Task.Delay(10);
        }
    }

    private static FolderFileItemViewModel FolderItem(
        string relativePath,
        ComparisonStatus status) => new(new FolderFileResult(
        relativePath,
        $"left-{relativePath}",
        $"right-{relativePath}",
        status,
        status == ComparisonStatus.Different ? 1 : 0,
        null,
        null));

    private static string[] VisibleFolderPaths(MainWindowViewModel viewModel) =>
        viewModel.FolderFilesView
            .Cast<FolderFileItemViewModel>()
            .Select(static item => item.RelativePath)
            .ToArray();

    private sealed class CapturingWorkbookComparer : IWorkbookComparer
    {
        public ComparisonOptions? LastOptions { get; private set; }

        public Task<WorkbookCompareResult> CompareAsync(
            string leftPath,
            string rightPath,
            ComparisonOptions? options = null,
            IProgress<ComparisonProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            return Task.FromResult(new WorkbookCompareResult(
                leftPath,
                rightPath,
                ComparisonStatus.Same,
                [],
                [],
                [],
                false,
                "left-sha",
                "right-sha",
                TimeSpan.Zero));
        }
    }

    private sealed class ControlledWorkbookComparer : IWorkbookComparer
    {
        private readonly ConcurrentDictionary<string, PendingComparison> _pending =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _startedSignal = new(0);
        private int _startedCount;

        public async Task<WorkbookCompareResult> CompareAsync(
            string leftPath,
            string rightPath,
            ComparisonOptions? options = null,
            IProgress<ComparisonProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<WorkbookCompareResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(_pending.TryAdd(leftPath, new PendingComparison(rightPath, completion, progress)));
            Interlocked.Increment(ref _startedCount);
            _startedSignal.Release();
            progress?.Report(new ComparisonProgress(
                ComparisonStage.Reading,
                leftPath,
                0,
                1,
                "reading"));
            return await completion.Task.WaitAsync(cancellationToken);
        }

        public async Task WaitForStartedCountAsync(int expected)
        {
            while (Volatile.Read(ref _startedCount) < expected)
            {
                Assert.True(await _startedSignal.WaitAsync(TimeSpan.FromSeconds(5)));
            }
        }

        public void Complete(string leftPath)
        {
            Assert.True(_pending.TryGetValue(leftPath, out var pending));
            pending.Completion.TrySetResult(CreateResult(leftPath, pending.RightPath));
        }

        public void Report(
            string leftPath,
            ComparisonStage stage,
            int processed,
            int total,
            string message)
        {
            Assert.True(_pending.TryGetValue(leftPath, out var pending));
            pending.Progress?.Report(new ComparisonProgress(
                stage,
                leftPath,
                processed,
                total,
                message));
        }

        public void CompleteAll()
        {
            foreach (var (leftPath, pending) in _pending)
            {
                pending.Completion.TrySetResult(CreateResult(leftPath, pending.RightPath));
            }
        }

        private static WorkbookCompareResult CreateResult(string leftPath, string rightPath) => new(
            leftPath,
            rightPath,
            ComparisonStatus.Same,
            [],
            [],
            [],
            false,
            "left-sha",
            "right-sha",
            TimeSpan.Zero);

        private sealed record PendingComparison(
            string RightPath,
            TaskCompletionSource<WorkbookCompareResult> Completion,
            IProgress<ComparisonProgress>? Progress);
    }

    private sealed class ControlledPreviewReader : IWorkbookReader
    {
        private readonly TaskCompletionSource _previewStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _previewCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<WorkbookInfo> ReadMetadataAsync(
            string filePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkbookInfo(
                filePath,
                false,
                [new WorksheetInfo("Sheet1", 0, "visible", 0)],
                []));

        public async IAsyncEnumerable<CellSnapshot> ReadCellsAsync(
            string filePath,
            string worksheetName,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async Task<WorksheetPreview> LoadWorksheetPreviewAsync(
            string filePath,
            string worksheetName,
            CancellationToken cancellationToken = default)
        {
            _previewStarted.TrySetResult();
            await _previewCompletion.Task.WaitAsync(cancellationToken);
            return new WorksheetPreview(
                filePath,
                worksheetName,
                new Dictionary<string, CellSnapshot>(),
                [],
                new HashSet<uint>(),
                []);
        }

        public Task WaitUntilPreviewStartedAsync() => _previewStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void CompletePreview() => _previewCompletion.TrySetResult();
    }

}
