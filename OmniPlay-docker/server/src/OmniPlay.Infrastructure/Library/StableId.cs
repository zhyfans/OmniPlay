using System.Security.Cryptography;
using System.Text;

namespace OmniPlay.Infrastructure.Library;

internal static class StableId
{
    public static string Create(string prefix, params string[] parts)
    {
        var input = string.Join('\u001f', parts.Select(static part => part.Trim().ToLowerInvariant()));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return $"{prefix}_{Convert.ToHexString(bytes)[..24].ToLowerInvariant()}";
    }
}

