using System.Net.Http;
using System.Text.Json;

namespace PatchIt.Client.Services;

public sealed record AppUpdate(
    string Version,
    string Url,
    string? AssetUrl,
    string? AssetName,
    long AssetSize);

/// <summary>
/// Chiede a GitHub l'ultima release pubblicata e la confronta con la versione
/// in esecuzione. Qualsiasi errore vale "nessun aggiornamento": un controllo
/// di cortesia non deve mai disturbare l'uso dell'app.
/// </summary>
public sealed class UpdateChecker
{
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/matteogit-dev/patchit-desktop/releases/latest";

    private const string ReleasesPage =
        "https://github.com/matteogit-dev/patchit-desktop/releases";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    static UpdateChecker()
    {
        // l'API di GitHub rifiuta le richieste senza User-Agent
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("PatchIT-Desktop");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>Null se la versione locale è già la più recente o se il controllo fallisce.</summary>
    public async Task<AppUpdate?> GetLatestAsync(string currentVersion, CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.GetAsync(LatestReleaseUrl, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement;

            if (!root.TryGetProperty("tag_name", out var tag)) return null;

            var version = tag.GetString();
            if (string.IsNullOrWhiteSpace(version)) return null;

            if (!VersionComparer.IsNewer(version, currentVersion)) return null;

            var url = root.TryGetProperty("html_url", out var html) ? html.GetString() : null;

            // l'eseguibile della release, per aggiornare senza passare dal browser
            string? assetUrl = null, assetName = null;
            long assetSize = 0;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (!asset.TryGetProperty("name", out var nameProp)) continue;

                    var name = nameProp.GetString() ?? "";
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                    if (asset.TryGetProperty("browser_download_url", out var downloadProp))
                        assetUrl = downloadProp.GetString();

                    assetName = name;

                    if (asset.TryGetProperty("size", out var sizeProp) && sizeProp.TryGetInt64(out var size))
                        assetSize = size;

                    break;
                }
            }

            return new AppUpdate(
                version.Trim().TrimStart('v', 'V'),
                string.IsNullOrWhiteSpace(url) ? ReleasesPage : url,
                assetUrl,
                assetName,
                assetSize);
        }
        catch
        {
            return null;
        }
    }
}
