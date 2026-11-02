using System.IO;
using System.Net.Http;

namespace PatchIt.Client.Services;

public sealed class CoverCacheService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<string?> GetAsync(string url, string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        try
        {
            var ext = Path.GetExtension(new Uri(url).AbsolutePath);
            if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".jpg";

            var path = Path.Combine(AppPaths.Covers, id + ext);
            if (File.Exists(path) && new FileInfo(path).Length > 0) return path;

            var bytes = await Http.GetByteArrayAsync(url, ct);
            if (bytes.Length == 0) return null;

            await File.WriteAllBytesAsync(path, bytes, ct);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
