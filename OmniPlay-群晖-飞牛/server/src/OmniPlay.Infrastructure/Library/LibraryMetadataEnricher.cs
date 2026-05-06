using Microsoft.Data.Sqlite;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Infrastructure.Data;

namespace OmniPlay.Infrastructure.Library;

public sealed class LibraryMetadataEnricher : ILibraryMetadataEnricher
{
    private readonly SqliteDatabase database;
    private readonly IAppSettingsRepository settingsRepository;
    private readonly ITmdbMetadataClient tmdbClient;

    public LibraryMetadataEnricher(
        SqliteDatabase database,
        IAppSettingsRepository settingsRepository,
        ITmdbMetadataClient tmdbClient)
    {
        this.database = database;
        this.settingsRepository = settingsRepository;
        this.tmdbClient = tmdbClient;
    }

    public async Task<LibraryMetadataEnrichmentSummary> EnrichMissingAsync(CancellationToken cancellationToken = default)
    {
        return await EnrichMissingAsync(null, cancellationToken);
    }

    public async Task<LibraryMetadataEnrichmentSummary> EnrichMissingAsync(
        IProgress<LibraryMetadataEnrichmentProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var candidates = await LoadCandidatesAsync(libraryItemId: null, cancellationToken);
        return await EnrichAsync(candidates, progress, cancellationToken);
    }

    public async Task<LibraryMetadataEnrichmentSummary> EnrichItemAsync(
        string libraryItemId,
        CancellationToken cancellationToken = default)
    {
        return await EnrichItemAsync(libraryItemId, null, cancellationToken);
    }

    public async Task<LibraryMetadataEnrichmentSummary> EnrichItemAsync(
        string libraryItemId,
        IProgress<LibraryMetadataEnrichmentProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var candidates = await LoadCandidatesAsync(libraryItemId, cancellationToken);
        return await EnrichAsync(candidates, progress, cancellationToken);
    }

    private async Task<LibraryMetadataEnrichmentSummary> EnrichAsync(
        IReadOnlyList<LibraryMetadataCandidate> candidates,
        IProgress<LibraryMetadataEnrichmentProgress>? progress,
        CancellationToken cancellationToken)
    {
        var settings = (await settingsRepository.GetAsync(cancellationToken)).Tmdb;
        if (!settings.EnableMetadataEnrichment && !settings.EnablePosterDownloads)
        {
            ReportProgress(progress, "disabled", candidates.Count, 0, 0, 0, 0, null);
            return new LibraryMetadataEnrichmentSummary(Diagnostics: ["TMDB 刮削已关闭。"]);
        }

        var matched = 0;
        var updated = 0;
        var downloadedPosters = 0;
        var processed = 0;
        List<string> diagnostics = [];

        ReportProgress(progress, "starting", candidates.Count, processed, matched, updated, downloadedPosters, null);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(
                progress,
                candidate.TmdbId.HasValue ? "fetching-details" : "searching",
                candidates.Count,
                processed,
                matched,
                updated,
                downloadedPosters,
                candidate);

            var match = await FindMatchAsync(candidate, settings, cancellationToken);
            if (match is null)
            {
                diagnostics.Add(candidate.TmdbId.HasValue
                    ? $"TMDB 详情刷新失败：{candidate.Title} ({candidate.TmdbId})"
                    : $"未匹配：{candidate.Title}");
                processed++;
                ReportProgress(
                    progress,
                    candidate.TmdbId.HasValue ? "fetching-details" : "searching",
                    candidates.Count,
                    processed,
                    matched,
                    updated,
                    downloadedPosters,
                    candidate);
                continue;
            }

            matched++;
            if (string.Equals(candidate.ItemKind, "tv", StringComparison.OrdinalIgnoreCase))
            {
                ReportProgress(progress, "fetching-episodes", candidates.Count, processed, matched, updated, downloadedPosters, candidate);
                downloadedPosters += await RefreshTvEpisodeMetadataAsync(candidate.Id, match.Id, settings, cancellationToken);
            }

            ReportProgress(progress, "downloading-poster", candidates.Count, processed, matched, updated, downloadedPosters, candidate);
            var posterLocalPath = settings.EnablePosterDownloads && !string.IsNullOrWhiteSpace(match.PosterPath)
                ? await tmdbClient.DownloadPosterAsync(match.PosterPath, match.MediaType, match.Id, cancellationToken)
                : null;
            if (!string.IsNullOrWhiteSpace(posterLocalPath))
            {
                downloadedPosters++;
            }

            ReportProgress(progress, "updating", candidates.Count, processed, matched, updated, downloadedPosters, candidate);
            await UpdateLibraryItemAsync(candidate, match, posterLocalPath, cancellationToken);
            updated++;
            processed++;
            ReportProgress(progress, "updating", candidates.Count, processed, matched, updated, downloadedPosters, candidate);
        }

