using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace VideoPlayer.Core;

public sealed record SubtitleCandidate(int FileId, string Release, bool HashMatch, int Downloads, string Language = "");

/// <summary>Requests use per-message credentials so download hosts never receive the API key.</summary>
public sealed class OpenSubtitlesClient(HttpClient httpClient)
{
    private const int DownloadBufferBytes = 8192;
    private static readonly Uri ApiBase = new("https://api.opensubtitles.com/api/v1/");

    public async Task<SubtitleCandidate?> FindAsync(
        string videoPath, string language, string apiKey, CancellationToken cancellationToken)
    {
        var identity = SubtitleMediaIdentity.FromPath(videoPath);
        if (string.IsNullOrWhiteSpace(identity.Title) || identity.IsMultiEpisode) return null;
        var hash = await SubtitleFileCache.ComputeMovieHashAsync(videoPath, cancellationToken);
        if (hash is not null)
        {
            var hashMatches = await SearchAsync(identity, language, apiKey, hash, cancellationToken);
            var exact = hashMatches.FirstOrDefault(candidate => candidate.HashMatch);
            if (exact is not null) return exact;
        }
        return (await SearchAsync(identity, language, apiKey, null, cancellationToken)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchCandidatesForVideoAsync(
        string videoPath, string language, string apiKey, CancellationToken cancellationToken)
    {
        var identity = SubtitleMediaIdentity.FromPath(videoPath);
        if (string.IsNullOrWhiteSpace(identity.Title)) return Array.Empty<SubtitleCandidate>();
        var results = new List<SubtitleCandidate>();
        var hash = await SubtitleFileCache.ComputeMovieHashAsync(videoPath, cancellationToken);
        if (hash is not null)
        {
            var hashMatches = await SearchAsync(identity, language, apiKey, hash, cancellationToken);
            results.AddRange(hashMatches);
        }
        var textMatches = await SearchByQueryAsync(identity.Title, language, apiKey, cancellationToken);
        foreach (var match in textMatches)
        {
            if (!results.Any(r => r.FileId == match.FileId))
                results.Add(match);
        }
        return results;
    }

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchByQueryAsync(
        string query, string language, string apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<SubtitleCandidate>();
        var parameters = new SortedDictionary<string, string>
        {
            ["languages"] = language,
            ["order_by"] = "download_count",
            ["order_direction"] = "desc",
            ["query"] = query.Trim()
        };
        var queryString = string.Join('&', parameters.Select(pair => $"{pair.Key}={WebUtility.UrlEncode(pair.Value)}"));
        using var response = await SendApiRequestAsync(HttpMethod.Get, $"subtitles?{queryString}", apiKey, null, cancellationToken);
        await EnsureSuccessAsync(response);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return ParseCandidatesAny(json.RootElement);
    }

    public static IReadOnlyList<SubtitleCandidate> ParseCandidatesAny(JsonElement root)
    {
        var candidates = new List<SubtitleCandidate>();
        if (root.ValueKind != JsonValueKind.Object) return candidates;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return candidates;
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("attributes", out var attributes)
                || attributes.ValueKind != JsonValueKind.Object) continue;
            var release = Text(attributes, "release");
            if (string.IsNullOrWhiteSpace(release) && attributes.TryGetProperty("feature_details", out var feat))
                release = Text(feat, "title");
            if (!attributes.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) continue;
            if (files.GetArrayLength() == 0) continue;
            var fileId = Number(files[0], "file_id");
            if (fileId <= 0) continue;
            var lang = Text(attributes, "language");
            candidates.Add(new(fileId, string.IsNullOrWhiteSpace(release) ? $"Subtitle #{fileId}" : release, false, Number(attributes, "download_count"), lang));
        }
        return candidates.OrderByDescending(c => c.Downloads).ToArray();
    }

    public async Task DownloadAsync(
        SubtitleCandidate candidate, string path, string apiKey, CancellationToken cancellationToken)
    {
        var postContent = JsonContent.Create(new { file_id = candidate.FileId, sub_format = "srt", in_utf8 = true });
        using var response = await SendApiRequestAsync(HttpMethod.Post, "download", apiKey, postContent, cancellationToken);
        await EnsureSuccessAsync(response);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (!Uri.TryCreate(Text(json.RootElement, "link"), UriKind.Absolute, out var link)
            || link.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException(UiText.OpenSubtitlesDidNotReturnAValidDownloadURL);

        // A fresh request deliberately has no Api-Key or Authorization header.
        using var download = await GetDownloadResponseAsync(link, cancellationToken);
        await EnsureSuccessAsync(download);
        if (download.Content.Headers.ContentLength > SubtitleAutomationOptions.MaximumSubtitleBytes)
            throw new InvalidDataException(UiText.TheSubtitleFileExceedsTheAllowedSize);
        await using var stream = await download.Content.ReadAsStreamAsync(cancellationToken);
        using var bytes = new MemoryStream();
        var buffer = new byte[DownloadBufferBytes];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (bytes.Length + count > SubtitleAutomationOptions.MaximumSubtitleBytes)
                throw new InvalidDataException(UiText.TheSubtitleFileExceedsTheAllowedSize);
            bytes.Write(buffer, 0, count);
        }
        await SubtitleFileCache.WriteAsync(path, bytes.ToArray(), cancellationToken);
    }

