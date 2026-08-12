using System.IO;
using System.Text.Json;
using ZCompare.App.Services;
using ZCompare.App.ViewModels;
using ZCompare.Core;

namespace ZCompare.App.Tests;

public sealed class RecentComparisonTests
{
    [Fact]
    public void LegacyHistoryWithoutProfileFieldsLoadsWithSafeDefaults()
    {
        var testDirectory = CreateTestDirectory();
        try
        {
            var filePath = Path.Combine(testDirectory, "recent.json");
            File.WriteAllText(filePath, JsonSerializer.Serialize(new[]
            {
                new
                {
                    Mode = "Folders",
                    LeftPath = Path.Combine(testDirectory, "legacy-left"),
                    RightPath = Path.Combine(testDirectory, "legacy-right"),
                    LastUsedUtc = DateTimeOffset.UtcNow,
                },
            }));

            var entry = Assert.Single(new JsonRecentComparisonStore(filePath).Load());

            Assert.False(entry.IsProfile);
            Assert.Null(entry.Name);
            Assert.True(entry.IncludeSubdirectories);
            Assert.Equal("*.xlsx", entry.EffectiveFilePattern);
            Assert.Equal(RowAlignmentMode.Conservative, entry.EffectiveOptions.RowAlignmentMode);
            Assert.Equal(WorksheetPairingMode.Name, entry.EffectiveOptions.WorksheetPairingMode);
            Assert.True(entry.EffectiveOptions.CaseSensitive);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public void NamedProfileRoundTripsEveryComparisonAndFolderOption()
    {
        var testDirectory = CreateTestDirectory();
        try
        {
            var filePath = Path.Combine(testDirectory, "recent.json");
            var options = new ComparisonOptions
            {
                CompareFormulas = true,
                CompareFormatting = true,
                CompareFonts = true,
                CompareComments = true,
                CompareHyperlinks = true,
                CompareLayout = true,
                CaseSensitive = false,
                RowAlignmentMode = RowAlignmentMode.KeyColumns,
                KeyColumnRules = [new KeyColumnRule("Data", 2, ["A", "C"])],
                WorksheetPairingMode = WorksheetPairingMode.Manual,
                ManualWorksheetPairs = [new WorksheetPair("LeftData", "RightData")],
                ColumnMappings =
                [
                    new WorksheetColumnMapping(
                        "LeftData",
                        "RightData",
                        [new ColumnPair("A", "C")]),
                ],
                MaxFolderConcurrency = 1,
            };
            var store = new JsonRecentComparisonStore(filePath);
            store.Record(new RecentComparisonEntry(
                RecentComparisonMode.Folders,
                Path.Combine(testDirectory, "left"),
                Path.Combine(testDirectory, "right"),
                DateTimeOffset.UtcNow,
                Name: "每日配置",
                IsProfile: true,
                Options: options,
                IncludeSubdirectories: false,
                FilePattern: "config-??.xlsx"));

            var entry = Assert.Single(new JsonRecentComparisonStore(filePath).Load());
            using var savedDocument = JsonDocument.Parse(File.ReadAllText(filePath));
            Assert.Equal(1, savedDocument.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(JsonValueKind.Array, savedDocument.RootElement.GetProperty("entries").ValueKind);
            var loaded = entry.EffectiveOptions;

            Assert.True(entry.IsProfile);
            Assert.Equal("每日配置", entry.Name);
            Assert.False(entry.IncludeSubdirectories);
            Assert.Equal("config-??.xlsx", entry.EffectiveFilePattern);
            Assert.True(loaded.CompareFormulas);
            Assert.True(loaded.CompareFormatting);
            Assert.True(loaded.CompareFonts);
            Assert.True(loaded.CompareComments);
            Assert.True(loaded.CompareHyperlinks);
            Assert.True(loaded.CompareLayout);
            Assert.False(loaded.CaseSensitive);
            Assert.Equal(RowAlignmentMode.KeyColumns, loaded.RowAlignmentMode);
            var keyRule = Assert.Single(loaded.KeyColumnRules);
            Assert.Equal("Data", keyRule.WorksheetName);
            Assert.Equal(2, keyRule.HeaderRow);
            Assert.Equal(new[] { "A", "C" }, keyRule.ColumnIdentifiers);
            Assert.Equal(WorksheetPairingMode.Manual, loaded.WorksheetPairingMode);
            Assert.Equal(new WorksheetPair("LeftData", "RightData"), Assert.Single(loaded.ManualWorksheetPairs));
            var columnMapping = Assert.Single(loaded.ColumnMappings);
            Assert.Equal("LeftData", columnMapping.LeftWorksheetName);
            Assert.Equal("RightData", columnMapping.RightWorksheetName);
            Assert.Equal(new ColumnPair("A", "C"), Assert.Single(columnMapping.ColumnPairs));
            Assert.Equal(1, loaded.MaxFolderConcurrency);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public void RecordingSameNamedProfileReplacesItCaseInsensitively()
    {
        var testDirectory = CreateTestDirectory();
        try
        {
            var filePath = Path.Combine(testDirectory, "recent.json");
            var store = new JsonRecentComparisonStore(filePath);
            store.Record(new RecentComparisonEntry(
                RecentComparisonMode.Files,
                Path.Combine(testDirectory, "first-left.xlsx"),
                Path.Combine(testDirectory, "first-right.xlsx"),
                DateTimeOffset.UtcNow.AddMinutes(-1),
                Name: "Daily",
                IsProfile: true));
            store.Record(new RecentComparisonEntry(
                RecentComparisonMode.Files,
                Path.Combine(testDirectory, "latest-left.xlsx"),
                Path.Combine(testDirectory, "latest-right.xlsx"),
                DateTimeOffset.UtcNow,
                Name: "daily",
                IsProfile: true));

            var profile = Assert.Single(new JsonRecentComparisonStore(filePath).Load());

            Assert.Equal("daily", profile.Name);
            Assert.EndsWith("latest-left.xlsx", profile.LeftPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public void JsonStoreRoundTripsDeduplicatesAndKeepsTenNewestEntries()
    {
        var testDirectory = CreateTestDirectory();
        try
        {
            var filePath = Path.Combine(testDirectory, "recent.json");
            var store = new JsonRecentComparisonStore(filePath);
            var baseline = DateTimeOffset.UtcNow.AddMinutes(-20);
            for (var index = 0; index < 11; index++)
            {
                store.Record(new RecentComparisonEntry(
                    RecentComparisonMode.Files,
                    Path.Combine(testDirectory, $"left-{index}.xlsx"),
                    Path.Combine(testDirectory, $"right-{index}.xlsx"),
                    baseline.AddMinutes(index)));
            }

            var duplicate = store.Record(new RecentComparisonEntry(
                RecentComparisonMode.Files,
                Path.Combine(testDirectory, "LEFT-10.XLSX"),
                Path.Combine(testDirectory, "RIGHT-10.XLSX"),
                baseline.AddHours(1)));
            var reloaded = new JsonRecentComparisonStore(filePath).Load();

            Assert.Equal(10, duplicate.Count);
            Assert.Equal(10, reloaded.Count);
            Assert.DoesNotContain(reloaded, entry => entry.LeftPath.EndsWith("left-0.xlsx", StringComparison.OrdinalIgnoreCase));
            Assert.Single(reloaded, entry => entry.LeftPath.EndsWith("left-10.xlsx", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("LEFT-10.XLSX", Path.GetFileName(reloaded[0].LeftPath));
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public void JsonStoreIgnoresCorruptedHistory()
    {
        var testDirectory = CreateTestDirectory();
        try
        {
            var filePath = Path.Combine(testDirectory, "recent.json");
            File.WriteAllText(filePath, "{not-json");

            var entries = new JsonRecentComparisonStore(filePath).Load();

            Assert.Empty(entries);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public void SelectingHistoryOnlyFillsModeAndPathsAndManualChangesClearSelection()
    {
        var entry = new RecentComparisonEntry(
            RecentComparisonMode.Folders,
            @"C:\left-folder",
            @"C:\right-folder",
            DateTimeOffset.UtcNow);
        var store = new RecordingRecentComparisonStore(entry);
        var viewModel = TestViewModels.CreateMainWindow(recentComparisonStore: store);

        viewModel.SelectedRecentComparison = Assert.Single(viewModel.RecentComparisons);

        Assert.True(viewModel.IsFolderMode);
        Assert.Equal(entry.LeftPath, viewModel.LeftPath);
        Assert.Equal(entry.RightPath, viewModel.RightPath);
        Assert.Contains("已载入最近对比", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Equal(0, store.RecordCalls);

        viewModel.LeftPath = @"C:\manual-left";
        Assert.Null(viewModel.SelectedRecentComparison);

        viewModel.SelectedRecentComparison = entry;
        viewModel.IsFileMode = true;
        Assert.Null(viewModel.SelectedRecentComparison);
    }

    [Fact]
    public async Task StartingWithValidPathsRecordsFolderComparison()
    {
        var leftDirectory = CreateTestDirectory();
        var rightDirectory = CreateTestDirectory();
        try
        {
            var store = new RecordingRecentComparisonStore();
            var viewModel = TestViewModels.CreateMainWindow(
                folderComparer: new SuccessfulFolderComparer(),
                recentComparisonStore: store);
            viewModel.IsFolderMode = true;
            viewModel.LeftPath = leftDirectory;
            viewModel.RightPath = rightDirectory;

            viewModel.StartCommand.Execute(null);
            await WaitUntilAsync(() => store.RecordCalls == 1 && !viewModel.IsBusy);

            var recorded = Assert.IsType<RecentComparisonEntry>(store.LastRecorded);
            Assert.Equal(RecentComparisonMode.Folders, recorded.Mode);
            Assert.Equal(Path.GetFullPath(leftDirectory), recorded.LeftPath);
            Assert.Equal(Path.GetFullPath(rightDirectory), recorded.RightPath);
            Assert.Single(viewModel.RecentComparisons);
        }
        finally
        {
            Directory.Delete(leftDirectory, recursive: true);
            Directory.Delete(rightDirectory, recursive: true);
        }
    }

    [Fact]
    public void ViewModelSavesAndReloadsNamedProfileWithoutStartingComparison()
    {
        var testDirectory = CreateTestDirectory();
        try
        {
            var store = new RecordingRecentComparisonStore();
            var viewModel = TestViewModels.CreateMainWindow(recentComparisonStore: store);
            viewModel.IsFolderMode = true;
            viewModel.LeftPath = Path.Combine(testDirectory, "left");
            viewModel.RightPath = Path.Combine(testDirectory, "right");
            viewModel.CompareFormulas = true;
            viewModel.CompareFormatting = true;
            viewModel.CompareFonts = true;
            viewModel.CompareComments = true;
            viewModel.CompareHyperlinks = true;
            viewModel.CompareLayout = true;
            viewModel.CaseSensitive = false;
            viewModel.StrictRowNumberComparison = true;
            viewModel.IncludeSubdirectories = false;
            viewModel.FolderFilePattern = "daily-*.xlsx";
            viewModel.ApplyWorksheetSettings(
                WorksheetPairingMode.Index,
                useKeyColumnAlignment: false,
                manualPairs: [],
                keyColumnRules: [],
                columnMappings:
                [
                    new WorksheetColumnMapping(
                        "LeftData",
                        "RightData",
                        [new ColumnPair("A", "C")]),
                ]);

            Assert.True(viewModel.SaveProfile("  工作配置  "));

            var saved = Assert.IsType<RecentComparisonEntry>(store.LastRecorded);
            Assert.Equal(1, store.RecordCalls);
            Assert.True(saved.IsProfile);
            Assert.Equal("工作配置", saved.Name);
            Assert.False(saved.IncludeSubdirectories);
            Assert.Equal("daily-*.xlsx", saved.EffectiveFilePattern);
            Assert.True(saved.EffectiveOptions.CompareFormulas);
            Assert.True(saved.EffectiveOptions.CompareFormatting);
            Assert.True(saved.EffectiveOptions.CompareFonts);
            Assert.True(saved.EffectiveOptions.CompareComments);
            Assert.True(saved.EffectiveOptions.CompareHyperlinks);
            Assert.True(saved.EffectiveOptions.CompareLayout);
            Assert.False(saved.EffectiveOptions.CaseSensitive);
            Assert.Equal(RowAlignmentMode.StrictRowNumber, saved.EffectiveOptions.RowAlignmentMode);
            Assert.Equal(
                new ColumnPair("A", "C"),
                Assert.Single(Assert.Single(saved.EffectiveOptions.ColumnMappings).ColumnPairs));
            Assert.Contains("不会自动开始比较", viewModel.StatusText, StringComparison.Ordinal);
            Assert.False(viewModel.IsBusy);

            var reloaded = TestViewModels.CreateMainWindow(
                recentComparisonStore: new RecordingRecentComparisonStore(saved));
            reloaded.SelectedRecentComparison = Assert.Single(reloaded.RecentComparisons);

            Assert.True(reloaded.IsFolderMode);
            Assert.Equal(saved.LeftPath, reloaded.LeftPath);
            Assert.Equal(saved.RightPath, reloaded.RightPath);
            Assert.True(reloaded.CompareFormulas);
            Assert.True(reloaded.CompareFormatting);
            Assert.True(reloaded.CompareFonts);
            Assert.True(reloaded.CompareComments);
            Assert.True(reloaded.CompareHyperlinks);
            Assert.True(reloaded.CompareLayout);
            Assert.False(reloaded.CaseSensitive);
            Assert.True(reloaded.StrictRowNumberComparison);
            Assert.False(reloaded.IncludeSubdirectories);
            Assert.Equal("daily-*.xlsx", reloaded.FolderFilePattern);
            var reloadedMapping = Assert.Single(reloaded.CurrentComparisonOptions.ColumnMappings);
            Assert.Equal("LeftData", reloadedMapping.LeftWorksheetName);
            Assert.Equal("RightData", reloadedMapping.RightWorksheetName);
            Assert.Equal(new ColumnPair("A", "C"), Assert.Single(reloadedMapping.ColumnPairs));
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
                throw new TimeoutException("Timed out waiting for recent comparison state.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class RecordingRecentComparisonStore(params RecentComparisonEntry[] entries) : IRecentComparisonStore
    {
        private readonly List<RecentComparisonEntry> _entries = [.. entries];

        public int RecordCalls { get; private set; }

        public RecentComparisonEntry? LastRecorded { get; private set; }

        public IReadOnlyList<RecentComparisonEntry> Load() => _entries.ToArray();

        public IReadOnlyList<RecentComparisonEntry> Record(RecentComparisonEntry entry)
        {
            RecordCalls++;
            LastRecorded = entry;
            _entries.RemoveAll(existing =>
                existing.Mode == entry.Mode &&
                string.Equals(existing.LeftPath, entry.LeftPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.RightPath, entry.RightPath, StringComparison.OrdinalIgnoreCase));
            _entries.Insert(0, entry);
            return _entries.Take(10).ToArray();
        }
    }

    private sealed class SuccessfulFolderComparer : IFolderComparer
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

        public Task<FolderCompareResult> ScanAsync(
            string leftDirectory,
            string rightDirectory,
            FolderScanOptions scanOptions,
            IProgress<ComparisonProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            ScanAsync(leftDirectory, rightDirectory, progress, cancellationToken);

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
