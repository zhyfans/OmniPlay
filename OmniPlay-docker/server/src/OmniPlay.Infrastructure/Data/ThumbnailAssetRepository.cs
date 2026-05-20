using Microsoft.Data.Sqlite;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Infrastructure.Data;

public sealed class ThumbnailAssetRepository : IThumbnailAssetRepository
{
    private readonly SqliteDatabase database;

    public ThumbnailAssetRepository(SqliteDatabase database)
    {
        this.database = database;
    }

    public async Task<ThumbnailAsset?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, video_file_id, local_path, width, height, created_at
            FROM thumbnail_assets
            WHERE id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ThumbnailAsset(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            DateTimeOffset.Parse(reader.GetString(5)));
    }
}
