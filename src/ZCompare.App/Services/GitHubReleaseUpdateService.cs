using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ZCompare.App.Services;

internal sealed record UpdateCheckResult(Version Version, Uri DownloadUri);

internal interface IReleaseUpdateService
{
    Task<UpdateCheckResult?> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default);
}

internal sealed class GitHubReleaseUpdateService : IReleaseUpdateService
{
    private static readonly Uri LatestReleaseUri = new(
        "https://api.github.com/repos/Zcube7/ZCompare/releases/latest");
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly string _cachePath;

    public GitHubReleaseUpdateService(
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null,
        string? cachePath = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cachePath = Path.GetFullPath(cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZCompare",
            "update-check.json"));
    }

    public async Task<UpdateCheckResult?> CheckAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        var now = _timeProvider.GetUtcNow();
        var cache = LoadCache();
        if (cache is not null && now - cache.LastCheckedUtc < CheckInterval)
        {
            return CreateResult(cache, currentVersion);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd($"ZCompare/{currentVersion.ToString(3)}");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (!string.IsNullOrWhiteSpace(cache?.ETag) &&
                EntityTagHeaderValue.TryParse(cache.ETag, out var entityTag))
            {
                request.Headers.IfNoneMatch.Add(entityTag);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            if (response.StatusCode == HttpStatusCode.NotModified && cache is not null)
            {
                var refreshed = cache with { LastCheckedUtc = now };
                SaveCache(refreshed);
                return CreateResult(refreshed, currentVersion);
            }

            if (!response.IsSuccessStatusCode)
            {
                SaveCache((cache ?? UpdateCache.Empty) with { LastCheckedUtc = now });
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            if (!TryReadRelease(document.RootElement, out var latestVersion, out var downloadUri))
            {
                SaveCache((cache ?? UpdateCache.Empty) with { LastCheckedUtc = now });
                return null;
            }

            var updated = new UpdateCache(
                now,
                response.Headers.ETag?.Tag,
                latestVersion.ToString(3),
                downloadUri.AbsoluteUri);
            SaveCache(updated);
            return latestVersion > currentVersion
                ? new UpdateCheckResult(latestVersion, downloadUri)
                : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or
                                           UnauthorizedAccessException or JsonException or
                                           TaskCanceledException or OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                SaveCache((cache ?? UpdateCache.Empty) with { LastCheckedUtc = now });
            }
            return null;
        }
    }

    private static bool TryReadRelease(
        JsonElement root,
        out Version version,
        out Uri downloadUri)
    {
        version = new Version(0, 0, 0);
        downloadUri = null!;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("tag_name", out var tagElement) ||
            !Version.TryParse(tagElement.GetString()?.TrimStart('v', 'V'), out var parsedVersion) ||
            !root.TryGetProperty("assets", out var assetsElement) ||
            assetsElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        version = parsedVersion;
        var expectedName = $"ZCompare-{version.ToString(3)}-win-x64-setup.exe";
        foreach (var asset in assetsElement.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var nameElement) ||
                !string.Equals(nameElement.GetString(), expectedName, StringComparison.OrdinalIgnoreCase) ||
                !asset.TryGetProperty("browser_download_url", out var urlElement) ||
                !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var candidate) ||
                !IsTrustedInstallerUri(candidate, expectedName))
            {
                continue;
            }

            downloadUri = candidate;
            return true;
        }
        return false;
    }

    private UpdateCache? LoadCache()
    {
        try
        {
            return File.Exists(_cachePath)
                ? JsonSerializer.Deserialize<UpdateCache>(File.ReadAllText(_cachePath))
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning($"读取更新缓存失败：{exception.Message}");
            return null;
        }
    }

    private void SaveCache(UpdateCache cache)
    {
        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            temporaryPath = _cachePath + $".{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(cache));
            File.Move(temporaryPath, _cachePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Trace.TraceWarning($"保存更新缓存失败：{exception.Message}");
            TryDelete(temporaryPath);
        }
    }

    private static UpdateCheckResult? CreateResult(UpdateCache cache, Version currentVersion) =>
        Version.TryParse(cache.LatestVersion, out var latestVersion) &&
        latestVersion > currentVersion &&
        Uri.TryCreate(cache.DownloadUrl, UriKind.Absolute, out var downloadUri) &&
        IsTrustedInstallerUri(
            downloadUri,
            $"ZCompare-{latestVersion.ToString(3)}-win-x64-setup.exe")
            ? new UpdateCheckResult(latestVersion, downloadUri)
            : null;

    private static bool IsTrustedInstallerUri(Uri uri, string expectedName) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith(
            "/Zcube7/ZCompare/releases/download/",
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath)),
            expectedName,
            StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record UpdateCache(
        DateTimeOffset LastCheckedUtc,
        string? ETag,
        string? LatestVersion,
        string? DownloadUrl)
    {
        public static UpdateCache Empty { get; } = new(DateTimeOffset.MinValue, null, null, null);
    }
}
