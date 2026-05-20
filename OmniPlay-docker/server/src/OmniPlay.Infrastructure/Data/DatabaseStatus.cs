namespace OmniPlay.Infrastructure.Data;

public sealed record DatabaseStatus(string Path, bool Exists, long SizeBytes);

