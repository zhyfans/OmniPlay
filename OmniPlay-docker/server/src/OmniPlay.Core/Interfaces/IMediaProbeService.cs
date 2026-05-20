using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface IMediaProbeService
{
    Task<MediaProbeSnapshot?> ProbeAsync(string filePath, CancellationToken cancellationToken);
}
