using OmniPlay.Core.Models;

namespace OmniPlay.Core.Interfaces;

public interface ICacheUsageService
{
    CacheUsageSummary GetUsage();
}
