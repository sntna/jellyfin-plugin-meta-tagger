using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.MetaTagger;

public sealed partial class MetaTaggerStateStore
{
    private readonly SemaphoreSlim _baselineGate = new(1, 1);
    private (int Count, string Checksum, string[] ItemIds)? _verifiedBaseline;

    private string BaselinePath => Path.Combine(_directory, "meta-tagger-baseline.json");

    // The configuration records the count and this checksum; the item IDs live beside the
    // ledger. A baseline that cannot reproduce its recorded checksum is not trusted, so a
    // truncated or replaced file can never silently shrink the set of protected items.
    public static string ComputeBaselineChecksum(IReadOnlyCollection<string> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        var builder = new StringBuilder();
        foreach (var itemId in itemIds)
        {
            builder.Append(itemId).Append('\n');
        }

        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    public async Task SaveBaselineAsync(IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        await _baselineGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AtomicWriteAsync(BaselinePath, itemIds, createBackup: false, cancellationToken).ConfigureAwait(false);
            _verifiedBaseline = (itemIds.Count, ComputeBaselineChecksum(itemIds), itemIds.ToArray());
        }
        finally
        {
            _baselineGate.Release();
        }
    }

    public async Task<string[]?> LoadBaselineAsync(int? expectedCount, string? expectedChecksum, CancellationToken cancellationToken)
    {
        if (expectedCount is null || string.IsNullOrEmpty(expectedChecksum))
        {
            return null;
        }

        await _baselineGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The checksum identifies the content, so a matching one needs no reread.
            // Item inspection asks for this set on every request.
            if (_verifiedBaseline is { } cached && cached.Count == expectedCount
                && string.Equals(cached.Checksum, expectedChecksum, StringComparison.Ordinal))
            {
                return cached.ItemIds;
            }

            // A library with no items at capture time records an empty baseline, which the
            // recorded count and checksum account for on their own. Only a baseline that
            // claims item IDs needs the file, and a missing file cannot stand in for them.
            if (expectedCount == 0
                && string.Equals(ComputeBaselineChecksum([]), expectedChecksum, StringComparison.Ordinal))
            {
                return [];
            }

            if (!File.Exists(BaselinePath))
            {
                return null;
            }

            string[]? itemIds;
            try
            {
                itemIds = await ReadJsonAsync<string[]>(BaselinePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException
                or UnauthorizedAccessException)
            {
                return null;
            }

            if (itemIds is null
                || itemIds.Length != expectedCount
                || !string.Equals(ComputeBaselineChecksum(itemIds), expectedChecksum, StringComparison.Ordinal)
                || !itemIds.All(id => Guid.TryParseExact(id, "N", out _)))
            {
                return null;
            }

            _verifiedBaseline = (itemIds.Length, expectedChecksum, itemIds);
            return itemIds;
        }
        finally
        {
            _baselineGate.Release();
        }
    }
}
