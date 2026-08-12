using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;

namespace ZCompare.Core;

public sealed class FolderComparer : IFolderComparer
{
    private readonly IWorkbookComparer _comparer;

    public FolderComparer(IWorkbookComparer? comparer = null)
    {
        _comparer = comparer ?? new WorkbookComparer();
    }

    public Task<FolderCompareResult> ScanAsync(
        string leftDirectory,
        string rightDirectory,
        IProgress<ComparisonProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ScanAsync(
            leftDirectory,
            rightDirectory,
            new FolderScanOptions(),
            progress,
            cancellationToken);

    public async Task<FolderCompareResult> ScanAsync(
        string leftDirectory,
        string rightDirectory,
        FolderScanOptions scanOptions,
        IProgress<ComparisonProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateDirectory(leftDirectory, nameof(leftDirectory));
        ValidateDirectory(rightDirectory, nameof(rightDirectory));
        ValidateScanOptions(scanOptions);
        var stopwatch = Stopwatch.StartNew();
        progress?.Report(new ComparisonProgress(
            ComparisonStage.ScanningFolders,
            null,
            0,
            0,
            "正在递归扫描文件夹…"));

        var scan = await Task.Run(
            () => ScanPair(leftDirectory, rightDirectory, scanOptions, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        var files = new List<FolderFileResult>(scan.RelativePaths.Count);
        for (var index = 0; index < scan.RelativePaths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = scan.RelativePaths[index];
            scan.LeftFiles.TryGetValue(relativePath, out var leftPath);
            scan.RightFiles.TryGetValue(relativePath, out var rightPath);
            var status = leftPath is null
                ? ComparisonStatus.RightOnly
                : rightPath is null
                    ? ComparisonStatus.LeftOnly
                    : ComparisonStatus.Pending;
            var result = new FolderFileResult(
                relativePath,
                leftPath,
                rightPath,
                status,
                status == ComparisonStatus.Pending ? 0 : 1,
                null,
                null);
            files.Add(result);
            progress?.Report(new ComparisonProgress(
                ComparisonStage.ScanningFolders,
                relativePath,
                index + 1,
                scan.RelativePaths.Count,
                $"已发现：{relativePath}",
                result));
        }

        var overallStatus = files.Any(static file => file.Status is ComparisonStatus.LeftOnly or ComparisonStatus.RightOnly)
            ? ComparisonStatus.Different
            : files.Count > 0
                ? ComparisonStatus.Pending
                : ComparisonStatus.Same;
        progress?.Report(new ComparisonProgress(
            ComparisonStage.Completed,
            null,
            files.Count,
            files.Count,
            "文件夹扫描完成。"));
        return new FolderCompareResult(
            Path.GetFullPath(leftDirectory),
            Path.GetFullPath(rightDirectory),
            overallStatus,
            files,
            stopwatch.Elapsed);
    }

    public Task<FolderCompareResult> CompareAsync(
        string leftDirectory,
        string rightDirectory,
        ComparisonOptions? options = null,
        IProgress<ComparisonProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        CompareAsync(
            leftDirectory,
            rightDirectory,
            options,
            new FolderScanOptions(),
            progress,
            cancellationToken);

    public async Task<FolderCompareResult> CompareAsync(
        string leftDirectory,
        string rightDirectory,
        ComparisonOptions? options,
        FolderScanOptions scanOptions,
        IProgress<ComparisonProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateDirectory(leftDirectory, nameof(leftDirectory));
        ValidateDirectory(rightDirectory, nameof(rightDirectory));
        ValidateScanOptions(scanOptions);
        options ??= new ComparisonOptions();
        var stopwatch = Stopwatch.StartNew();
        progress?.Report(new ComparisonProgress(
            ComparisonStage.ScanningFolders,
            null,
            0,
            0,
            "正在递归扫描文件夹…"));

        var scan = ScanPair(leftDirectory, rightDirectory, scanOptions, cancellationToken);
        var leftFiles = scan.LeftFiles;
        var rightFiles = scan.RightFiles;
        var relativePaths = scan.RelativePaths;
        var results = new ConcurrentDictionary<string, FolderFileResult>(StringComparer.OrdinalIgnoreCase);
        var completed = 0;
        var cancelled = false;
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(options.MaxFolderConcurrency, 1, 2)
        };

        try
        {
            await Parallel.ForEachAsync(relativePaths, parallelOptions, async (relativePath, token) =>
            {
                leftFiles.TryGetValue(relativePath, out var leftPath);
                rightFiles.TryGetValue(relativePath, out var rightPath);
                FolderFileResult result;
                if (leftPath is null)
                {
                    result = new FolderFileResult(
                        relativePath,
                        null,
                        rightPath,
                        ComparisonStatus.RightOnly,
                        1,
                        null,
                        null);
                }
                else if (rightPath is null)
                {
                    result = new FolderFileResult(
                        relativePath,
                        leftPath,
                        null,
                        ComparisonStatus.LeftOnly,
                        1,
                        null,
                        null);
                }
                else
                {
                    try
                    {
                        var comparison = await _comparer.CompareAsync(
                            leftPath,
                            rightPath,
                            options,
                            progress: null,
                            token).ConfigureAwait(false);
                        result = new FolderFileResult(
                            relativePath,
                            leftPath,
                            rightPath,
                            comparison.Status,
                            comparison.DifferenceCount,
                            comparison,
                            null);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        result = new FolderFileResult(
                            relativePath,
                            leftPath,
                            rightPath,
                            ComparisonStatus.Error,
                            0,
                            null,
                            exception.Message);
                    }
                }

                results[relativePath] = result;
                var processed = Interlocked.Increment(ref completed);
                progress?.Report(new ComparisonProgress(
                    ComparisonStage.Comparing,
                    relativePath,
                    processed,
                    relativePaths.Count,
                    $"已完成：{relativePath}",
                    result));
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }

        if (cancelled)
        {
            foreach (var relativePath in relativePaths)
            {
                if (results.ContainsKey(relativePath))
                {
                    continue;
                }
                leftFiles.TryGetValue(relativePath, out var leftPath);
                rightFiles.TryGetValue(relativePath, out var rightPath);
                results[relativePath] = new FolderFileResult(
                    relativePath,
                    leftPath,
                    rightPath,
                    ComparisonStatus.Cancelled,
                    0,
                    null,
                    null);
            }
        }

        var orderedResults = relativePaths.Select(path => results[path]).ToArray();
        var status = DetermineStatus(orderedResults, cancelled);
        progress?.Report(new ComparisonProgress(
            ComparisonStage.Completed,
            null,
            completed,
            relativePaths.Count,
            cancelled ? "文件夹比较已取消。" : "文件夹比较完成。"));
        return new FolderCompareResult(
            Path.GetFullPath(leftDirectory),
            Path.GetFullPath(rightDirectory),
            status,
            orderedResults,
            stopwatch.Elapsed);
    }

    private static Dictionary<string, string> Scan(
        string root,
        FolderScanOptions options,
        CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            foreach (var file in current.EnumerateFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(file.Extension, ".xlsx", StringComparison.OrdinalIgnoreCase) ||
                    file.Name.StartsWith("~$", StringComparison.Ordinal) ||
                    !FileSystemName.MatchesSimpleExpression(
                        options.FilePattern,
                        file.Name,
                        ignoreCase: true))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(root, file.FullName)
                    .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                files.TryAdd(relativePath, file.FullName);
            }

            if (!options.IncludeSubdirectories)
            {
                continue;
            }

            foreach (var directory in current.EnumerateDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((directory.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Push(directory);
                }
            }
        }
        return files;
    }

    private static FolderScan ScanPair(
        string leftDirectory,
        string rightDirectory,
        FolderScanOptions options,
        CancellationToken cancellationToken)
    {
        var leftFiles = Scan(leftDirectory, options, cancellationToken);
        var rightFiles = Scan(rightDirectory, options, cancellationToken);
        var relativePaths = leftFiles.Keys
            .Concat(rightFiles.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new FolderScan(leftFiles, rightFiles, relativePaths);
    }

    private static ComparisonStatus DetermineStatus(IReadOnlyList<FolderFileResult> files, bool cancelled)
    {
        if (cancelled)
        {
            return ComparisonStatus.Cancelled;
        }
        if (files.Any(static file => file.Status is ComparisonStatus.Different or ComparisonStatus.LeftOnly or ComparisonStatus.RightOnly))
        {
            return ComparisonStatus.Different;
        }
        if (files.Any(static file => file.Status is ComparisonStatus.Warning or ComparisonStatus.Error))
        {
            return ComparisonStatus.Warning;
        }
        return ComparisonStatus.Same;
    }

    private static void ValidateDirectory(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"找不到文件夹：{path}");
        }
    }

    private static void ValidateScanOptions(FolderScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.FilePattern, nameof(options.FilePattern));
        if (options.FilePattern.IndexOfAny([
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar,
            Path.VolumeSeparatorChar]) >= 0)
        {
            throw new ArgumentException("文件通配符只能匹配文件名，不能包含目录。", nameof(options.FilePattern));
        }
    }

    private sealed record FolderScan(
        IReadOnlyDictionary<string, string> LeftFiles,
        IReadOnlyDictionary<string, string> RightFiles,
        IReadOnlyList<string> RelativePaths);
}
