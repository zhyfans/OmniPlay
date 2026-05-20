using System.Text.Json;
using OmniPlay.Core.Models;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.FileSystem;
using Xunit;

namespace OmniPlay.Tests;

public sealed class MediaSourceRepositoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "omniplay-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task UpdateRenamesAndTogglesSource()
    {
        var mediaRoot = Directory.CreateDirectory(Path.Combine(root, "media")).FullName;
        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();
        var repository = new MediaSourceRepository(database);
        var source = await repository.AddLocalAsync("旧名称", mediaRoot);

        var updated = await repository.UpdateAsync(
            source.Id,
            new UpdateMediaSourceRequest("新名称", IsEnabled: false));

        Assert.NotNull(updated);
        Assert.Equal("新名称", updated.Name);
        Assert.False(updated.IsEnabled);
    }

    [Fact]
    public async Task RemoveHidesSourceFromList()
    {
        var mediaRoot = Directory.CreateDirectory(Path.Combine(root, "media")).FullName;
        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();
        var repository = new MediaSourceRepository(database);
        var source = await repository.AddLocalAsync("媒体", mediaRoot);

        var removed = await repository.RemoveAsync(source.Id);
        var sources = await repository.GetAllAsync();

        Assert.True(removed);
        Assert.Empty(sources);
    }

    [Fact]
    public async Task AddLocalReEnablesPreviouslyRemovedSource()
    {
        var mediaRoot = Directory.CreateDirectory(Path.Combine(root, "media")).FullName;
        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();
        var repository = new MediaSourceRepository(database);
        var source = await repository.AddLocalAsync("媒体", mediaRoot);
        await repository.RemoveAsync(source.Id);

        var restored = await repository.AddLocalAsync("恢复", mediaRoot);
        var sources = await repository.GetAllAsync();

        Assert.Equal(source.Id, restored.Id);
        Assert.True(restored.IsEnabled);
        Assert.Equal("恢复", restored.Name);
        Assert.Single(sources);
    }

    [Fact]
    public async Task AddWebDavPersistsSourceAndCredential()
    {
        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();
        var repository = new MediaSourceRepository(database);

        var source = await repository.AddWebDavAsync(
            "远程媒体",
            "https://example.com/dav/?ignored=1#frag",
            " user ",
            "secret");

        Assert.Equal("远程媒体", source.Name);
        Assert.Equal("webdav", source.Kind);
        Assert.Equal("https://example.com/dav", source.BaseUrl);
        Assert.True(source.IsEnabled);

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.username, c.secret_json, s.auth_reference
            FROM media_source_credentials c
            INNER JOIN media_sources s ON s.id = c.source_id
            WHERE c.source_id = $sourceId;
            """;
        command.Parameters.AddWithValue("$sourceId", source.Id);
        using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal("user", reader.GetString(0));
        using var secret = JsonDocument.Parse(reader.GetString(1));
        Assert.Equal("secret", secret.RootElement.GetProperty("password").GetString());
        Assert.False(reader.IsDBNull(2));
    }

    [Fact]
    public async Task AddWebDavReEnablesPreviouslyRemovedSourceAndUpdatesCredential()
    {
        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();
        var repository = new MediaSourceRepository(database);
        var source = await repository.AddWebDavAsync("旧 WebDAV", "https://example.com/dav/", "old", "old-secret");
        await repository.RemoveAsync(source.Id);

        var restored = await repository.AddWebDavAsync("新 WebDAV", "https://example.com/dav", "new", "new-secret");
        var sources = await repository.GetAllAsync();

        Assert.Equal(source.Id, restored.Id);
        Assert.Equal("新 WebDAV", restored.Name);
        Assert.True(restored.IsEnabled);
        Assert.Single(sources);

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT username, secret_json
            FROM media_source_credentials
            WHERE source_id = $sourceId;
            """;
        command.Parameters.AddWithValue("$sourceId", source.Id);
        using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal("new", reader.GetString(0));
        using var secret = JsonDocument.Parse(reader.GetString(1));
        Assert.Equal("new-secret", secret.RootElement.GetProperty("password").GetString());
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task AddWebDavRejectsInvalidUrl()
    {
        var database = new SqliteDatabase(new StoragePaths(Path.Combine(root, "app")));
        database.EnsureInitialized();
        var repository = new MediaSourceRepository(database);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.AddWebDavAsync("坏地址", "ftp://example.com/dav", null, null));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
