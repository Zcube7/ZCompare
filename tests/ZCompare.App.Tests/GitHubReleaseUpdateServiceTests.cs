using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using ZCompare.App.Services;
using ZCompare.App.ViewModels;

namespace ZCompare.App.Tests;

public sealed class GitHubReleaseUpdateServiceTests
{
    [Fact]
    public async Task NewVersionReturnsOfficialInstallerWithoutSendingUserData()
    {
        using var temporary = new TestDirectory();
        var handler = new RecordingHandler(_ => JsonResponse("0.1.1"));
        var service = CreateService(temporary, handler);

        var result = await service.CheckAsync(new Version(0, 1, 0));

        Assert.NotNull(result);
        Assert.Equal(new Version(0, 1, 1), result.Version);
        Assert.Equal(
            "https://github.com/Zcube7/ZCompare/releases/download/v0.1.1/ZCompare-0.1.1-win-x64-setup.exe",
            result.DownloadUri.AbsoluteUri);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/repos/Zcube7/ZCompare/releases/latest", request.Uri);
        Assert.Null(request.Body);
        Assert.Contains("ZCompare/0.1.0", request.UserAgent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.1.0")]
    [InlineData("0.0.9")]
    public async Task SameOrOlderVersionDoesNotPrompt(string releaseVersion)
    {
        using var temporary = new TestDirectory();
        var service = CreateService(temporary, new RecordingHandler(_ => JsonResponse(releaseVersion)));

        Assert.Null(await service.CheckAsync(new Version(0, 1, 0)));
    }

    [Fact]
    public async Task CacheLimitsNetworkChecksAndUsesEtagForRevalidation()
    {
        using var temporary = new TestDirectory();
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero));
        var handler = new RecordingHandler(call => call == 1
            ? JsonResponse("0.1.1", etag: "\"release-1\"")
            : new HttpResponseMessage(HttpStatusCode.NotModified));
        var service = CreateService(temporary, handler, time);

        Assert.NotNull(await service.CheckAsync(new Version(0, 1, 0)));
        Assert.NotNull(await service.CheckAsync(new Version(0, 1, 0)));
        Assert.Single(handler.Requests);

        time.Advance(TimeSpan.FromHours(25));
        Assert.NotNull(await service.CheckAsync(new Version(0, 1, 0)));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("\"release-1\"", handler.Requests[1].IfNoneMatch, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task HttpFailuresAreSilent(HttpStatusCode statusCode)
    {
        using var temporary = new TestDirectory();
        var service = CreateService(
            temporary,
            new RecordingHandler(_ => new HttpResponseMessage(statusCode)));

        Assert.Null(await service.CheckAsync(new Version(0, 1, 0)));
    }

    [Fact]
    public async Task MalformedJsonAndMissingOrUntrustedAssetAreSilent()
    {
        using var temporary = new TestDirectory();
        var responses = new[]
        {
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{broken", Encoding.UTF8, "application/json"),
            },
            JsonResponse("0.1.1", includeAsset: false),
            JsonResponse("0.1.1", assetUrl: "https://example.invalid/setup.exe"),
        };

        for (var index = 0; index < responses.Length; index++)
        {
            var service = new GitHubReleaseUpdateService(
                new HttpClient(new RecordingHandler(_ => responses[index])),
                TimeProvider.System,
                Path.Combine(temporary.Path, $"cache-{index}.json"));
            Assert.Null(await service.CheckAsync(new Version(0, 1, 0)));
        }
    }

    [Fact]
    public async Task TimeoutIsSilentAndStatusModelCanBeDismissed()
    {
        using var temporary = new TestDirectory();
        var service = CreateService(
            temporary,
            new RecordingHandler(_ => throw new TaskCanceledException("timeout")));
        Assert.Null(await service.CheckAsync(new Version(0, 1, 0)));

        var status = new UpdateStatusViewModel();
        status.Show(new UpdateCheckResult(
            new Version(0, 1, 1),
            new Uri("https://github.com/Zcube7/ZCompare/releases/download/v0.1.1/ZCompare-0.1.1-win-x64-setup.exe")));
        Assert.True(status.IsVisible);
        Assert.Contains("v0.1.1", status.Message, StringComparison.Ordinal);
        status.Dismiss();
        Assert.False(status.IsVisible);
    }

    private static GitHubReleaseUpdateService CreateService(
        TestDirectory temporary,
        HttpMessageHandler handler,
        TimeProvider? timeProvider = null) =>
        new(
            new HttpClient(handler),
            timeProvider ?? TimeProvider.System,
            Path.Combine(temporary.Path, "update-cache.json"));

    private static HttpResponseMessage JsonResponse(
        string version,
        bool includeAsset = true,
        string? etag = null,
        string? assetUrl = null)
    {
        var assetName = $"ZCompare-{version}-win-x64-setup.exe";
        var payload = JsonSerializer.Serialize(new
        {
            tag_name = $"v{version}",
            assets = includeAsset
                ? new[]
                {
                    new
                    {
                        name = assetName,
                        browser_download_url = assetUrl ??
                            $"https://github.com/Zcube7/ZCompare/releases/download/v{version}/{assetName}",
                    },
                }
                : [],
        });
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        if (etag is not null)
        {
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        }
        return response;
    }

    private sealed class RecordingHandler(Func<int, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        private int _calls;

        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.RequestUri?.AbsoluteUri,
                request.Headers.UserAgent.ToString(),
                request.Headers.IfNoneMatch.ToString(),
                request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult()));
            return Task.FromResult(responseFactory(Interlocked.Increment(ref _calls)));
        }
    }

    private sealed record RecordedRequest(string? Uri, string UserAgent, string IfNoneMatch, string? Body);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan value) => _now += value;
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ZCompare.UpdateTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
