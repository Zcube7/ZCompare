using System.Collections.Concurrent;
using ZCompare.Core;
using ZCompare.Tests.Fixtures;

namespace ZCompare.Tests;

public sealed class FolderComparisonTests : ComparisonTestBase
{
    [Fact]
    public async Task DeepComparisonNeverExceedsTwoConcurrentWorkbooks()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var left = temporaryDirectory.Directory("left");
        var right = temporaryDirectory.Directory("right");
        for (var index = 0; index < 8; index++)
        {
            var fileName = $"pair-{index:D2}.xlsx";
            File.WriteAllText(System.IO.Path.Combine(left, fileName), "left placeholder");
            File.WriteAllText(System.IO.Path.Combine(right, fileName), "right placeholder");
        }

        var comparer = new ConcurrencyTrackingComparer();
        var folderComparer = new FolderComparer(comparer);

        var result = await folderComparer.CompareAsync(
            left,
            right,
            new ComparisonOptions { MaxFolderConcurrency = 99 });

        Assert.Equal(8, result.Files.Count);
        Assert.Equal(2, comparer.PeakConcurrency);
    }

    [Fact]
    public async Task RecursesPairsCaseInsensitivelyAndIgnoresExcelTemporaryFiles()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var left = temporaryDirectory.Directory("left");
        var right = temporaryDirectory.Directory("right");
        var leftSub = System.IO.Path.Combine(left, "Sub");
        var rightSub = System.IO.Path.Combine(right, "sub");
        Directory.CreateDirectory(leftSub);
        Directory.CreateDirectory(rightSub);

        new TestWorkbookBuilder().AddSheet("Sheet1", sheet => sheet.Cell("A1", "1"))
            .Save(System.IO.Path.Combine(left, "Common.xlsx"));
        new TestWorkbookBuilder().AddSheet("Sheet1", sheet => sheet.Cell("A1", "1"))
            .Save(System.IO.Path.Combine(right, "common.xlsx"));
        new TestWorkbookBuilder().AddSheet("Sheet1", sheet => sheet.Cell("A1", "1"))
            .Save(System.IO.Path.Combine(leftSub, "Changed.xlsx"));
        new TestWorkbookBuilder().AddSheet("Sheet1", sheet => sheet.Cell("A1", "2"))
            .Save(System.IO.Path.Combine(rightSub, "changed.xlsx"));
        new TestWorkbookBuilder().AddSheet("Sheet1")
            .Save(System.IO.Path.Combine(left, "LeftOnly.xlsx"));
        new TestWorkbookBuilder().AddSheet("Sheet1")
            .Save(System.IO.Path.Combine(right, "RightOnly.xlsx"));
        File.WriteAllText(System.IO.Path.Combine(left, "~$Common.xlsx"), "temporary");

        var result = await CreateFolderComparer().CompareAsync(left, right);

        Assert.Equal(4, result.Files.Count);
        Assert.DoesNotContain(result.Files, file => file.RelativePath.Contains("~$", StringComparison.Ordinal));
        Assert.Contains(result.Files, file => file.Status == ComparisonStatus.Same && Name(file) == "common.xlsx");
        Assert.Contains(result.Files, file => file.Status == ComparisonStatus.Different && Name(file) == "changed.xlsx");
        var leftOnly = Assert.Single(result.Files, file => Name(file) == "leftonly.xlsx");
        Assert.Equal(ComparisonStatus.LeftOnly, leftOnly.Status);
        Assert.NotNull(leftOnly.LeftPath);
        Assert.Null(leftOnly.RightPath);
        Assert.Null(leftOnly.Comparison);
        var rightOnly = Assert.Single(result.Files, file => Name(file) == "rightonly.xlsx");
        Assert.Equal(ComparisonStatus.RightOnly, rightOnly.Status);
        Assert.Null(rightOnly.LeftPath);
        Assert.NotNull(rightOnly.RightPath);
        Assert.Null(rightOnly.Comparison);
    }

    [Fact]
    public async Task CorruptWorkbookIsReportedAsErrorWithoutStoppingFolder()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var left = temporaryDirectory.Directory("left");
        var right = temporaryDirectory.Directory("right");
        File.WriteAllText(System.IO.Path.Combine(left, "broken.xlsx"), "not a zip archive");
        File.WriteAllText(System.IO.Path.Combine(right, "broken.xlsx"), "also not a zip archive");
        new TestWorkbookBuilder().AddSheet("Sheet1").Save(System.IO.Path.Combine(left, "ok.xlsx"));
        new TestWorkbookBuilder().AddSheet("Sheet1").Save(System.IO.Path.Combine(right, "ok.xlsx"));

        var result = await CreateFolderComparer().CompareAsync(left, right);

        Assert.Equal(ComparisonStatus.Warning, result.Status);
        Assert.Equal(1, result.ErrorFileCount);
        Assert.False(result.HasConfirmedDifferences);
        Assert.Contains(result.Files, file => Name(file) == "broken.xlsx" && file.Status == ComparisonStatus.Error && file.Error is not null);
        Assert.Contains(result.Files, file => Name(file) == "ok.xlsx" && file.Status == ComparisonStatus.Same);
    }

    [Fact]
    public async Task ConfirmedDifferencesRemainVisibleWhenAnotherWorkbookFails()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var left = temporaryDirectory.Directory("left");
        var right = temporaryDirectory.Directory("right");
        File.WriteAllText(System.IO.Path.Combine(left, "broken.xlsx"), "broken-left");
        File.WriteAllText(System.IO.Path.Combine(right, "broken.xlsx"), "broken-right");
        new TestWorkbookBuilder().AddSheet("Sheet1", sheet => sheet.Cell("A1", "left"))
            .Save(System.IO.Path.Combine(left, "changed.xlsx"));
        new TestWorkbookBuilder().AddSheet("Sheet1", sheet => sheet.Cell("A1", "right"))
            .Save(System.IO.Path.Combine(right, "changed.xlsx"));

        var result = await CreateFolderComparer().CompareAsync(left, right);

        Assert.Equal(ComparisonStatus.Different, result.Status);
        Assert.Equal(1, result.ErrorFileCount);
        Assert.True(result.HasConfirmedDifferences);
        Assert.Contains(result.Files, file => Name(file) == "changed.xlsx" && file.Status == ComparisonStatus.Different);
        Assert.Contains(result.Files, file => Name(file) == "broken.xlsx" && file.Status == ComparisonStatus.Error);
    }

    [Fact]
    public async Task PreCancelledWorkbookAndFolderComparisonsStopImmediately()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet.Cell("A1", "1")),
            builder => builder.AddSheet("Sheet1", sheet => sheet.Cell("A1", "2")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateComparer().CompareAsync(pair.Left, pair.Right, cancellationToken: cancellation.Token));

        var leftDirectory = temporaryDirectory.Directory("folder-left");
        var rightDirectory = temporaryDirectory.Directory("folder-right");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateFolderComparer().CompareAsync(leftDirectory, rightDirectory, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task ReportsEachOfOneHundredFilesProgressively()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var left = temporaryDirectory.Directory("left");
        var right = temporaryDirectory.Directory("right");
        var template = new TestWorkbookBuilder()
            .AddSheet("Sheet1", sheet => sheet.Cell("A1", "1"))
            .Save(temporaryDirectory.File("template.xlsx"));

        for (var index = 0; index < 100; index++)
        {
            var fileName = $"book-{index:D3}.xlsx";
            File.Copy(template, System.IO.Path.Combine(left, fileName));
            File.Copy(template, System.IO.Path.Combine(right, fileName));
        }

        var completed = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var progress = new InlineProgress<ComparisonProgress>(update =>
        {
            if (update.CompletedFile is not null)
            {
                completed.TryAdd(update.CompletedFile.RelativePath, 0);
            }
        });

        var result = await CreateFolderComparer().CompareAsync(left, right, progress: progress);

        Assert.Equal(ComparisonStatus.Same, result.Status);
        Assert.Equal(100, result.Files.Count);
        Assert.All(result.Files, file => Assert.Equal(ComparisonStatus.Same, file.Status));
        Assert.Equal(100, completed.Count);
    }

    private static string Name(FolderFileResult result) =>
        System.IO.Path.GetFileName(result.RelativePath).ToLowerInvariant();

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class ConcurrencyTrackingComparer : IWorkbookComparer
    {
        private readonly object _gate = new();
        private int _active;

        public int PeakConcurrency { get; private set; }

        public async Task<WorkbookCompareResult> CompareAsync(
            string leftPath,
            string rightPath,
            ComparisonOptions? options = null,
            IProgress<ComparisonProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _active++;
                PeakConcurrency = Math.Max(PeakConcurrency, _active);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(75), cancellationToken);
            }
            finally
            {
                lock (_gate)
                {
                    _active--;
                }
            }

            return new WorkbookCompareResult(
                leftPath,
                rightPath,
                ComparisonStatus.Same,
                [],
                [],
                [],
                false,
                string.Empty,
                string.Empty,
                TimeSpan.Zero);
        }
    }
}
