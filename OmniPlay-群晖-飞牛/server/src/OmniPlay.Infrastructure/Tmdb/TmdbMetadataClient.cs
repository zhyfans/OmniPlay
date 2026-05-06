using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Infrastructure.Tmdb;

public sealed class TmdbMetadataClient : ITmdbMetadataClient
{
    private const string ApiBaseUrl = "https://api.themoviedb.org/3";
    private const string ImageBaseUrl = "https://image.tmdb.org/t/p/w500";
    private const string BuiltInPublicApiKey = "d05a3f7e939f5034054090b376de6f8c";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient httpClient;
    private readonly IStoragePaths storagePaths;

    public TmdbMetadataClient(HttpClient httpClient, IStoragePaths storagePaths)
    {
        this.httpClient = httpClient;
        this.storagePaths = storagePaths;
        if (this.httpClient.Timeout == Timeout.InfiniteTimeSpan)
        {
            this.httpClient.Timeout = TimeSpan.FromSeconds(15);
        }

        if (this.httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            this.httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("OmniPlay.NAS/0.1");
        }
    }

    public async Task<TmdbMetadataMatch?> SearchAsync(
        string mediaType,
        string title,
        string? year,
        TmdbSettings settings,
        CancellationToken cancellationToken = default)
    {
        var candidates = await SearchCandidatesAsync(mediaType, title, year, settings, limit: 1, cancellationToken);
        return candidates.FirstOrDefault();
    }

