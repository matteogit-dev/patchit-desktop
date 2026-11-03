using System.IO;
using System.Text.Json;
using PatchIt.Client.Models;

namespace PatchIt.Client.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Dictionary<string, string> _gameDirectories = new();

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

            _gameDirectories.Clear();

            if (file?.GameDirectories is { } directories)
                foreach (var pair in directories)
                    if (!string.IsNullOrWhiteSpace(pair.Value))
                        _gameDirectories[pair.Key] = pair.Value;
        }
        catch
        {
            // impostazioni corrotte: si usano i default
        }
    }

    public void Save(string downloadDirectory)
    {
        DownloadDirectory = downloadDirectory;
        Persist();
    }

    /// <summary>Cartella scelta per un singolo gioco; null = si usa quella predefinita.</summary>
    public string? GetGameDirectory(string id) =>
        _gameDirectories.TryGetValue(id, out var path) ? path : null;

    public void SetGameDirectory(string id, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) _gameDirectories.Remove(id);
        else _gameDirectories[id] = path;

        Persist();
    }

    private void Persist()
    {
        var file = new SettingsFile
        {
            DownloadDirectory = DownloadDirectory,
            GameDirectories = _gameDirectories,
        };

        AtomicFile.WriteAllText(AppPaths.Settings, JsonSerializer.Serialize(file, JsonOptions));
    }

    private static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "PatchIT");
}
