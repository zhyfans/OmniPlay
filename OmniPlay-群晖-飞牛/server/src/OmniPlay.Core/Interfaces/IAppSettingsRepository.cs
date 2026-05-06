using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface IAppSettingsRepository
{
    Task<AppSettingsSnapshot> GetAsync(CancellationToken cancellationToken = default);

    Task<AppSettingsSnapshot> UpdateAsync(
        AppSettingsUpdateRequest request,
        CancellationToken cancellationToken = default);
}

