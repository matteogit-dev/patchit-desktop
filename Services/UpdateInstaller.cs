using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace PatchIt.Client.Services;

/// <summary>
/// Scarica l'exe della nuova release e lo sostituisce quando l'app è chiusa.
/// Windows non permette di sovrascrivere un eseguibile in esecuzione: uno
/// script in %TEMP% aspetta la chiusura del processo, copia il file e riavvia.
/// </summary>
public sealed class UpdateInstaller
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    static UpdateInstaller()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("PatchIT-Desktop");
    }

    public static string UpdatesRoot { get; } = Path.Combine(AppPaths.Root, "updates");

    public async Task<string> DownloadAsync(
        AppUpdate update,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(update.AssetUrl))
            throw new InvalidOperationException("La release non ha un eseguibile da scaricare.");

        var folder = Path.Combine(UpdatesRoot, update.Version);
        Directory.CreateDirectory(folder);

        var name = string.IsNullOrWhiteSpace(update.AssetName) ? "PatchIT.exe" : update.AssetName;
        var path = Path.Combine(folder, name);

        using var response = await Http.GetAsync(
            update.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? update.AssetSize;

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long received = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;

            if (total > 0) progress?.Report(received * 100.0 / total);
        }

        return path;
    }

    /// <summary>Avvia lo script che sostituisce l'exe e riapre l'app.</summary>
    public bool TryStartSwap(string downloadedExe, out string error)
    {
        error = "";

        var target = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(target)
            || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            error = "Non riesco a individuare PatchIT.exe: scarica l'aggiornamento dalla pagina della release.";
            return false;
        }

        try
        {
            // se la cartella non è scrivibile tanto vale dirlo subito, senza scaricare
            var probe = Path.Combine(Path.GetDirectoryName(target)!, $".patchit-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch
        {
            error = "La cartella di PatchIT non è scrivibile: scarica l'aggiornamento dalla pagina della release.";
            return false;
        }

        try
        {
            var script = Path.Combine(Path.GetTempPath(), $"patchit-update-{Guid.NewGuid():N}.cmd");
            File.WriteAllText(script, SwapScript);

            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(script);
            start.ArgumentList.Add(downloadedExe);
            start.ArgumentList.Add(target);

            Process.Start(start);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Impossibile avviare l'aggiornamento: {ex.Message}";
            return false;
        }
    }

    /// <summary>Aspetta che il processo rilasci l'exe, lo sostituisce e riapre l'app.</summary>
    private const string SwapScript = """
        @echo off
        setlocal
        set "SRC=%~1"
        set "DST=%~2"
        for /l %%i in (1,1,120) do (
            copy /y "%SRC%" "%DST%" >nul 2>&1 && goto done
            ping -n 2 127.0.0.1 >nul
        )
        exit /b 1
        :done
        del "%SRC%" >nul 2>&1
        start "" "%DST%"
        del "%~f0" >nul 2>&1
        """;
}
