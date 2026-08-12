using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZCompare.Core;

namespace ZCompare.App.Services;

internal enum RecentComparisonMode
{
    Files,
    Folders,
}

internal sealed record RecentComparisonEntry(
    RecentComparisonMode Mode,
    string LeftPath,
    string RightPath,
    DateTimeOffset LastUsedUtc,
    string? Name = null,
    bool IsProfile = false,
    ComparisonOptions? Options = null,
    bool IncludeSubdirectories = true,
    string FilePattern = "*.xlsx")
{
    [JsonIgnore]
    public string DisplayText => IsProfile
        ? $"★ {Name} · {(Mode == RecentComparisonMode.Files ? "文件" : "文件夹")}"
        : $"{(Mode == RecentComparisonMode.Files ? "文件" : "文件夹")} · {LeafName(LeftPath)} ↔ {LeafName(RightPath)}";

    [JsonIgnore]
    public string ToolTipText => $"{(IsProfile ? $"配置：{Name}\n" : string.Empty)}左：{LeftPath}\n右：{RightPath}\n" +
        $"工作表：{WorksheetPairingText()}；行对齐：{RowAlignmentText()}；列映射：{EffectiveOptions.ColumnMappings.Count} 组；" +
        $"公式/格式/字体/批注/链接/布局：{OptionFlags()}\n" +
        $"子目录：{(IncludeSubdirectories ? "包含" : "不包含")}；文件：{EffectiveFilePattern}\n" +
        $"最近使用：{LastUsedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";

    [JsonIgnore]
    public ComparisonOptions EffectiveOptions => Options ?? new ComparisonOptions();

    [JsonIgnore]
    public string EffectiveFilePattern => string.IsNullOrWhiteSpace(FilePattern) ? "*.xlsx" : FilePattern;

    private static string LeafName(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private string OptionFlags()
    {
        var options = EffectiveOptions;
        return string.Join(
            '/',
            new[]
            {
                options.CompareFormulas,
                options.CompareFormatting,
                options.CompareFonts,
                options.CompareComments,
                options.CompareHyperlinks,
                options.CompareLayout,
            }.Select(static enabled => enabled ? "开" : "关"));
    }

    private string RowAlignmentText() => EffectiveOptions.RowAlignmentMode switch
    {
        RowAlignmentMode.StrictRowNumber => "严格原行号",
        RowAlignmentMode.KeyColumns => $"关键列（{EffectiveOptions.KeyColumnRules.Count} 条规则）",
        _ => "保守对齐",
    };

    private string WorksheetPairingText() => EffectiveOptions.WorksheetPairingMode switch
    {
        WorksheetPairingMode.Index => "按顺序",
        WorksheetPairingMode.Manual => $"手工（{EffectiveOptions.ManualWorksheetPairs.Count} 组）",
        _ => "按名称",
    };
}

internal interface IRecentComparisonStore
{
    IReadOnlyList<RecentComparisonEntry> Load();

    IReadOnlyList<RecentComparisonEntry> Record(RecentComparisonEntry entry);
}

internal sealed class JsonRecentComparisonStore : IRecentComparisonStore
{
    private const int CurrentSchemaVersion = 1;
    private const int MaximumRecentEntries = 10;
    private const int MaximumProfiles = 50;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;

    public JsonRecentComparisonStore(string? filePath = null)
    {
        _filePath = Path.GetFullPath(filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZCompare",
            "recent-comparisons.json"));
    }

    public IReadOnlyList<RecentComparisonEntry> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return [];
            }

            using var document = JsonDocument.Parse(File.ReadAllText(_filePath));
            JsonElement entriesElement;
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                entriesElement = document.RootElement;
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object &&
                     document.RootElement.TryGetProperty("schemaVersion", out var schemaElement) &&
                     schemaElement.TryGetInt32(out var schemaVersion) &&
                     schemaVersion == CurrentSchemaVersion &&
                     document.RootElement.TryGetProperty("entries", out entriesElement) &&
                     entriesElement.ValueKind == JsonValueKind.Array)
            {
            }
            else
            {
                return [];
            }

            var entries = new List<RecentComparisonEntry>();
            foreach (var element in entriesElement.EnumerateArray())
            {
                var entry = element.Deserialize<RecentComparisonEntry>(JsonOptions);
                if (entry is null)
                {
                    continue;
                }

                // Version 1 only stored mode, paths, and time. Preserve its original
                // recursive *.xlsx behavior when the newly added fields are absent.
                if (!HasProperty(element, nameof(RecentComparisonEntry.IncludeSubdirectories)))
                {
                    entry = entry with { IncludeSubdirectories = true };
                }
                if (!HasProperty(element, nameof(RecentComparisonEntry.FilePattern)))
                {
                    entry = entry with { FilePattern = "*.xlsx" };
                }
                entries.Add(entry);
            }

            return Normalize(entries);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning($"读取最近对比记录失败：{exception.Message}");
            return [];
        }
    }

    public IReadOnlyList<RecentComparisonEntry> Record(RecentComparisonEntry entry)
    {
        if (!TryNormalize(entry, out var normalized))
        {
            return Load();
        }

        var key = ComparisonKey(normalized);
        var entries = new[] { normalized }
            .Concat(Load().Where(item => !string.Equals(ComparisonKey(item), key, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        entries = Normalize(entries).ToArray();

        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            temporaryPath = _filePath + $".{Guid.NewGuid():N}.tmp";
            var document = new RecentComparisonDocument(CurrentSchemaVersion, entries);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Trace.TraceWarning($"保存最近对比记录失败：{exception.Message}");
            TryDelete(temporaryPath);
        }

        return entries;
    }

    private static IReadOnlyList<RecentComparisonEntry> Normalize(IEnumerable<RecentComparisonEntry> source)
    {
        var normalizedSource = source
            .Select(item => TryNormalize(item, out var normalized) ? normalized : null)
            .OfType<RecentComparisonEntry>()
            .ToArray();
        var profiles = normalizedSource
            .Where(static item => item.IsProfile)
            .GroupBy(static item => ComparisonKey(item), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(static item => item.LastUsedUtc).First())
            .OrderBy(static item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaximumProfiles);
        var recent = normalizedSource
            .Where(static item => !item.IsProfile)
            .GroupBy(static item => ComparisonKey(item), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(static item => item.LastUsedUtc).First())
            .OrderByDescending(static item => item.LastUsedUtc)
            .Take(MaximumRecentEntries);
        return profiles.Concat(recent).ToArray();
    }

    private static bool TryNormalize(RecentComparisonEntry entry, out RecentComparisonEntry normalized)
    {
        normalized = entry;
        if (!Enum.IsDefined(entry.Mode) ||
            string.IsNullOrWhiteSpace(entry.LeftPath) ||
            string.IsNullOrWhiteSpace(entry.RightPath) ||
            (entry.IsProfile && string.IsNullOrWhiteSpace(entry.Name)) ||
            !Path.IsPathFullyQualified(entry.LeftPath) ||
            !Path.IsPathFullyQualified(entry.RightPath))
        {
            return false;
        }

        try
        {
            var options = entry.EffectiveOptions with
            {
                KeyColumnRules = entry.EffectiveOptions.KeyColumnRules ?? [],
                ManualWorksheetPairs = entry.EffectiveOptions.ManualWorksheetPairs ?? [],
                ColumnMappings = entry.EffectiveOptions.ColumnMappings ?? [],
            };
            normalized = entry with
            {
                LeftPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.LeftPath)),
                RightPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.RightPath)),
                LastUsedUtc = entry.LastUsedUtc.ToUniversalTime(),
                Name = entry.IsProfile ? entry.Name!.Trim() : null,
                Options = options,
                FilePattern = entry.EffectiveFilePattern.Trim(),
            };
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Trace.TraceWarning($"忽略无效的最近对比记录：{exception.Message}");
            return false;
        }
    }

    private static string ComparisonKey(RecentComparisonEntry entry) =>
        entry.IsProfile
            ? $"Profile\0{entry.Name}"
            : $"Recent\0{entry.Mode}\0{entry.LeftPath}\0{entry.RightPath}";

    private static bool HasProperty(JsonElement element, string name) =>
        element.EnumerateObject().Any(property =>
            string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase));

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record RecentComparisonDocument(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("entries")] IReadOnlyList<RecentComparisonEntry> Entries);
}
