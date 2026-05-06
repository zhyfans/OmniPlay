namespace OmniPlay.Packaging;

public sealed record PackageRuntimeEnvironment(
    string Platform,
    string AppRoot,
    string DataRoot,
    string ListenUrls);