    private async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(
        SubtitleMediaIdentity identity, string language, string apiKey, string? hash, CancellationToken cancellationToken)
    {
        var parameters = new SortedDictionary<string, string>
        {
            ["languages"] = language,
            ["order_by"] = "download_count",
            ["order_direction"] = "desc",
            ["type"] = identity.IsEpisode ? "episode" : "movie"
        };
        if (hash is not null) parameters["moviehash"] = hash;
        else
        {
            parameters["query"] = identity.Title;
            if (identity.Year is { } year) parameters["year"] = year.ToString(CultureInfo.InvariantCulture);
        }
        if (identity.IsEpisode)
        {
            parameters["season_number"] = identity.Season!.Value.ToString(CultureInfo.InvariantCulture);
            parameters["episode_number"] = identity.Episode!.Value.ToString(CultureInfo.InvariantCulture);
        }
        var query = string.Join('&', parameters.Select(pair => $"{pair.Key}={WebUtility.UrlEncode(pair.Value)}"));
        using var response = await SendApiRequestAsync(HttpMethod.Get, $"subtitles?{query}", apiKey, null, cancellationToken);
        await EnsureSuccessAsync(response);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return SelectCandidates(json.RootElement, identity, language, hash is not null);
    }

    private async Task<HttpResponseMessage> GetDownloadResponseAsync(Uri link, CancellationToken cancellationToken)
    {
        const int maximumRedirects = 5;
        for (var redirect = 0; redirect <= maximumRedirects; redirect++)
        {
            if (link.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException(UiText.SubtitleDownloadURLsMustUseHTTPS);
            var response = await httpClient.GetAsync(link, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new InvalidDataException(UiText.TheSubtitleDownloadRedirectIsInvalid);
            link = location.IsAbsoluteUri ? location : new Uri(link, location);
        }
        throw new InvalidDataException(UiText.TheSubtitleDownloadURLReturnedTooManyRedirects);
    }

    public static IReadOnlyList<SubtitleCandidate> SelectCandidates(
        JsonElement root, SubtitleMediaIdentity identity, string language, bool requireHash)
    {
        var candidates = new List<SubtitleCandidate>();
        if (root.ValueKind != JsonValueKind.Object) return candidates;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return candidates;
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("attributes", out var attributes)
                || attributes.ValueKind != JsonValueKind.Object) continue;
            var hashMatch = attributes.TryGetProperty("moviehash_match", out var matched) && matched.ValueKind == JsonValueKind.True;
            if (requireHash && !hashMatch || !string.Equals(Text(attributes, "language"), language, StringComparison.OrdinalIgnoreCase)) continue;
            if (!attributes.TryGetProperty("feature_details", out var feature) || feature.ValueKind != JsonValueKind.Object) continue;
            if (identity.IsEpisode)
            {
                if (Text(feature, "feature_type") != "Episode"
                    || Number(feature, "season_number") != identity.Season
                    || Number(feature, "episode_number") != identity.Episode) continue;
            }
            else if (Text(feature, "feature_type") != "Movie") continue;
            if (!hashMatch)
            {
                var title = Text(feature, identity.IsEpisode ? "parent_title" : "title");
                if (SubtitleMediaIdentity.NormalizeTitle(title) != SubtitleMediaIdentity.NormalizeTitle(identity.Title)) continue;
                if (identity.Year.HasValue && Number(feature, "year") != identity.Year) continue;
            }
            if (!attributes.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) continue;
            // Multi-file packages need user choice; selecting the first file could load the wrong disc/episode.
            if (files.GetArrayLength() != 1) continue;
            var fileId = Number(files[0], "file_id");
            if (fileId <= 0) continue;
            candidates.Add(new(fileId, Text(attributes, "release"), hashMatch, Number(attributes, "download_count"), Text(attributes, "language")));
        }
        return candidates.OrderByDescending(candidate => candidate.HashMatch)
            .ThenByDescending(candidate => candidate.Downloads).ThenBy(candidate => candidate.FileId).ToArray();
    }

    private async Task<HttpResponseMessage> SendApiRequestAsync(
        HttpMethod method, string endpointOrQuery, string apiKey, HttpContent? content, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(char.IsControl))
            throw new ArgumentException(UiText.CheckYourOpenSubtitlesAPIKey);

        var currentUri = new Uri(ApiBase, endpointOrQuery);
        const int maxRedirects = 5;

        for (var redirect = 0; redirect <= maxRedirects; redirect++)
        {
            using var request = new HttpRequestMessage(method, currentUri);
            request.Headers.Add("Api-Key", apiKey);
            request.Headers.UserAgent.ParseAdd("Pantheon v1.0");
            request.Headers.Accept.ParseAdd("application/json");
            if (content is not null && redirect == 0)
            {
                request.Content = content;
            }

            var response = await httpClient.SendAsync(request, cancellationToken);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
            {
                throw new InvalidDataException(UiText.TheSubtitleDownloadRedirectIsInvalid);
            }

            currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
            if (currentUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidDataException(UiText.SubtitleDownloadURLsMustUseHTTPS);
            }
        }

        throw new InvalidDataException(UiText.TheSubtitleDownloadURLReturnedTooManyRedirects);
    }

    private static Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return Task.CompletedTask;
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => UiText.OpenSubtitlesAccessDenied,
            HttpStatusCode.TooManyRequests or HttpStatusCode.NotAcceptable => UiText.OpenSubtitlesQuotaReached,
            _ => UiText.Format(UiText.OpenSubtitlesRequestFailedFormat, (int)response.StatusCode)
        };
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private static string Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static int Number(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
}
