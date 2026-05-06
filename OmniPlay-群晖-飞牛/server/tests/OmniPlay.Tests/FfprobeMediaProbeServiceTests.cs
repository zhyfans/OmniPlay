using OmniPlay.Media;
using Xunit;

namespace OmniPlay.Tests;

public sealed class FfprobeMediaProbeServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "omniplay-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ProbeAsyncReturnsNullWhenFfprobeIsUnavailable()
    {
        var mediaPath = Path.Combine(root, "media", "sample.mp4");
        Touch(mediaPath);
        var service = new FfprobeMediaProbeService(Path.Combine(root, "missing-ffprobe"), TimeSpan.FromMilliseconds(50));

        var snapshot = await service.ProbeAsync(mediaPath, CancellationToken.None);

        Assert.Null(snapshot);
    }

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0, 1, 2, 3]);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
