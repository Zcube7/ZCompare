namespace ZCompare.Core;

public interface IWorkbookReader
{
    Task<WorkbookInfo> ReadMetadataAsync(
        string filePath,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<CellSnapshot> ReadCellsAsync(
        string filePath,
        string worksheetName,
        CancellationToken cancellationToken = default);

    Task<WorksheetPreview> LoadWorksheetPreviewAsync(
        string filePath,
        string worksheetName,
        CancellationToken cancellationToken = default);
}

public interface IWorkbookComparer
{
    Task<WorkbookCompareResult> CompareAsync(
        string leftPath,
        string rightPath,
        ComparisonOptions? options = null,
        IProgress<ComparisonProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IFolderComparer
{
    Task<FolderCompareResult> ScanAsync(
        string leftDirectory,
        string rightDirectory,
        IProgress<ComparisonProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<FolderCompareResult> ScanAsync(
        string leftDirectory,
        string rightDirectory,
        FolderScanOptions scanOptions,
        IProgress<ComparisonProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scanOptions);
        if (!scanOptions.IncludeSubdirectories ||
            !string.Equals(scanOptions.FilePattern, "*.xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("当前文件夹比较器实现不支持自定义扫描选项。");
        }
        return ScanAsync(leftDirectory, rightDirectory, progress, cancellationToken);
    }

    Task<FolderCompareResult> CompareAsync(
        string leftDirectory,
        string rightDirectory,
        ComparisonOptions? options = null,
        IProgress<ComparisonProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<FolderCompareResult> CompareAsync(
        string leftDirectory,
        string rightDirectory,
        ComparisonOptions? options,
        FolderScanOptions scanOptions,
        IProgress<ComparisonProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scanOptions);
        if (!scanOptions.IncludeSubdirectories ||
            !string.Equals(scanOptions.FilePattern, "*.xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("当前文件夹比较器实现不支持自定义扫描选项。");
        }
        return CompareAsync(leftDirectory, rightDirectory, options, progress, cancellationToken);
    }
}