    public async Task<IReadOnlyList<TmdbMetadataMatch>> SearchCandidatesAsync(
        string mediaType,
        string title,
        string? year,
        TmdbSettings settings,
        int limit = 8,
        CancellationToken cancellationToken = default)
    {
        var credential = ResolveCredential(settings);
        if (credential is null || string.IsNullOrWhiteSpace(title))
        {
            return [];
        }

        var normalizedType = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
        var language = string.IsNullOrWhiteSpace(settings.Language) ? "zh-CN" : settings.Language;
        var query = new Dictionary<string, string?>
        {
            ["query"] = title,
            ["language"] = language,
            ["page"] = "1",
            ["include_adult"] = "false"
        };

        if (!string.IsNullOrWhiteSpace(year))
        {
            query[normalizedType == "tv" ? "first_air_date_year" : "primary_release_year"] = year;
        }

        var relativeUrl = $"/search/{normalizedType}?{BuildQueryString(query, credential)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBaseUrl + relativeUrl);
        if (!string.IsNullOrWhiteSpace(credential.BearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.BearerToken);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<TmdbSearchResponse>(stream, JsonOptions, cancellationToken);
        return payload?.Results?
            .Where(static item => item.Id > 0)
            .OrderByDescending(item => Score(item, title, year, normalizedType))
            .Select(item => ToMatch(item, normalizedType))
            .Take(Math.Clamp(limit, 1, 20))
            .ToArray() ?? [];
    }

    public async Task<string?> DownloadPosterAsync(
        string posterPath,
        string mediaType,
        int tmdbId,
        CancellationToken cancellationToken = default)
    {
        var safeType = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
        return await DownloadImageAsync(
            posterPath,
            storagePaths.PostersDirectory,
            $"{safeType}-{tmdbId}",
            cancellationToken);
    }

    public async Task<string?> DownloadStillAsync(
        string stillPath,
        int tvTmdbId,
        int seasonNumber,
        int episodeNumber,
        CancellationToken cancellationToken = default)
    {
        return await DownloadImageAsync(
            stillPath,
            storagePaths.ThumbnailsDirectory,
            $"tv-{tvTmdbId}-s{seasonNumber:00}e{episodeNumber:00}",
            cancellationToken);
    }

    private async Task<string?> DownloadImageAsync(
        string imagePath,
        string destinationDirectory,
        string filePrefix,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        storagePaths.EnsureCreated();
        var normalizedImagePath = imagePath.StartsWith('/') ? imagePath : "/" + imagePath;
        var extension = Path.GetExtension(normalizedImagePath);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 8)
        {
            extension = ".jpg";
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedImagePath)))[..12].ToLowerInvariant();
        var destination = Path.Combine(destinationDirectory, $"{filePrefix}-{hash}{extension}");
        if (File.Exists(destination) && new FileInfo(destination).Length > 0)
        {
            return destination;
        }

        var url = ImageBaseUrl + normalizedImagePath;
        await using var stream = await httpClient.GetStreamAsync(url, cancellationToken);
        await using var file = File.Create(destination);
        await stream.CopyToAsync(file, cancellationToken);
        return destination;
    }

    public async Task<TmdbMetadataMatch?> GetDetailsAsync(
        string mediaType,
        int tmdbId,
        TmdbSettings settings,
        CancellationToken cancellationToken = default)
    {
        var credential = ResolveCredential(settings);
        if (credential is null || tmdbId <= 0)
        {
            return null;
        }

        var normalizedType = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
        var language = string.IsNullOrWhiteSpace(settings.Language) ? "zh-CN" : settings.Language;
        var relativeUrl = $"/{normalizedType}/{tmdbId}?{BuildQueryString(
            new Dictionary<string, string?> { ["language"] = language },
            credential)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBaseUrl + relativeUrl);
        if (!string.IsNullOrWhiteSpace(credential.BearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.BearerToken);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<TmdbSearchItem>(stream, JsonOptions, cancellationToken);
        return payload is null || payload.Id <= 0 ? null : ToMatch(payload, normalizedType);
    }

    public async Task<TmdbSeasonDetail?> GetSeasonAsync(
        int tvTmdbId,
        int seasonNumber,
        TmdbSettings settings,
        CancellationToken cancellationToken = default)
    {
        var credential = ResolveCredential(settings);
        if (credential is null || tvTmdbId <= 0 || seasonNumber < 0)
        {
            return null;
        }

        var language = string.IsNullOrWhiteSpace(settings.Language) ? "zh-CN" : settings.Language;
        var relativeUrl = $"/tv/{tvTmdbId}/season/{seasonNumber}?{BuildQueryString(
            new Dictionary<string, string?> { ["language"] = language },
            credential)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBaseUrl + relativeUrl);
        if (!string.IsNullOrWhiteSpace(credential.BearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.BearerToken);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<TmdbSeasonResponse>(stream, JsonOptions, cancellationToken);
        if (payload is null)
        {
            return null;
        }

        return new TmdbSeasonDetail(
            payload.SeasonNumber ?? seasonNumber,
            payload.Name,
            payload.Overview,
            payload.AirDate,
            payload.PosterPath,
            payload.Episodes?
                .Where(static episode => episode.EpisodeNumber > 0)
                .Select(static episode => new TmdbEpisodeDetail(
                    episode.EpisodeNumber,
                    episode.Name,
                    episode.Overview,
                    episode.AirDate,
                    episode.StillPath))
                .ToArray() ?? []);
    }

    private static TmdbCredential? ResolveCredential(TmdbSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.CustomAccessToken))
        {
            return new TmdbCredential(null, settings.CustomAccessToken.Trim());
        }

        var envToken = Environment.GetEnvironmentVariable("OMNIPLAY_TMDB_ACCESS_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken))
        {
            return new TmdbCredential(null, envToken.Trim());
        }

        if (!string.IsNullOrWhiteSpace(settings.CustomApiKey))
        {
            return new TmdbCredential(settings.CustomApiKey.Trim(), null);
        }

        var envKey = Environment.GetEnvironmentVariable("OMNIPLAY_TMDB_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            return new TmdbCredential(envKey.Trim(), null);
        }

        return settings.EnableBuiltInPublicSource ? new TmdbCredential(BuiltInPublicApiKey, null) : null;
    }

    private static string BuildQueryString(IReadOnlyDictionary<string, string?> values, TmdbCredential credential)
    {
        var pairs = values
            .Where(static pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}")
            .ToList();

        if (!string.IsNullOrWhiteSpace(credential.ApiKey))
        {
            pairs.Add($"api_key={Uri.EscapeDataString(credential.ApiKey)}");
        }

        return string.Join('&', pairs);
    }

    private static double Score(TmdbSearchItem item, string query, string? year, string mediaType)
    {
        var title = mediaType == "tv" ? item.Name : item.Title;
        var score = 0.0;
        if (!string.IsNullOrWhiteSpace(title) &&
            string.Equals(NormalizeTitle(title), NormalizeTitle(query), StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        var date = mediaType == "tv" ? item.FirstAirDate : item.ReleaseDate;
        if (!string.IsNullOrWhiteSpace(year) && date?.StartsWith(year, StringComparison.Ordinal) == true)
        {
            score += 30;
        }

        score += Math.Min(item.Popularity ?? 0, 100) / 10.0;
        score += (item.VoteAverage ?? 0) / 2.0;
        return score;
    }

    private static TmdbMetadataMatch ToMatch(TmdbSearchItem item, string mediaType)
    {
        return new TmdbMetadataMatch(
            item.Id,
            mediaType,
            mediaType == "tv" ? item.Name ?? "未知剧集" : item.Title ?? "未知影片",
            item.Overview,
            mediaType == "tv" ? item.FirstAirDate : item.ReleaseDate,
            item.PosterPath,
            item.VoteAverage,
            item.Popularity);
    }

    private static string NormalizeTitle(string value)
    {
        return value
            .Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private sealed record TmdbCredential(string? ApiKey, string? BearerToken);

    private sealed record TmdbSearchResponse(
        [property: JsonPropertyName("results")] IReadOnlyList<TmdbSearchItem>? Results);

    private sealed record TmdbSearchItem(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("overview")] string? Overview,
        [property: JsonPropertyName("release_date")] string? ReleaseDate,
        [property: JsonPropertyName("first_air_date")] string? FirstAirDate,
        [property: JsonPropertyName("poster_path")] string? PosterPath,
        [property: JsonPropertyName("vote_average")] double? VoteAverage,
        [property: JsonPropertyName("popularity")] double? Popularity);

    private sealed record TmdbSeasonResponse(
        [property: JsonPropertyName("season_number")] int? SeasonNumber,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("overview")] string? Overview,
        [property: JsonPropertyName("air_date")] string? AirDate,
        [property: JsonPropertyName("poster_path")] string? PosterPath,
        [property: JsonPropertyName("episodes")] IReadOnlyList<TmdbSeasonEpisodeResponse>? Episodes);

    private sealed record TmdbSeasonEpisodeResponse(
        [property: JsonPropertyName("episode_number")] int EpisodeNumber,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("overview")] string? Overview,
        [property: JsonPropertyName("air_date")] string? AirDate,
        [property: JsonPropertyName("still_path")] string? StillPath);
}
