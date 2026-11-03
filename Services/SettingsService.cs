using System.IO;
using System.Text.Json;
using PatchIt.Client.Models;

namespace PatchIt.Client.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string DownloadDirectory { get; private set; } = DefaultDirectory();

    public void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.Settings)) return;
            var file = JsonSerializer.Deserialize<SettingsFile>(
                File.ReadAllText(AppPaths.Settings), JsonOptions);
            if (!string.IsNullOrWhiteSpace(file?.DownloadDirectory))
                DownloadDirectory = file.DownloadDirectory;
        }
        catch
        {
            // impostazioni corrotte: si usano i default
        }
    }

    public void Save(string downloadDirectory)
    {
        DownloadDirectory = downloadDirectory;
        var file = new SettingsFile { DownloadDirectory = downloadDirectory };
        AtomicFile.WriteAllText(AppPaths.Settings, JsonSerializer.Serialize(file, JsonOptions));
    }

    private static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "PatchIT");
}
