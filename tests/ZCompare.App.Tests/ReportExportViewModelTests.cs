using System.IO;
using System.Text.Json;
using ZCompare.App.ViewModels;
using ZCompare.Core;

namespace ZCompare.App.Tests;

public sealed class ReportExportViewModelTests
{
    [Fact]
    public async Task MixedSameAndPendingFolderRowsExportOverallPendingWithoutFalseDifferenceCount()
    {
        var testDirectory = CreateTestDirectory();
        try
        {
            var outputPath = Path.Combine(testDirectory, "partial-folder.json");
            var viewModel = TestViewModels.CreateMainWindow();
            viewModel.IsFolderMode = true;
            viewModel.LeftPath = Path.Combine(testDirectory, "left");
            viewModel.RightPath = Path.Combine(testDirectory, "right");
            viewModel.FolderFiles.Add(new FolderFileItemViewModel(new FolderFileResult(
                "same.xlsx",
                "left-same.xlsx",
                "right-same.xlsx",
                ComparisonStatus.Same,
                0,
                null,
                null)));
            viewModel.FolderFiles.Add(new FolderFileItemViewModel(new FolderFileResult(
                "waiting.xlsx",
                "left-waiting.xlsx",
                "right-waiting.xlsx",
                ComparisonStatus.Pending,
                0,
                null,
                null)));

            Assert.True(viewModel.CanExportReport);
            await viewModel.ExportReportAsync(outputPath, ComparisonReportFormat.Json);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
            Assert.Equal("Pending", document.RootElement.GetProperty("Status").GetString());
            Assert.Equal(0, document.RootElement.GetProperty("DifferentFileCount").GetInt32());
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task FolderResultCanBeExportedAndOptionChangeMakesItStale()
    {
        var testDirectory = CreateTestDirectory();
        try
        {
            var outputPath = Path.Combine(testDirectory, "folder-report.json");
            var folderComparer = new ReportingFolderComparer();
            var viewModel = TestViewModels.CreateMainWindow(folderComparer: folderComparer);
            viewModel.IsFolderMode = true;
            viewModel.LeftPath = Path.Combine(testDirectory, "left");
            viewModel.RightPath = Path.Combine(testDirectory, "right");
            Directory.CreateDirectory(viewModel.LeftPath);
            Directory.CreateDirectory(viewModel.RightPath);
            viewModel.IncludeSubdirectories = false;
            viewModel.FolderFilePattern = "changed-*.xlsx";

            viewModel.StartCommand.Execute(null);
            await WaitUntilAsync(() => !viewModel.IsBusy && viewModel.FolderFiles.Count == 1);

            Assert.True(viewModel.CanExportReport);
            Assert.NotNull(folderComparer.LastScanOptions);
            Assert.False(folderComparer.LastScanOptions!.IncludeSubdirectories);
            Assert.Equal("changed-*.xlsx", folderComparer.LastScanOptions.FilePattern);

            await viewModel.ExportReportAsync(outputPath, ComparisonReportFormat.Json);

            Assert.True(File.Exists(outputPath));
            using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath)))
            {
                Assert.Equal(1, document.RootElement.GetProperty("FileCount").GetInt32());
                Assert.Equal(1, document.RootElement.GetProperty("DifferentFileCount").GetInt32());
                Assert.Equal("changed.xlsx", document.RootElement
                    .GetProperty("Files")[0]
                    .GetProperty("RelativePath")
                    .GetString());
            }
            Assert.False(viewModel.IsBusy);
            Assert.False(viewModel.ProgressIsIndeterminate);
            Assert.Equal(100, viewModel.ProgressPercent);
            Assert.Contains("报告已导出", viewModel.StatusText, StringComparison.Ordinal);

            viewModel.CompareComments = true;

            Assert.False(viewModel.CanExportReport);
            var staleOutput = Path.Combine(testDirectory, "stale.json");
            await viewModel.ExportReportAsync(staleOutput, ComparisonReportFormat.Json);
            Assert.False(File.Exists(staleOutput));
            Assert.Contains("没有可导出的比较结果", viewModel.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static string CreateTestDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ZCompare.App.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for folder scan state.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class ReportingFolderComparer : IFolderComparer
    {
        public FolderScanOptions? LastScanOptions { get; private set; }

        public Task<FolderCompareResult> ScanAsync(
            string leftDirectory,
            string rightDirectory,
            IProgress<ComparisonProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            ScanAsync(leftDirectory, rightDirectory, new FolderScanOptions(), progress, cancellationToken);

        public Task<FolderCompareResult> ScanAsync(
            string leftDirectory,
            string rightDirectory,
            FolderScanOptions scanOptions,
            IProgress<ComparisonProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            LastScanOptions = scanOptions;
            return Task.FromResult(new FolderCompareResult(
                leftDirectory,
                rightDirectory,
                ComparisonStatus.Different,
                [new FolderFileResult(
                    "changed.xlsx",
                    Path.Combine(leftDirectory, "changed.xlsx"),
                    Path.Combine(rightDirectory, "changed.xlsx"),
                    ComparisonStatus.Different,
                    3,
                    null,
                    null)],
                TimeSpan.FromSeconds(1)));
        }

        public Task<FolderCompareResult> CompareAsync(
            string leftDirectory,
            string rightDirectory,
            ComparisonOptions? options = null,
            IProgress<ComparisonProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            ScanAsync(leftDirectory, rightDirectory, progress, cancellationToken);

        public Task<FolderCompareResult> CompareAsync(
            string leftDirectory,
            string rightDirectory,
            ComparisonOptions? options,
            FolderScanOptions scanOptions,
            IProgress<ComparisonProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            ScanAsync(leftDirectory, rightDirectory, scanOptions, progress, cancellationToken);
    }
}
