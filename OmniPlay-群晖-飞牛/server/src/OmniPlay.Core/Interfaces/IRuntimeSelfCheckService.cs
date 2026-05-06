using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface IRuntimeSelfCheckService
{
    Task<RuntimeSelfCheckSnapshot> CheckAsync(CancellationToken cancellationToken = default);
}
