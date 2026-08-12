using System.Diagnostics;
using Xunit.Abstractions;
using ZCompare.Core;

namespace ZCompare.Tests;

public sealed class PerformanceSmokeTests(ITestOutputHelper output) : ComparisonTestBase
{
    [Fact]
    [Trait("Category", "Performance")]
    public async Task ComparesOptInWorkbooksInDefaultValueOnlyMode()
    {
        var left = Environment.GetEnvironmentVariable("ZCOMPARE_PERF_LEFT");
        var right = Environment.GetEnvironmentVariable("ZCOMPARE_PERF_RIGHT");
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            output.WriteLine("Skipped: set ZCOMPARE_PERF_LEFT and ZCOMPARE_PERF_RIGHT to opt in.");
            return;
        }

        Assert.True(File.Exists(left), $"Performance fixture not found: {left}");
        Assert.True(File.Exists(right), $"Performance fixture not found: {right}");
        var leftBefore = Sha256(left);
        var rightBefore = Sha256(right);
        var stopwatch = Stopwatch.StartNew();

        var result = await CreateComparer().CompareAsync(
            left,
            right,
            new ComparisonOptions());

        stopwatch.Stop();
        var leftCellCount = result.Worksheets.Sum(static worksheet => (long)worksheet.LeftCellCount);
        var rightCellCount = result.Worksheets.Sum(static worksheet => (long)worksheet.RightCellCount);
        output.WriteLine(
            "DefaultValueOnly Elapsed={0}; Status={1}; Sheets={2}; LeftCells={3:N0}; RightCells={4:N0}; Differences={5}",
            stopwatch.Elapsed,
            result.Status,
            result.Worksheets.Count,
            leftCellCount,
            rightCellCount,
            result.DifferenceCount);

        Assert.Equal(leftBefore, Sha256(left));
        Assert.Equal(rightBefore, Sha256(right));
        Assert.True(stopwatch.Elapsed <= TimeSpan.FromSeconds(20), $"Default comparison took {stopwatch.Elapsed}.");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task ComparesOptInWorkbooksWithoutChangingSources()
    {
        var left = Environment.GetEnvironmentVariable("ZCOMPARE_PERF_LEFT");
        var right = Environment.GetEnvironmentVariable("ZCOMPARE_PERF_RIGHT");
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            output.WriteLine("Skipped: set ZCOMPARE_PERF_LEFT and ZCOMPARE_PERF_RIGHT to opt in.");
            return;
        }

        Assert.True(File.Exists(left), $"Performance fixture not found: {left}");
        Assert.True(File.Exists(right), $"Performance fixture not found: {right}");
        var leftBefore = Sha256(left);
        var rightBefore = Sha256(right);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var heapBefore = GC.GetGCMemoryInfo().HeapSizeBytes;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();

        var result = await CreateComparer().CompareAsync(
            left,
            right,
            AllComparisonsEnabled());

        stopwatch.Stop();
        var heapAfter = GC.GetGCMemoryInfo().HeapSizeBytes;
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var peakWorkingSet = process.PeakWorkingSet64;
        var leftCellCount = result.Worksheets.Sum(static worksheet => (long)worksheet.LeftCellCount);
        var rightCellCount = result.Worksheets.Sum(static worksheet => (long)worksheet.RightCellCount);
        output.WriteLine(
            "Elapsed={0}; Status={1}; Sheets={2}; LeftCells={3:N0}; RightCells={4:N0}; Differences={5}; " +
            "HeapBefore={6:N0}; HeapAfter={7:N0}; PeakWorkingSet={8:N0}; Allocated={9:N0}",
            stopwatch.Elapsed,
            result.Status,
            result.Worksheets.Count,
            leftCellCount,
            rightCellCount,
            result.DifferenceCount,
            heapBefore,
            heapAfter,
            peakWorkingSet,
            allocatedAfter - allocatedBefore);

        Assert.Equal(leftBefore, Sha256(left));
        Assert.Equal(rightBefore, Sha256(right));
        Assert.True(stopwatch.Elapsed <= TimeSpan.FromSeconds(30), $"Comparison took {stopwatch.Elapsed}.");
        Assert.True(peakWorkingSet < 1_500L * 1024 * 1024, $"Peak working set was {peakWorkingSet:N0} bytes.");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task CancelsOptInWorkbookComparisonWithinOneSecond()
    {
        var left = Environment.GetEnvironmentVariable("ZCOMPARE_PERF_LEFT");
        var right = Environment.GetEnvironmentVariable("ZCOMPARE_PERF_RIGHT");
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            output.WriteLine("Skipped: set ZCOMPARE_PERF_LEFT and ZCOMPARE_PERF_RIGHT to opt in.");
            return;
        }

        using var cancellation = new CancellationTokenSource();
        var stopwatch = Stopwatch.StartNew();
        var comparison = CreateComparer().CompareAsync(
            left,
            right,
            AllComparisonsEnabled(),
            cancellationToken: cancellation.Token);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => comparison);
        stopwatch.Stop();
        output.WriteLine("CancellationElapsed={0}", stopwatch.Elapsed);
        Assert.True(stopwatch.Elapsed <= TimeSpan.FromSeconds(1), $"Cancellation took {stopwatch.Elapsed}.");
    }

    private static ComparisonOptions AllComparisonsEnabled() => new()
    {
        CompareFormulas = true,
        CompareFormatting = true,
        CompareFonts = true,
        CompareComments = true,
        CompareHyperlinks = true,
        CompareLayout = true,
    };
}
