using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface IDoubanMetadataClient
{
    DoubanMetadata CreateSubjectPlaceholder(
        string libraryItemId,
        string subject,
        string fallbackTitle,
        string? fallbackYear = null);

    Task<DoubanMetadata> FetchSubjectAsync(
        string libraryItemId,
        string subject,
        string fallbackTitle,
        string? fallbackYear = null,
        CancellationToken cancellationToken = default);
}
