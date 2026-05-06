using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.FileSystem;
using Xunit;

namespace OmniPlay.Tests;

public sealed class SqliteDatabaseTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "omniplay-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void EnsureInitializedCreatesDatabase()
    {
        var database = new SqliteDatabase(new StoragePaths(root));

        database.EnsureInitialized();
        var status = database.GetStatus();

        Assert.True(status.Exists);
        Assert.True(status.SizeBytes > 0);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
