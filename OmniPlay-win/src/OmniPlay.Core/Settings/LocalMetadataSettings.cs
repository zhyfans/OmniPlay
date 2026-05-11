namespace OmniPlay.Core.Settings;

public sealed record LocalMetadataSettings
{
    public bool EnableLocalMetadataImport { get; init; }

    public bool EnableLocalMetadataExport { get; init; }
}
