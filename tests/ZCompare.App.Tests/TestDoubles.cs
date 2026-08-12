using System.Runtime.CompilerServices;
using ZCompare.App.Services;
using ZCompare.App.ViewModels;
using ZCompare.Core;

namespace ZCompare.App.Tests;

internal static class TestViewModels
{
    public static MainWindowViewModel CreateMainWindow(
        IWorkbookReader? workbookReader = null,
        IWorkbookComparer? workbookComparer = null,
        IFolderComparer? folderComparer = null,
        IPathDialogService? pathDialogService = null,
        IRecentComparisonStore? recentComparisonStore = null) => new(
        workbookReader ?? new StubWorkbookReader(),
        workbookComparer ?? new StubWorkbookComparer(),
        folderComparer ?? new StubFolderComparer(),
        pathDialogService ?? new StubPathDialogService(),
        recentComparisonStore);

    public static CellSnapshot Cell(
        string reference,
        string value,
        CellValueKind kind = CellValueKind.Text) => new(
            "Sheet1",
            reference,
            kind,
            value,
            value,
            value,
            null,
            FormulaKind.None,
            null,
            new CellFormatSnapshot(
                "General",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                "FF000000",
                "FFFFFFFF"),
            null,
            null,
            null,
            false,
            false);

    public static WorksheetPreview Preview(params CellSnapshot[] cells) => new(
        "test.xlsx",
        "Sheet1",
        cells.ToDictionary(static cell => cell.CellReference, StringComparer.OrdinalIgnoreCase),
        [],
        new HashSet<uint>(),
        []);

    private sealed class StubWorkbookReader : IWorkbookReader
    {
        public Task<WorkbookInfo> ReadMetadataAsync(
            string filePath,
            CancellationToken cancellationToken = default) =>
            Task.FromException<WorkbookInfo>(new NotSupportedException());

        public async IAsyncEnumerable<CellSnapshot> ReadCellsAsync(
            string filePath,
            string worksheetName,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<WorksheetPreview> LoadWorksheetPreviewAsync(
            string filePath,
            string worksheetName,
            CancellationToken cancellationToken = default) =>
            Task.FromException<WorksheetPreview>(new NotSupportedException());
    }

    private sealed class StubWorkbookComparer : IWorkbookComparer
    {
        public Task<WorkbookCompareResult> CompareAsync(
            string leftPath,
            string rightPath,
            ComparisonOptions? options = null,
            IProgress<ComparisonProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<WorkbookCompareResult>(new NotSupportedException());
    }

    private sealed class StubFolderComparer : IFolderComparer
    {
        public Task<FolderCompareResult> ScanAsync(
            string leftDirectory,
            string rightDirectory,
            IProgress<ComparisonProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<FolderCompareResult>(new NotSupportedException());

        public Task<FolderCompareResult> ScanAsync(
            string leftDirectory,
            string rightDirectory,
            FolderScanOptions scanOptions,
            IProgress<ComparisonProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<FolderCompareResult>(new NotSupportedException());

        public Task<FolderCompareResult> CompareAsync(
            string leftDirectory,
            string rightDirectory,
            ComparisonOptions? options = null,
            IProgress<ComparisonProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<FolderCompareResult>(new NotSupportedException());

        public Task<FolderCompareResult> CompareAsync(
            string leftDirectory,
            string rightDirectory,
            ComparisonOptions? options,
            FolderScanOptions scanOptions,
            IProgress<ComparisonProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<FolderCompareResult>(new NotSupportedException());
    }

    private sealed class StubPathDialogService : IPathDialogService
    {
        public string? SelectWorkbook(string? initialPath) => null;

        public string? SelectFolder(string? initialPath) => null;
    }
}
