using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface IBackgroundTaskCenter
{
    BackgroundTaskSnapshot GetSnapshot();

    bool TryStartExclusive(
        string kind,
        string title,
        Func<string, IProgress<BackgroundTaskProgress>, CancellationToken, Task<string?>> executeAsync,
        Action<DateTimeOffset>? onAccepted,
        out BackgroundTaskStatus status);

    bool TryCancel(string taskId, out BackgroundTaskStatus status);

    bool TryCancelKind(string kind, out BackgroundTaskStatus status);
}
