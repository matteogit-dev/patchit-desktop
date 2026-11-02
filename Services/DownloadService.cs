using System.IO;
using System.Net.Http;

namespace PatchIt.Client.Services;

public sealed class DownloadService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    public async Task DownloadAsync(
        string url,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var tempPath = destinationPath + ".part";

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;

        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = File.Create(tempPath))
        {
            var buffer = new byte[81920];
            long readTotal = 0;
            int read;

            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                readTotal += read;
                if (totalBytes is > 0)
                    progress?.Report(readTotal * 100.0 / totalBytes.Value);
            }
        }

        File.Move(tempPath, destinationPath, overwrite: true);
        progress?.Report(100);
    }
}
