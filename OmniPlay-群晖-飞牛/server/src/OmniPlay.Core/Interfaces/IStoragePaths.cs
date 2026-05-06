namespace OmniPlay.Core.Interfaces;

public interface IStoragePaths
{
    string RootDirectory { get; }
    string DataDirectory { get; }
    string CacheDirectory { get; }
    string SettingsDirectory { get; }
    string PostersDirectory { get; }
    string ThumbnailsDirectory { get; }
    string TranscodeDirectory { get; }
    string LogsDirectory { get; }

    void EnsureCreated();
}

