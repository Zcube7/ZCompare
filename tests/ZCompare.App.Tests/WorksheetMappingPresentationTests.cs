using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using ZCompare.App.ViewModels;
using ZCompare.Core;

namespace ZCompare.App.Tests;

public sealed class WorksheetMappingPresentationTests
{
    [Fact]
    public async Task DifferentWorksheetNamesAndMappedColumnsUseRealSourcesAndDistinctTabCount()
    {
        var testDirectory = CreateTestDirectory();
        try
        {
            var leftPath = Path.Combine(testDirectory, "left.xlsx");
            var rightPath = Path.Combine(testDirectory, "right.xlsx");
            await File.WriteAllTextAsync(leftPath, "left source");
            await File.WriteAllTextAsync(rightPath, "right source");
            var leftCell = TestViewModels.Cell("A1", "left") with { WorksheetName = "LeftData" };
            var rightCell = TestViewModels.Cell("C1", "right") with { WorksheetName = "RightData" };
            Difference[] differences =
            [
                new(
                    DifferenceKind.Value,
                    "LeftData ↔ RightData",
                    "A1",
                    "保存值不同。",
                    leftCell,
                    rightCell,
                    "left",
                    "right"),
                new(
                    DifferenceKind.Comment,
                    "LeftData ↔ RightData",
                    "A1",
                    "批注不同。",
                    leftCell,
                    rightCell,
                    "left note",
                    "right note"),
            ];
            var worksheet = new WorksheetCompareResult(
                "LeftData ↔ RightData",
                ComparisonStatus.Different,
                2,
                differences,
                1,
                1,
                [new RowAlignment(1, 1, 1, RowAlignmentStatus.Modified)],
                1,
                1,
                "LeftData",
                "RightData",
                [new ColumnPair("A", "C")]);
            var comparison = new WorkbookCompareResult(
                leftPath,
                rightPath,
                ComparisonStatus.Different,
                [worksheet],
                [],
                [],
                false,
                Hash(leftPath),
                Hash(rightPath),
                TimeSpan.Zero);
            var reader = new NamedWorksheetReader(leftPath, rightPath);
            var viewModel = TestViewModels.CreateMainWindow(workbookReader: reader);
            var item = new FolderFileItemViewModel(new FolderFileResult(
                "pair.xlsx",
                leftPath,
                rightPath,
                ComparisonStatus.Different,
                comparison.DifferenceCount,
                comparison,
                null));

            viewModel.OpenFolderItemCommand.Execute(item);
            await WaitUntilAsync(() =>
                viewModel.IsWorkbookOpen &&
                !viewModel.IsBusy &&
                !viewModel.IsPreviewBusy &&
                reader.PreviewRequests.Count >= 2);

            Assert.Contains(reader.PreviewRequests, request =>
                request.FilePath == leftPath && request.WorksheetName == "LeftData");
            Assert.Contains(reader.PreviewRequests, request =>
                request.FilePath == rightPath && request.WorksheetName == "RightData");
            var tab = Assert.Single(viewModel.Worksheets);
            Assert.Equal(1, worksheet.DistinctDifferenceCount);
            Assert.Equal(1, tab.DifferenceCount);
            Assert.Equal("LeftData ↔ RightData (1)", tab.Header);

            var leftGridCell = viewModel.GridViewport.GetCell(CompareSide.Left, 0, 0);
            var rightGridCell = viewModel.GridViewport.GetCell(CompareSide.Right, 0, 0);
            Assert.NotNull(leftGridCell);
            Assert.NotNull(rightGridCell);
            Assert.Equal("A1", leftGridCell!.Address);
            Assert.Equal("left", leftGridCell.RawValue);
            Assert.Equal("C1", rightGridCell!.Address);
            Assert.Equal("right", rightGridCell.RawValue);
            Assert.True(rightGridCell.IsDifferent);
            Assert.Equal("—", viewModel.GridViewport.GetCell(CompareSide.Right, 0, 2)?.Address);
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

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for mapped worksheet preview.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class NamedWorksheetReader(string leftPath, string rightPath) : IWorkbookReader
    {
        public ConcurrentBag<(string FilePath, string WorksheetName)> PreviewRequests { get; } = [];

        public Task<WorkbookInfo> ReadMetadataAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            var worksheetName = filePath == leftPath
                ? "LeftData"
                : filePath == rightPath
                    ? "RightData"
                    : throw new ArgumentException("Unexpected workbook path.", nameof(filePath));
            return Task.FromResult(new WorkbookInfo(
                filePath,
                false,
                [new WorksheetInfo(worksheetName, 0, "visible", 1)],
                []));
        }

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
            CancellationToken cancellationToken = default)
        {
            PreviewRequests.Add((filePath, worksheetName));
            var cell = filePath == leftPath
                ? TestViewModels.Cell("A1", "left") with { WorksheetName = worksheetName }
                : TestViewModels.Cell("C1", "right") with { WorksheetName = worksheetName };
            return Task.FromResult(new WorksheetPreview(
                filePath,
                worksheetName,
                new Dictionary<string, CellSnapshot>(StringComparer.OrdinalIgnoreCase)
                {
                    [cell.CellReference] = cell,
                },
                [],
                new HashSet<uint>(),
                []));
        }
    }
}
