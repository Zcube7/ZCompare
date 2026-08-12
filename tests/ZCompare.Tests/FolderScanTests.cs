using ZCompare.Core;
using ZCompare.Tests.Fixtures;

namespace ZCompare.Tests;

public sealed class FolderScanTests
{
    [Fact]
    public async Task ScanRecursesPairsCaseInsensitivelyIgnoresTemporaryFilesAndDoesNotDeepCompare()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var left = temporaryDirectory.Directory("left");
        var right = temporaryDirectory.Directory("right");
        var leftNested = System.IO.Path.Combine(left, "Nested");
        var rightNested = System.IO.Path.Combine(right, "nested");
        Directory.CreateDirectory(leftNested);
        Directory.CreateDirectory(rightNested);

        WritePlaceholder(left, "Common.xlsx");
        WritePlaceholder(right, "common.XLSX");
        WritePlaceholder(leftNested, "Pair.xlsx");
        WritePlaceholder(rightNested, "pair.xlsx");
        WritePlaceholder(left, "LeftOnly.xlsx");
        WritePlaceholder(right, "RightOnly.xlsx");
        WritePlaceholder(left, "~$Common.xlsx");
        WritePlaceholder(rightNested, "~$Pair.xlsx");

        IFolderComparer comparer = new FolderComparer();
        var result = await comparer.ScanAsync(left, right);

        Assert.Equal(ComparisonStatus.Different, result.Status);
        Assert.Equal(4, result.Files.Count);
        Assert.DoesNotContain(result.Files, file => file.RelativePath.Contains("~$", StringComparison.Ordinal));

        var common = Assert.Single(result.Files, file => FileName(file) == "common.xlsx");
        AssertPendingPair(common);
        var nested = Assert.Single(result.Files, file => FileName(file) == "pair.xlsx");
        AssertPendingPair(nested);
        Assert.Contains("Nested", nested.RelativePath, StringComparison.OrdinalIgnoreCase);

        var leftOnly = Assert.Single(result.Files, file => FileName(file) == "leftonly.xlsx");
        Assert.Equal(ComparisonStatus.LeftOnly, leftOnly.Status);
        Assert.Equal(1, leftOnly.DifferenceCount);
        Assert.NotNull(leftOnly.LeftPath);
        Assert.Null(leftOnly.RightPath);
        Assert.Null(leftOnly.Comparison);
        Assert.Null(leftOnly.Error);

        var rightOnly = Assert.Single(result.Files, file => FileName(file) == "rightonly.xlsx");
        Assert.Equal(ComparisonStatus.RightOnly, rightOnly.Status);
        Assert.Equal(1, rightOnly.DifferenceCount);
        Assert.Null(rightOnly.LeftPath);
        Assert.NotNull(rightOnly.RightPath);
        Assert.Null(rightOnly.Comparison);
        Assert.Null(rightOnly.Error);
    }

    [Fact]
    public async Task ScanHonorsPreCancelledToken()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var left = temporaryDirectory.Directory("left");
        var right = temporaryDirectory.Directory("right");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        IFolderComparer comparer = new FolderComparer();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            comparer.ScanAsync(left, right, cancellationToken: cancellation.Token));
    }

    private static void AssertPendingPair(FolderFileResult file)
    {
        Assert.Equal(ComparisonStatus.Pending, file.Status);
        Assert.Equal(0, file.DifferenceCount);
        Assert.NotNull(file.LeftPath);
        Assert.NotNull(file.RightPath);
        Assert.Null(file.Comparison);
        Assert.Null(file.Error);
    }

    private static string FileName(FolderFileResult result) =>
        System.IO.Path.GetFileName(result.RelativePath).ToLowerInvariant();

    private static void WritePlaceholder(string directory, string fileName) =>
        File.WriteAllText(System.IO.Path.Combine(directory, fileName), "scan must not open this placeholder");
}
