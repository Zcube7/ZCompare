using System.Runtime.CompilerServices;
using ZCompare.Core;
using ZCompare.Tests.Fixtures;

namespace ZCompare.Tests;

public sealed class FileChangeDetectionTests : ComparisonTestBase
{
    [Fact]
    public async Task SameLengthReplacementWithRestoredTimestampInvalidatesComparison()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var left = new TestWorkbookBuilder()
            .AddSheet("Sheet1", sheet => sheet.Cell("A1", "1"))
            .Save(temporaryDirectory.File("left.xlsx"));
        var replacement = new TestWorkbookBuilder()
            .AddSheet("Sheet1", sheet => sheet.Cell("A1", "2"))
            .Save(temporaryDirectory.File("replacement.xlsx"));
        var right = new TestWorkbookBuilder()
            .AddSheet("Sheet1", sheet => sheet.Cell("A1", "2"))
            .Save(temporaryDirectory.File("right.xlsx"));
        var originalTimestamp = File.GetLastWriteTimeUtc(left);

        Assert.Equal(new FileInfo(left).Length, new FileInfo(replacement).Length);
        Assert.Equal(Sha256(replacement), Sha256(right));
        var reader = new SameLengthReplacingReader(left, replacement, originalTimestamp);
        var comparer = new WorkbookComparer(reader);
        WorkbookCompareResult? result = null;

        var exception = await Record.ExceptionAsync(async () =>
            result = await comparer.CompareAsync(left, right));

        Assert.True(reader.Replaced);
        Assert.Equal(originalTimestamp, File.GetLastWriteTimeUtc(left));
        if (exception is not null)
        {
            Assert.True(exception is IOException or InvalidOperationException, exception.ToString());
            Assert.True(
                exception.Message.Contains("变化", StringComparison.Ordinal) ||
                exception.Message.Contains("changed", StringComparison.OrdinalIgnoreCase) ||
                exception.Message.Contains("modified", StringComparison.OrdinalIgnoreCase),
                exception.Message);
            return;
        }

        Assert.NotNull(result);
        Assert.NotEqual(ComparisonStatus.Same, result.Status);
        Assert.Contains(
            result.Warnings.Concat(AllDifferences(result).Select(static difference => difference.Description)),
            message => message.Contains("变化", StringComparison.Ordinal) ||
                message.Contains("changed", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("modified", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class SameLengthReplacingReader(
        string targetPath,
        string replacementPath,
        DateTime originalTimestamp) : IWorkbookReader
    {
        private readonly OpenXmlWorkbookReader _inner = new();
        private int _replaced;

        public bool Replaced => Volatile.Read(ref _replaced) != 0;

        public Task<WorkbookInfo> ReadMetadataAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            ReplaceOnce();
            return _inner.ReadMetadataAsync(filePath, cancellationToken);
        }

        public async IAsyncEnumerable<CellSnapshot> ReadCellsAsync(
            string filePath,
            string worksheetName,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var cell in _inner.ReadCellsAsync(filePath, worksheetName, cancellationToken))
            {
                yield return cell;
            }
        }

        public Task<WorksheetPreview> LoadWorksheetPreviewAsync(
            string filePath,
            string worksheetName,
            CancellationToken cancellationToken = default) =>
            _inner.LoadWorksheetPreviewAsync(filePath, worksheetName, cancellationToken);

        private void ReplaceOnce()
        {
            if (Interlocked.Exchange(ref _replaced, 1) != 0)
            {
                return;
            }

            File.WriteAllBytes(targetPath, File.ReadAllBytes(replacementPath));
            File.SetLastWriteTimeUtc(targetPath, originalTimestamp);
        }
    }
}
