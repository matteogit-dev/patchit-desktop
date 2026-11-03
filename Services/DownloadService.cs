using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace PatchIt.Client.Services;

public enum DownloadStage
{
    Downloading,
    Verifying,
    Completed,
}

public sealed record DownloadProgress(double Percent, DownloadStage Stage);

/// <summary>
/// Scarica una patch in un file temporaneo ".part", riprende da dove si era
/// interrotta se il server accetta Range, e verifica lo sha256 mentre legge
/// gli ultimi byte: il file viene spostato a destinazione solo se l'hash torna.
/// </summary>
public sealed class DownloadService
{
    private const int MaxAttempts = 3;
    private const int BufferSize = 81920;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    public static string TempPathFor(string destinationPath) => destinationPath + ".part";

    public async Task DownloadAsync(
        string url,
        string destinationPath,
        string? expectedSha256 = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var tempPath = TempPathFor(destinationPath);
        var expected = NormalizeHash(expectedSha256);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadOnceAsync(url, tempPath, destinationPath, expected, progress, ct);
                return;
            }
            catch (OperationCanceledException)
            {
                // il .part resta su disco: il prossimo download riprende da lì
                throw;
            }
            catch (InvalidDataException)
            {
                // hash non corrispondente: il parziale è da buttare
                TryDelete(tempPath);
                throw;
            }
            catch (Exception ex) when (IsPermanent(ex))
            {
                throw;
            }
            catch (Exception) when (attempt < MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct);
            }
        }
    }

    private static async Task DownloadOnceAsync(
        string url,
        string tempPath,
        string destinationPath,
        string? expected,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        while (true)
        {
            var offset = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0)
            {
                // il file sul server non combacia più col parziale: si riparte da zero
                TryDelete(tempPath);
                continue;
            }

            response.EnsureSuccessStatusCode();

            // 206 = il server ha accettato la ripresa; con 200 si ricomincia da capo
            var append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            var hash = await ReceiveAsync(response, tempPath, append, append ? offset : 0, progress, ct);

            VerifyAndMove(tempPath, destinationPath, expected, hash, progress);
            return;
        }
    }

    private static async Task<byte[]> ReceiveAsync(
        HttpResponseMessage response,
        string tempPath,
        bool append,
        long startOffset,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // il parziale già scaricato fa parte del file: entra nell'hash
        if (append) AppendFileToHash(hasher, tempPath);

        var remaining = response.Content.Headers.ContentLength ?? 0;
        var total = remaining > 0 ? startOffset + remaining : 0;
        var received = startOffset;

        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = new FileStream(
            tempPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            useAsync: true))
        {
            var buffer = new byte[BufferSize];
            int read;

            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                hasher.AppendData(buffer, 0, read);
                received += read;

                if (total > 0)
                    progress?.Report(new DownloadProgress(received * 100.0 / total, DownloadStage.Downloading));
            }

            await target.FlushAsync(ct);
        }

        return hasher.GetHashAndReset();
    }

    private static void VerifyAndMove(
        string tempPath,
        string destinationPath,
        string? expected,
        byte[] hash,
        IProgress<DownloadProgress>? progress)
    {
        progress?.Report(new DownloadProgress(100, DownloadStage.Verifying));

        if (expected is not null)
        {
            var actual = Convert.ToHexString(hash);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Il file scaricato non corrisponde all'hash pubblicato dal sito.");
        }

        File.Move(tempPath, destinationPath, overwrite: true);
        progress?.Report(new DownloadProgress(100, DownloadStage.Completed));
    }

    private static void AppendFileToHash(IncrementalHash hasher, string path)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[BufferSize];
        int read;

        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            hasher.AppendData(buffer, 0, read);
    }

    /// <summary>Normalizza l'hash del catalogo; null = nessun hash verificabile.</summary>
    private static string? NormalizeHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var text = value.Trim();
        if (text.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            text = text["sha256:".Length..];

        return text.Length == 64 ? text.ToLowerInvariant() : null;
    }

    /// <summary>Gli errori del client (404, 403…) non hanno senso da ritentare.</summary>
    private static bool IsPermanent(Exception ex) =>
        ex is HttpRequestException { StatusCode: { } status }
        && (int)status is >= 400 and < 500
        && status != HttpStatusCode.RequestTimeout
        && status != HttpStatusCode.TooManyRequests;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // file bloccato: resterà lì e verrà sovrascritto al prossimo tentativo
        }
    }
}
