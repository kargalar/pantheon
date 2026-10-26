using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using VideoPlayer.Core;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class SubtitleAutomationTests
{
    private const string ValidSrt = "1\n00:00:01,000 --> 00:00:03,000\nTürkçe altyazı\n";

    [Theory]
    [InlineData("Breaking.Bad.S02E03.1080p.WEB-DL.mkv", "Breaking Bad", null, 2, 3)]
    [InlineData("Show Name 1x07 HDTV.mkv", "Show Name", null, 1, 7)]
    [InlineData("The.Matrix.1999.1080p.BluRay.mkv", "The Matrix", 1999, null, null)]
    [InlineData("2001.A.Space.Odyssey.1968.1080p.mkv", "2001 A Space Odyssey", 1968, null, null)]
    public void MediaIdentityExtractsFilmOrEpisode(string path, string title, int? year, int? season, int? episode)
    {
        var result = SubtitleMediaIdentity.FromPath(path);
        Assert.Equal(title, result.Title);
        Assert.Equal(year, result.Year);
        Assert.Equal(season, result.Season);
        Assert.Equal(episode, result.Episode);
    }

    [Theory]
    [InlineData("Show.S01E01E02.mkv")]
    [InlineData("Show.S01E01-E02.mkv")]
    public void MultiEpisodeFilesCannotBeMatchedToASingleEpisode(string path) =>
        Assert.True(SubtitleMediaIdentity.FromPath(path).IsMultiEpisode);

    [Fact]
    public void SimilarFilenameNeverSelectsAnotherEpisode()
    {
        Assert.Null(MediaPathRules.FindMatchingSubtitle("Show.S01E01.1080p.mkv", ["Show.S01E02.1080p.srt"]));
        Assert.Null(MediaPathRules.FindMatchingSubtitle("Show.S02E01.1080p.mkv", ["Show.S01E01.1080p.srt"]));
        Assert.Null(MediaPathRules.FindMatchingSubtitle("Movie.2020.1080p.mkv", ["Movie.2021.1080p.srt"]));
    }

    [Fact]
    public async Task OnlineLookupReportsMissingCredentialsEvenWhenLocalSubtitleExists()
    {
        using var folder = new TestFolder();
        var video = Path.Combine(folder.Path, "Show.Name.S02E03.mkv");
        await File.WriteAllTextAsync(video, "video");
        await File.WriteAllTextAsync(Path.ChangeExtension(video, ".srt"), ValidSrt);
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("Unexpected request"));
        using var http = new HttpClient(handler);
        var lookup = new OnlineSubtitleLookup(new OpenSubtitlesClient(http), new SubtitleFileCache(Path.Combine(folder.Path, "cache")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => lookup.FindAsync(video, "tr", "", false, null, default));
        Assert.Contains("API key", error.Message);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("Show.Name.S02E03.mkv", true)]
    [InlineData("The.Matrix.1999.mkv", false)]
    public async Task OnlineLookupSearchesRepositoryDespiteLocalFilesAndReusesOnlyDownloadedCache(string filename, bool isEpisode)
    {
        using var folder = new TestFolder();
        var video = Path.Combine(folder.Path, filename);
        await File.WriteAllTextAsync(video, "video");
        await File.WriteAllTextAsync(Path.ChangeExtension(video, ".srt"), ValidSrt.Replace("Türkçe", "LOCAL_ONLY"));
        var subtitles = Directory.CreateDirectory(Path.Combine(folder.Path, "Subs")).FullName;
        await File.WriteAllTextAsync(Path.Combine(subtitles, Path.GetFileNameWithoutExtension(video) + ".tr.srt"), ValidSrt);
        var handler = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/subtitles")
            {
                Assert.Contains(isEpisode ? "type=episode" : "type=movie", request.RequestUri.Query);
                object entry = isEpisode ? Entry(42, 2, 3, 10) : new
                {
                    attributes = new
                    {
                        language = "tr",
                        moviehash_match = false,
                        feature_details = new { feature_type = "Movie", title = "The Matrix", year = 1999 },
                        files = new[] { new { file_id = 42 } },
                        download_count = 10,
                        release = "release"
                    }
                };
                return Task.FromResult(Json(new { data = new[] { entry } }));
            }
            if (request.RequestUri.AbsolutePath == "/api/v1/download")
                return Task.FromResult(Json(new { link = "https://repository.example/subtitle.srt" }));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ValidSrt) });
        });
        using var http = new HttpClient(handler);
        var cache = new SubtitleFileCache(Path.Combine(folder.Path, "cache"));
        var lookup = new OnlineSubtitleLookup(new OpenSubtitlesClient(http), cache);
        var stages = new List<OnlineSubtitleStage>();
        var result = await lookup.FindAsync(video, "tr", "key", false, stages.Add, default);
        Assert.NotNull(result);
        Assert.False(result.FromCache);
        Assert.Equal(cache.DownloadPath(video, "tr"), result.Path);
        Assert.Equal(ValidSrt, await File.ReadAllTextAsync(result.Path));
        Assert.Equal([OnlineSubtitleStage.Searching, OnlineSubtitleStage.Downloading], stages);
        Assert.Equal(3, handler.RequestCount);
        var cached = await lookup.FindAsync(video, "tr", "", false, null, default);
        Assert.True(cached!.FromCache);
        Assert.Equal(3, handler.RequestCount);
        await lookup.FindAsync(video, "tr", "key", true, null, default);
        Assert.Equal(6, handler.RequestCount);
    }

    [Fact]
    public async Task MovieHashUsesFirstAndLastBlocksWithUnsignedOverflow()
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Path, "Movie.mkv");
        await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)255, 128 * 1024).ToArray());
        Assert.Equal("000000000001c000", await SubtitleFileCache.ComputeMovieHashAsync(path, default));
        await File.WriteAllBytesAsync(path, new byte[100]);
        Assert.Null(await SubtitleFileCache.ComputeMovieHashAsync(path, default));
    }

    [Fact]
    public async Task ProviderSearchRejectsWrongEpisodeAndDownloadsUtf8WithoutLeakingCredentials()
    {
        using var folder = new TestFolder();
        var video = Path.Combine(folder.Path, "Show.Name.S02E03.1080p.mkv");
        await File.WriteAllTextAsync(video, "small video fixture");
        var handler = new StubHandler(async (request, cancellation) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/subtitles")
            {
                Assert.Contains("episode_number=3", request.RequestUri.Query);
                Assert.Contains("season_number=2", request.RequestUri.Query);
                Assert.Contains("query=Show+Name", request.RequestUri.Query);
                Assert.Equal("secret-key", request.Headers.GetValues("Api-Key").Single());
                return Json(new { data = new[] { Entry(99, 2, 4, 10000), Entry(42, 2, 3, 10) } });
            }
            if (request.RequestUri.AbsolutePath == "/api/v1/download")
            {
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellation);
                Assert.Equal(42, body.GetProperty("file_id").GetInt32());
                Assert.True(body.GetProperty("in_utf8").GetBoolean());
                Assert.Equal("srt", body.GetProperty("sub_format").GetString());
                return Json(new { link = "https://downloads.example/subtitle.srt" });
            }
            Assert.Equal("downloads.example", request.RequestUri.Host);
            Assert.False(request.Headers.Contains("Api-Key"));
            Assert.Null(request.Headers.Authorization);
            return new(HttpStatusCode.OK) { Content = new StringContent(ValidSrt, Encoding.UTF8) };
        });
        using var http = new HttpClient(handler);
        var client = new OpenSubtitlesClient(http);
        var candidate = await client.FindAsync(video, "tr", "secret-key", default);
        Assert.NotNull(candidate);
        var destination = new SubtitleFileCache(folder.Path).DownloadPath(video, "tr");
        await client.DownloadAsync(candidate, destination, "secret-key", default);
        Assert.Contains("Türkçe", await File.ReadAllTextAsync(destination));
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task HashMatchHasPriorityOverTitleSearch()
    {
        using var folder = new TestFolder();
        var video = Path.Combine(folder.Path, "Obscure.Movie.2020.mkv");
        await File.WriteAllBytesAsync(video, new byte[128 * 1024]);
        var handler = new StubHandler((request, _) =>
        {
            Assert.Contains("moviehash=", request.RequestUri!.Query);
            return Task.FromResult(Json(new
            {
                data = new[] { new { attributes = new { language = "tr", moviehash_match = true,
                    feature_details = new { feature_type = "Movie", title = "Original title", year = 2020 },
                    files = new[] { new { file_id = 12 } }, download_count = 1, release = "exact release" } } }
            }));
        });
        using var http = new HttpClient(handler);
        var match = await new OpenSubtitlesClient(http).FindAsync(video, "tr", "key", default);
        Assert.True(match!.HashMatch);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task SearchFollowsRedirectWhenApiReturnsMovedPermanently()
    {
        var redirectCount = 0;
        var handler = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.Query.Contains("query=Barry+Lyndon"))
            {
                redirectCount++;
                var redirect = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
                redirect.Headers.Location = new Uri("https://api.opensubtitles.com/api/v1/subtitles?languages=tr&order_by=download_count&order_direction=desc&query=barry+lyndon&type=movie");
                return Task.FromResult(redirect);
            }
            Assert.Contains("query=barry+lyndon", request.RequestUri.Query);
            Assert.Equal("secret-key", request.Headers.GetValues("Api-Key").Single());
            return Task.FromResult(Json(new
            {
                data = new[] { new { attributes = new { language = "tr", moviehash_match = false,
                    feature_details = new { feature_type = "Movie", title = "Barry Lyndon", year = 1975 },
                    files = new[] { new { file_id = 99 } }, download_count = 100, release = "Barry Lyndon (1975)" } } }
            }));
        });
        using var http = new HttpClient(handler);
        var client = new OpenSubtitlesClient(http);
        var results = await client.SearchByQueryAsync("Barry Lyndon", "tr", "secret-key", default);
        Assert.Single(results);
        Assert.Equal(99, results[0].FileId);
        Assert.Equal(1, redirectCount);
    }

    [Fact]
    public async Task DownloadFollowsHttpsRedirectsWithoutSendingApiKey()
    {
        using var folder = new TestFolder();
        var handler = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "api.opensubtitles.com")
                return Task.FromResult(Json(new { link = "https://files.example/start" }));
            Assert.False(request.Headers.Contains("Api-Key"));
            if (request.RequestUri.AbsolutePath == "/start")
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("https://cdn.example/final");
                return Task.FromResult(response);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ValidSrt) });
        });
        using var http = new HttpClient(handler);
        var path = Path.Combine(folder.Path, "subtitle.srt");
        await new OpenSubtitlesClient(http).DownloadAsync(new(42, "release", true, 0), path, "key", default);
        Assert.True(SubtitleFileCache.IsValidSrt(path));
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task CancelingAProviderSearchCancelsItsHttpRequest()
    {
        using var folder = new TestFolder();
        var video = Path.Combine(folder.Path, "Movie.2020.mkv");
        await File.WriteAllTextAsync(video, "video");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new StubHandler(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var cancellation = new CancellationTokenSource();
        var search = new OpenSubtitlesClient(http).FindAsync(video, "tr", "key", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "quota")]
    [InlineData(HttpStatusCode.Unauthorized, "API key")]
    public async Task ProviderErrorsExplainTheProblemWithoutExposingApiKey(HttpStatusCode status, string message)
    {
        using var folder = new TestFolder();
        var video = Path.Combine(folder.Path, "Movie.2020.mkv");
        await File.WriteAllTextAsync(video, "video");
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status))));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new OpenSubtitlesClient(http).FindAsync(video, "tr", "secret-value", default));
        Assert.Contains(message, error.Message);
        Assert.DoesNotContain("secret-value", error.Message);
    }

    [Fact]
    public async Task InvalidDownloadCannotReplaceAnExistingSubtitle()
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Path, "cached.srt");
        await SubtitleFileCache.WriteAsync(path, Encoding.UTF8.GetBytes(ValidSrt), default);
        await Assert.ThrowsAsync<InvalidDataException>(() => SubtitleFileCache.WriteAsync(path,
            Encoding.UTF8.GetBytes("<html>quota exceeded</html>"), default));
        Assert.Equal(ValidSrt, await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task CacheChangesWithMediaLanguageContentAudioAndEncoding()
    {
        using var folder = new TestFolder();
        var video = Path.Combine(folder.Path, "movie.mkv");
        var subtitle = Path.Combine(folder.Path, "movie.srt");
        await File.WriteAllTextAsync(video, "video");
        await File.WriteAllTextAsync(subtitle, ValidSrt);
        var cache = new SubtitleFileCache(folder.Path);
        Assert.NotEqual(cache.DownloadPath(video, "tr"), cache.DownloadPath(video, "en"));
        var first = await cache.SynchronizationPathAsync(video, subtitle, "0:a:0", "infer", default);
        Assert.NotEqual(first, await cache.SynchronizationPathAsync(video, subtitle, "0:a:1", "infer", default));
        Assert.NotEqual(first, await cache.SynchronizationPathAsync(video, subtitle, "0:a:0", "cp1254", default));
        await File.AppendAllTextAsync(subtitle, "\nnew cue");
        Assert.NotEqual(first, await cache.SynchronizationPathAsync(video, subtitle, "0:a:0", "infer", default));
        var originalDownload = cache.DownloadPath(video, "tr");
        await File.AppendAllTextAsync(video, "new content");
        Assert.NotEqual(originalDownload, cache.DownloadPath(video, "tr"));
    }

    [Theory]
    [InlineData(new[] { "hdmv_pgs_subtitle", "subrip" }, null, null, 1)]
    [InlineData(new[] { "subrip", "ass" }, null, null, 0)]
    [InlineData(new[] { "subrip", "ass" }, null, 1, 1)]
    [InlineData(new[] { "hdmv_pgs_subtitle", "dvd_subtitle" }, null, null, null)]
    [InlineData(new[] { "subrip" }, true, null, null)]
    public void EmbeddedTextSubtitleIsPreferredOverBitmapForcedAndMissingTracks(
        string[] codecs, bool? forcedFirst, int? selected, int? expected)
    {
        var tracks = codecs.Select((codec, index) => new EmbeddedSubtitleTrack(
            codec, forcedFirst == true && index == 0, selected == index)).ToArray();
        Assert.Equal(expected, SubtitleReferenceRules.SelectEmbeddedTextSubtitle(tracks));
    }

    [Theory]
    [InlineData("score: 600\noffset seconds: -3.1\nframerate scale factor: 1.0\ntotal of speech segments: 1000", true)]
    [InlineData("score: -10\noffset seconds: 3\nframerate scale factor: 1.0\ntotal of speech segments: 1000", false)]
    [InlineData("score: 60\noffset seconds: 60\nframerate scale factor: 1.0\ntotal of speech segments: 1000", false)]
    [InlineData("score: 600\noffset seconds: -3\nframerate scale factor: 1.2\ntotal of speech segments: 1000", false)]
    [InlineData("score: 600\noffset seconds: -3\nframerate scale factor: 1.0\ntotal of speech segments: 0", false)]
    [InlineData("failed to sync\nscore: 600\noffset seconds: -3\nframerate scale factor: 1.0\ntotal of speech segments: 1000", false)]
    public void SynchronizationRejectsSilenceExtremeOffsetsAndFailures(string log, bool accepted) =>
        Assert.Equal(accepted, SubtitleSynchronizer.ParseAlignment(log).HasValue);

    private static object Entry(int fileId, int season, int episode, int downloads) => new
    {
        attributes = new
        {
            language = "tr",
            moviehash_match = false,
            feature_details = new { feature_type = "Episode", parent_title = "Show Name", season_number = season, episode_number = episode },
            files = new[] { new { file_id = fileId } },
            download_count = downloads,
            release = "release"
        }
    };

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return send(request, cancellationToken);
        }
    }

    private sealed class TestFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pantheon-subtitles-{Guid.NewGuid():N}")).FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
