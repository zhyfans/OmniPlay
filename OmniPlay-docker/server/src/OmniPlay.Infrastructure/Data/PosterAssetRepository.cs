using Microsoft.Data.Sqlite;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Infrastructure.Data;

public sealed class PosterAssetRepository : IPosterAssetRepository
{
    private readonly SqliteDatabase database;

    public PosterAssetRepository(SqliteDatabase database)
    {
        this.database = database;
    }

    public async Task<PosterAsset?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, remote_path, local_path, width, height, created_at
            FROM poster_assets
            WHERE id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PosterAsset(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            DateTimeOffset.Parse(reader.GetString(5)));
    }
}