        return new LibraryMetadataEnrichmentSummary(
            candidates.Count,
            matched,
            updated,
            downloadedPosters,
            diagnostics);
    }

    private async Task<TmdbMetadataMatch?> FindMatchAsync(
        LibraryMetadataCandidate candidate,
        TmdbSettings settings,
        CancellationToken cancellationToken)
    {
        if (candidate.TmdbId.HasValue)
        {
            return await tmdbClient.GetDetailsAsync(
                candidate.ItemKind,
                candidate.TmdbId.Value,
                settings,
                cancellationToken);
        }

        var year = candidate.ReleaseDate?.Length >= 4 ? candidate.ReleaseDate[..4] : null;
        return await tmdbClient.SearchAsync(
            candidate.ItemKind,
            candidate.Title,
            year,
            settings,
            cancellationToken);
    }

    private async Task<IReadOnlyList<LibraryMetadataCandidate>> LoadCandidatesAsync(
        string? libraryItemId,
        CancellationToken cancellationToken)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT li.id,
                   li.item_kind,
                   li.title,
                   li.release_date,
                   li.overview,
                   li.poster_asset_id,
                   li.vote_average,
                   CASE
                       WHEN li.item_kind = 'tv' THEN tv.tmdb_id
                       ELSE m.tmdb_id
                   END AS tmdb_id
            FROM library_items li
            LEFT JOIN movies m ON m.library_item_id = li.id
            LEFT JOIN tv_shows tv ON tv.library_item_id = li.id
            WHERE li.is_locked = 0
              AND ($id IS NULL OR li.id = $id)
            ORDER BY li.updated_at ASC;
            """;
        command.Parameters.AddWithValue("$id", string.IsNullOrWhiteSpace(libraryItemId) ? DBNull.Value : libraryItemId);

        List<LibraryMetadataCandidate> candidates = [];
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new LibraryMetadataCandidate(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetDouble(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7)));
        }

        return candidates;
    }

    private async Task UpdateLibraryItemAsync(
        LibraryMetadataCandidate candidate,
        TmdbMetadataMatch match,
        string? posterLocalPath,
        CancellationToken cancellationToken)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");
        var posterAssetId = candidate.PosterAssetId;

        if (!string.IsNullOrWhiteSpace(posterLocalPath))
        {
            posterAssetId = StableId.Create("poster", match.MediaType, match.Id.ToString(), match.PosterPath ?? posterLocalPath);
            using var poster = connection.CreateCommand();
            poster.Transaction = transaction;
            poster.CommandText = """
                INSERT INTO poster_assets (id, remote_path, local_path, width, height, created_at)
                VALUES ($id, $remotePath, $localPath, NULL, NULL, $createdAt)
                ON CONFLICT(id) DO UPDATE SET
                    remote_path = excluded.remote_path,
                    local_path = excluded.local_path;
                """;
            poster.Parameters.AddWithValue("$id", posterAssetId);
            poster.Parameters.AddWithValue("$remotePath", match.PosterPath ?? (object)DBNull.Value);
            poster.Parameters.AddWithValue("$localPath", posterLocalPath);
            poster.Parameters.AddWithValue("$createdAt", now);
            await poster.ExecuteNonQueryAsync(cancellationToken);
        }

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE library_items
            SET title = $title,
                sort_title = $sortTitle,
                release_date = COALESCE($releaseDate, release_date),
                overview = COALESCE($overview, overview),
                poster_asset_id = COALESCE($posterAssetId, poster_asset_id),
                vote_average = COALESCE($voteAverage, vote_average),
                updated_at = $updatedAt
            WHERE id = $id AND is_locked = 0;
            """;
        update.Parameters.AddWithValue("$id", candidate.Id);
        update.Parameters.AddWithValue("$title", match.Title);
        update.Parameters.AddWithValue("$sortTitle", match.Title.Trim().ToLowerInvariant());
        update.Parameters.AddWithValue("$releaseDate", string.IsNullOrWhiteSpace(match.ReleaseDate) ? DBNull.Value : match.ReleaseDate);
        update.Parameters.AddWithValue("$overview", string.IsNullOrWhiteSpace(match.Overview) ? DBNull.Value : match.Overview);
        update.Parameters.AddWithValue("$posterAssetId", string.IsNullOrWhiteSpace(posterAssetId) ? DBNull.Value : posterAssetId);
        update.Parameters.AddWithValue("$voteAverage", match.VoteAverage ?? (object)DBNull.Value);
        update.Parameters.AddWithValue("$updatedAt", now);
        var updated = await update.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (updated)
        {
            await UpdateTmdbIdAsync(connection, transaction, candidate.Id, candidate.ItemKind, match.Id, cancellationToken);
        }

        transaction.Commit();
    }

    private async Task<int> RefreshTvEpisodeMetadataAsync(
        string libraryItemId,
        int tvTmdbId,
        TmdbSettings settings,
        CancellationToken cancellationToken)
    {
        var localSeasons = await LoadLocalTvSeasonsAsync(libraryItemId, cancellationToken);
        if (localSeasons.Count == 0)
        {
            return 0;
        }

        var downloadedImages = 0;
        foreach (var season in localSeasons)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remoteSeason = await tmdbClient.GetSeasonAsync(
                tvTmdbId,
                season.SeasonNumber,
                settings,
                cancellationToken);
            if (remoteSeason is null)
            {
                continue;
            }

            string? seasonPosterLocalPath = null;
            if (settings.EnablePosterDownloads && !string.IsNullOrWhiteSpace(remoteSeason.PosterPath))
            {
                seasonPosterLocalPath = await tmdbClient.DownloadPosterAsync(
                    remoteSeason.PosterPath,
                    "tv",
                    tvTmdbId,
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(seasonPosterLocalPath))
                {
                    downloadedImages++;
                }
            }

            var episodeStillLocalPaths = new Dictionary<int, string>(capacity: remoteSeason.Episodes.Count);
            if (settings.EnablePosterDownloads)
            {
                foreach (var episode in remoteSeason.Episodes)
                {
                    if (!season.Episodes.ContainsKey(episode.EpisodeNumber) || string.IsNullOrWhiteSpace(episode.StillPath))
                    {
                        continue;
                    }

                    var stillLocalPath = await tmdbClient.DownloadStillAsync(
                        episode.StillPath,
                        tvTmdbId,
                        season.SeasonNumber,
                        episode.EpisodeNumber,
                        cancellationToken);
                    if (!string.IsNullOrWhiteSpace(stillLocalPath))
                    {
                        episodeStillLocalPaths[episode.EpisodeNumber] = stillLocalPath;
                        downloadedImages++;
                    }
                }
            }

            await UpdateSeasonEpisodeMetadataAsync(
                season,
                remoteSeason,
                tvTmdbId,
                seasonPosterLocalPath,
                episodeStillLocalPaths,
                cancellationToken);
        }

        return downloadedImages;
    }

    private async Task<IReadOnlyList<LocalSeason>> LoadLocalTvSeasonsAsync(
        string libraryItemId,
        CancellationToken cancellationToken)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id,
                   s.season_number,
                   e.id,
                   e.episode_number,
                   vf.id
            FROM tv_shows tv
            JOIN seasons s ON s.tv_show_id = tv.id
            LEFT JOIN episodes e ON e.season_id = s.id
            LEFT JOIN video_files vf ON vf.episode_id = e.id AND vf.missing_at IS NULL
            WHERE tv.library_item_id = $libraryItemId
            ORDER BY s.season_number ASC, e.episode_number ASC;
            """;
        command.Parameters.AddWithValue("$libraryItemId", libraryItemId);

        Dictionary<string, LocalSeason> seasons = [];
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var seasonId = reader.GetString(0);
            if (!seasons.TryGetValue(seasonId, out var season))
            {
                season = new LocalSeason(seasonId, reader.GetInt32(1));
                seasons[seasonId] = season;
            }

            if (!reader.IsDBNull(2))
            {
                var episodeNumber = reader.GetInt32(3);
                season.Episodes.TryAdd(
                    episodeNumber,
                    new LocalEpisode(
                        reader.GetString(2),
                        episodeNumber,
                        reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        return seasons.Values.OrderBy(static season => season.SeasonNumber).ToArray();
    }

    private async Task UpdateSeasonEpisodeMetadataAsync(
        LocalSeason localSeason,
        TmdbSeasonDetail remoteSeason,
        int tvTmdbId,
        string? seasonPosterLocalPath,
        IReadOnlyDictionary<int, string> episodeStillLocalPaths,
        CancellationToken cancellationToken)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");
        string? seasonPosterAssetId = null;
        if (!string.IsNullOrWhiteSpace(seasonPosterLocalPath))
        {
            seasonPosterAssetId = StableId.Create(
                "poster",
                "tv-season",
                tvTmdbId.ToString(),
                localSeason.SeasonNumber.ToString(),
                remoteSeason.PosterPath ?? seasonPosterLocalPath);
            using var posterCommand = connection.CreateCommand();
            posterCommand.Transaction = transaction;
            posterCommand.CommandText = """
                INSERT INTO poster_assets (id, remote_path, local_path, width, height, created_at)
                VALUES ($id, $remotePath, $localPath, NULL, NULL, $createdAt)
                ON CONFLICT(id) DO UPDATE SET
                    remote_path = excluded.remote_path,
                    local_path = excluded.local_path;
                """;
            posterCommand.Parameters.AddWithValue("$id", seasonPosterAssetId);
            posterCommand.Parameters.AddWithValue("$remotePath", NullIfWhiteSpace(remoteSeason.PosterPath));
            posterCommand.Parameters.AddWithValue("$localPath", seasonPosterLocalPath);
            posterCommand.Parameters.AddWithValue("$createdAt", now);
            await posterCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        using var seasonCommand = connection.CreateCommand();
        seasonCommand.Transaction = transaction;
        seasonCommand.CommandText = """
            UPDATE seasons
            SET title = COALESCE($title, title),
                poster_asset_id = COALESCE($posterAssetId, poster_asset_id)
            WHERE id = $id;
            """;
        seasonCommand.Parameters.AddWithValue("$id", localSeason.Id);
        seasonCommand.Parameters.AddWithValue("$title", NullIfWhiteSpace(remoteSeason.Title));
        seasonCommand.Parameters.AddWithValue("$posterAssetId", NullIfWhiteSpace(seasonPosterAssetId));
        await seasonCommand.ExecuteNonQueryAsync(cancellationToken);

        foreach (var episode in remoteSeason.Episodes)
        {
            if (!localSeason.Episodes.TryGetValue(episode.EpisodeNumber, out var localEpisode))
            {
                continue;
            }

            string? stillAssetId = null;
            if (episodeStillLocalPaths.TryGetValue(episode.EpisodeNumber, out var stillLocalPath))
            {
                stillAssetId = StableId.Create(
                    "thumbnail",
                    "tv",
                    tvTmdbId.ToString(),
                    localSeason.SeasonNumber.ToString(),
                    episode.EpisodeNumber.ToString(),
                    episode.StillPath ?? stillLocalPath);
                using var thumbnailCommand = connection.CreateCommand();
                thumbnailCommand.Transaction = transaction;
                thumbnailCommand.CommandText = """
                    INSERT INTO thumbnail_assets (id, video_file_id, local_path, width, height, created_at)
                    VALUES ($id, $videoFileId, $localPath, NULL, NULL, $createdAt)
                    ON CONFLICT(id) DO UPDATE SET
                        video_file_id = excluded.video_file_id,
                        local_path = excluded.local_path;
                    """;
                thumbnailCommand.Parameters.AddWithValue("$id", stillAssetId);
                thumbnailCommand.Parameters.AddWithValue("$videoFileId", NullIfWhiteSpace(localEpisode.VideoFileId));
                thumbnailCommand.Parameters.AddWithValue("$localPath", stillLocalPath);
                thumbnailCommand.Parameters.AddWithValue("$createdAt", now);
                await thumbnailCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            using var episodeCommand = connection.CreateCommand();
            episodeCommand.Transaction = transaction;
            episodeCommand.CommandText = """
                UPDATE episodes
                SET title = COALESCE($title, title),
                    overview = COALESCE($overview, overview),
                    still_asset_id = COALESCE($stillAssetId, still_asset_id),
                    air_date = COALESCE($airDate, air_date)
                WHERE season_id = $seasonId
                  AND episode_number = $episodeNumber;
                """;
            episodeCommand.Parameters.AddWithValue("$seasonId", localSeason.Id);
            episodeCommand.Parameters.AddWithValue("$episodeNumber", episode.EpisodeNumber);
            episodeCommand.Parameters.AddWithValue("$title", NullIfWhiteSpace(episode.Title));
            episodeCommand.Parameters.AddWithValue("$overview", NullIfWhiteSpace(episode.Overview));
            episodeCommand.Parameters.AddWithValue("$stillAssetId", NullIfWhiteSpace(stillAssetId));
            episodeCommand.Parameters.AddWithValue("$airDate", NullIfWhiteSpace(episode.AirDate));
            await episodeCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
    }

    private static async Task UpdateTmdbIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string libraryItemId,
        string itemKind,
        int tmdbId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var isTv = string.Equals(itemKind, "tv", StringComparison.OrdinalIgnoreCase);
        command.CommandText = isTv
            ? """
                INSERT INTO tv_shows (id, library_item_id, tmdb_id, original_name, first_air_date)
                VALUES ($id, $libraryItemId, $tmdbId, NULL, NULL)
                ON CONFLICT(id) DO UPDATE SET
                    library_item_id = excluded.library_item_id,
                    tmdb_id = excluded.tmdb_id;
                """
            : """
                INSERT INTO movies (id, library_item_id, tmdb_id, original_title, runtime_seconds)
                VALUES ($id, $libraryItemId, $tmdbId, NULL, NULL)
                ON CONFLICT(id) DO UPDATE SET
                    library_item_id = excluded.library_item_id,
                    tmdb_id = excluded.tmdb_id;
                """;
        command.Parameters.AddWithValue("$id", StableId.Create(isTv ? "show" : "movie", libraryItemId));
        command.Parameters.AddWithValue("$libraryItemId", libraryItemId);
        command.Parameters.AddWithValue("$tmdbId", tmdbId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static object NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    }

    private sealed record LibraryMetadataCandidate(
        string Id,
        string ItemKind,
        string Title,
        string? ReleaseDate,
        string? Overview,
        string? PosterAssetId,
        double? VoteAverage,
        int? TmdbId);

    private sealed record LocalSeason(string Id, int SeasonNumber)
    {
        public Dictionary<int, LocalEpisode> Episodes { get; } = [];
    }

    private sealed record LocalEpisode(string Id, int EpisodeNumber, string? VideoFileId);

    private static void ReportProgress(
        IProgress<LibraryMetadataEnrichmentProgress>? progress,
        string phase,
        int targetItemCount,
        int processedItemCount,
        int matchedItemCount,
        int updatedItemCount,
        int downloadedPosterCount,
        LibraryMetadataCandidate? candidate)
    {
        progress?.Report(new LibraryMetadataEnrichmentProgress(
            phase,
            targetItemCount,
            processedItemCount,
            matchedItemCount,
            updatedItemCount,
            downloadedPosterCount,
            candidate?.Id,
            candidate?.Title,
            DateTimeOffset.UtcNow));
    }
}
