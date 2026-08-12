using ZCompare.Core;
using ZCompare.Tests.Fixtures;

namespace ZCompare.Tests;

public sealed class FolderScanOptionsTests : ComparisonTestBase
{
    [Fact]
    public void DefaultsIncludeSubdirectoriesAndAllXlsxFiles()
    {
        var options = new FolderScanOptions();

        Assert.True(options.IncludeSubdirectories);
        Assert.Equal("*.xlsx", options.FilePattern);
    }

    [Fact]
    public async Task ScanCanBeRestrictedToTopLevelFiles()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var left = temporaryDirectory.Directory("left");
        var right = temporaryDirectory.Directory("right");
        WritePlaceholder(left, "top.xlsx");
        WritePlaceholder(right, "top.xlsx");
        WritePlaceholder(Path.Combine(left, "nested"), "nested.xlsx");
        WritePlaceholder(Path.Combine(right, "nested"), "nested.xlsx");

        var result = await CreateFolderComparer().ScanAsync(
            left,
            right,
            new FolderScanOptions { IncludeSubdirectories = false });

        var file = Assert.Single(result.Files);
        Assert.Equal("top.xlsx", file.RelativePath, ignoreCase: true);
    }

    [Fact]
    public async Task SimpleWildcardMatchesFileNameOnlyAndStillEnforcesXlsxRules()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var left = temporaryDirectory.Directory("left");
        var right = temporaryDirectory.Directory("right");
        WritePlaceholder(left, "data-01.xlsx");
        WritePlaceholder(left, "DATA-AB.XLSX");
        WritePlaceholder(left, "data-01.xlsm");
        WritePlaceholder(left, "notes.xlsx");
        WritePlaceholder(left, "~$data-02.xlsx");
        WritePlaceholder(Path.Combine(left, "nested"), "data-02.xlsx");

        var result = await CreateFolderComparer().ScanAsync(
            left,
            right,
            new FolderScanOptions
            {
                IncludeSubdirectories = true,
                FilePattern = "data-??.xlsx",
            });

        Assert.Equal(3, result.Files.Count);
        Assert.Contains(result.Files, file => file.RelativePath.Equals("data-01.xlsx", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Files, file => file.RelativePath.Equals("DATA-AB.XLSX", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Files, file => file.RelativePath.EndsWith(
            Path.Combine("nested", "data-02.xlsx"),
            StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Files, file => file.RelativePath.Contains("~$", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Files, file => Path.GetExtension(file.RelativePath).Equals(".xlsm", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WildcardCannotContainDirectorySegments()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var left = temporaryDirectory.Directory("left");
        var right = temporaryDirectory.Directory("right");

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateFolderComparer().ScanAsync(
                left,
                right,
                new FolderScanOptions { FilePattern = Path.Combine("nested", "*.xlsx") }));

        Assert.Contains("不能包含目录", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyFolderComparerImplementationsKeepWorkingWithDefaultScanOptions()
    {
        IFolderComparer comparer = new LegacyFolderComparer();

        var result = await comparer.ScanAsync("left", "right", new FolderScanOptions());

        Assert.Equal(ComparisonStatus.Same, result.Status);
        await Assert.ThrowsAsync<NotSupportedException>(() => comparer.ScanAsync(
            "left",
            "right",
            new FolderScanOptions { IncludeSubdirectories = false }));
    }

    private static void WritePlaceholder(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), "scan-only placeholder");
    }

    private sealed class LegacyFolderComparer : IFolderComparer
    {
        public Task<FolderCompareResult> ScanAsync(
            string leftDirectory,
            string rightDirectory,
            IProgress<ComparisonProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new FolderCompareResult(
                leftDirectory,
                rightDirectory,
                ComparisonStatus.Same,
                [],
                TimeSpan.Zero));

        public Task<FolderCompareResult> CompareAsync(
            string leftDirectory,
            string rightDirectory,
            ComparisonOptions? options = null,
            IProgress<ComparisonProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            ScanAsync(leftDirectory, rightDirectory, progress, cancellationToken);
    }
}
