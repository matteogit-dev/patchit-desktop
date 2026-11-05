using System.IO;
using System.Security.Cryptography;

namespace PatchIt.Client.Services;

/// <summary>Ricalcola l'hash di un file già scaricato e lo confronta col catalogo.</summary>
public static class FileIntegrity
{
    /// <summary>Null se il file non esiste o non è leggibile.</summary>
    public static async Task<string?> HashAsync(string path, CancellationToken ct = default)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = await SHA256.HashDataAsync(stream, ct);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public static bool Matches(string actual, string expected) =>
        string.Equals(actual.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);
}
