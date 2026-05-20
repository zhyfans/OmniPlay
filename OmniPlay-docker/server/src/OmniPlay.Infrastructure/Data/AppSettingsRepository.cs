using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Infrastructure.Data;

public sealed class AppSettingsRepository : IAppSettingsRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteDatabase database;

    public AppSettingsRepository(SqliteDatabase database)
    {
        this.database = database;
    }

    public async Task<AppSettingsSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        var tmdb = await ReadJsonAsync<TmdbSettings>(connection, "tmdb", cancellationToken) ?? new TmdbSettings();
        var cache = await ReadJsonAsync<CacheSettings>(connection, "cache", cancellationToken) ?? new CacheSettings();
        var playback = await ReadJsonAsync<PlaybackSettings>(connection, "playback", cancellationToken)
                       ?? new PlaybackSettings();
        var proxy = await ReadJsonAsync<ProxySettings>(connection, "proxy", cancellationToken) ?? new ProxySettings();
        var automation = await ReadJsonAsync<AutomationSettings>(connection, "automation", cancellationToken)
                         ?? new AutomationSettings();
        return BuildSnapshot(Normalize(tmdb), Normalize(cache), Normalize(playback), Normalize(proxy), Normalize(automation));
    }

    public async Task<AppSettingsSnapshot> UpdateAsync(
        AppSettingsUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        var current = await ReadJsonAsync<TmdbSettings>(connection, "tmdb", cancellationToken) ?? new TmdbSettings();
        var currentCache = await ReadJsonAsync<CacheSettings>(connection, "cache", cancellationToken) ?? new CacheSettings();
        var currentPlayback = await ReadJsonAsync<PlaybackSettings>(connection, "playback", cancellationToken)
                              ?? new PlaybackSettings();
        var currentProxy = await ReadJsonAsync<ProxySettings>(connection, "proxy", cancellationToken) ?? new ProxySettings();
        var currentAutomation = await ReadJsonAsync<AutomationSettings>(connection, "automation", cancellationToken)
                                ?? new AutomationSettings();
        var tmdb = Normalize(request.Tmdb ?? current);
        var cache = Normalize(request.Cache ?? currentCache);
        var playback = Normalize(request.Playback ?? currentPlayback);
        var proxy = Normalize(request.Proxy ?? currentProxy);
        var automation = Normalize(request.Automation ?? currentAutomation);
        await WriteJsonAsync(connection, "tmdb", tmdb, cancellationToken);
        await WriteJsonAsync(connection, "cache", cache, cancellationToken);
        await WriteJsonAsync(connection, "playback", playback, cancellationToken);
        await WriteJsonAsync(connection, "proxy", proxy, cancellationToken);
        await WriteJsonAsync(connection, "automation", automation, cancellationToken);
        return BuildSnapshot(tmdb, cache, playback, proxy, automation);
    }

    private static AppSettingsSnapshot BuildSnapshot(
        TmdbSettings tmdb,
        CacheSettings cache,
        PlaybackSettings playback,
        ProxySettings proxy,
        AutomationSettings automation)
    {
        return new AppSettingsSnapshot(
            "OmniPlay",
            "phase-2",
            tmdb,
            cache,
            playback,
            proxy,
            automation);
    }

    private static TmdbSettings Normalize(TmdbSettings settings)
    {
        var customApiKey = settings.CustomApiKey.Trim();
        var customAccessToken = NormalizeAccessToken(settings.CustomAccessToken);
        if (string.IsNullOrWhiteSpace(customAccessToken) && LooksLikeAccessToken(customApiKey))
        {
            customAccessToken = NormalizeAccessToken(customApiKey);
            customApiKey = string.Empty;
        }

        return settings with
        {
            EnableMetadataEnrichment = true,
            EnablePosterDownloads = true,
            Language = "zh-CN",
            CustomApiKey = customApiKey,
            CustomAccessToken = customAccessToken
        };
    }

    private static string NormalizeAccessToken(string value)
    {
        var token = value.Trim();
        const string bearerPrefix = "Bearer ";
        return token.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? token[bearerPrefix.Length..].Trim()
            : token;
    }

    private static bool LooksLikeAccessToken(string value)
    {
        var token = NormalizeAccessToken(value);
        return token.StartsWith("eyJ", StringComparison.Ordinal) || token.Length > 80;
    }

    private static PlaybackSettings Normalize(PlaybackSettings settings)
    {
        return settings with
        {
            DirectStream = true,
            HlsRemux = true,
            Transcode = true,
            PlaybackQualityPreference = NormalizePlaybackQualityPreference(settings.PlaybackQualityPreference),
            DefaultAudioLanguage = NormalizeDefaultAudioLanguage(settings.DefaultAudioLanguage),
            DefaultSubtitleLanguage = NormalizeDefaultSubtitleLanguage(settings.DefaultSubtitleLanguage)
        };
    }

    private static string NormalizePlaybackQualityPreference(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "original-priority" => "original-priority",
            "compatibility" => "compatibility",
            _ => "auto"
        };
    }

    private static string NormalizeDefaultAudioLanguage(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "en" => "en",
            "ja" => "ja",
            _ => "smart"
        };
    }

    private static string NormalizeDefaultSubtitleLanguage(string? value)
    {
        return string.Equals(value?.Trim(), "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";
    }

    private static CacheSettings Normalize(CacheSettings settings)
    {
        var scope = string.Equals(settings.ImageCleanupScope, "orphans-only", StringComparison.OrdinalIgnoreCase)
            ? "orphans-only"
            : "orphans-and-untracked";
        return settings with
        {
            HlsRetentionHours = Math.Clamp(settings.HlsRetentionHours, 1, 24 * 30),
            ImageCleanupScope = scope,
            WebDavRetentionHours = settings.WebDavRetentionHours <= 0
                ? 72
                : Math.Clamp(settings.WebDavRetentionHours, 1, 24 * 30),
            WebDavMaxGb = settings.WebDavMaxGb <= 0
                ? 20
                : Math.Clamp(settings.WebDavMaxGb, 1, 1024)
        };
    }

    private static ProxySettings Normalize(ProxySettings settings)
    {
        return settings with
        {
            Url = NormalizeProxyUrl(settings.Url),
            Username = settings.Username.Trim(),
            Password = settings.Password.Trim(),
            BypassList = NormalizeBypassList(settings.BypassList)
        };
    }

    private static AutomationSettings Normalize(AutomationSettings settings)
    {
        return settings with
        {
            ScheduledLibraryRefreshIntervalHours = Math.Clamp(
                settings.ScheduledLibraryRefreshIntervalHours <= 0 ? 24 : settings.ScheduledLibraryRefreshIntervalHours,
                1,
                24 * 30)
        };
    }

    private static string NormalizeProxyUrl(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        var candidate = trimmed.Contains("://", StringComparison.Ordinal)
            ? trimmed
            : $"http://{trimmed}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !IsSupportedProxyScheme(uri.Scheme))
        {
            return trimmed;
        }

        var builder = new UriBuilder(uri.Scheme, uri.Host);
        if (!uri.IsDefaultPort)
        {
            builder.Port = uri.Port;
        }

        return builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private static string NormalizeBypassList(string value)
    {
        return string.Join(
            ',',
            value.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static item => !string.IsNullOrWhiteSpace(item)));
    }

    private static bool IsSupportedProxyScheme(string scheme)
    {
        return string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               || string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
               || string.Equals(scheme, "socks4", StringComparison.OrdinalIgnoreCase)
               || string.Equals(scheme, "socks4a", StringComparison.OrdinalIgnoreCase)
               || string.Equals(scheme, "socks5", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<T?> ReadJsonAsync<T>(
        SqliteConnection connection,
        string key,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value_json FROM app_settings WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string json ? JsonSerializer.Deserialize<T>(json, JsonOptions) : default;
    }

    private static async Task WriteJsonAsync<T>(
        SqliteConnection connection,
        string key,
        T value,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_settings (key, value_json, updated_at)
            VALUES ($key, $valueJson, $updatedAt)
            ON CONFLICT(key) DO UPDATE SET
                value_json = excluded.value_json,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$valueJson", JsonSerializer.Serialize(value, JsonOptions));
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
