using System.Security.Cryptography;
using System.Text.Json;

namespace Jellyfin.Plugin.MetaTagger;

internal static class MetaTaggerPreviewChangeChecksum
{
    public static string Compute(IReadOnlyCollection<MetaTaggerPreviewChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(changes)));
    }
}
