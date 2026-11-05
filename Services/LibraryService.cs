using System.IO;
using System.Text.Json;
using PatchIt.Client.Models;

namespace PatchIt.Client.Services;

public sealed class LibraryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Dictionary<string, LocalEntry> _entries = new();

    public void Load()
    {
        _entries.Clear();

        try
        {
            if (!File.Exists(AppPaths.Library)) return;
            var file = JsonSerializer.Deserialize<LibraryFile>(
                File.ReadAllText(AppPaths.Library), JsonOptions);
            foreach (var entry in file?.Entries ?? [])
                _entries[entry.Id] = entry;
        }
        catch
        {
            // libreria corrotta: si riparte da zero
        }
    }

    public string? GetVersion(string id) =>
        _entries.TryGetValue(id, out var entry) ? entry.Version : null;

    public LocalEntry? Get(string id) =>
        _entries.TryGetValue(id, out var entry) ? entry : null;

    public IReadOnlyList<LocalEntry> GetAll() =>
        _entries.Values
            .OrderByDescending(e => e.DownloadedAt, StringComparer.Ordinal)
            .ToList();

    public void Clear()
    {
        _entries.Clear();

        try
        {
            if (File.Exists(AppPaths.Library)) File.Delete(AppPaths.Library);
        }
        catch
        {
            // file non eliminabile: la prossima Save lo sovrascriverà
        }
    }

    public void Save(string id, string version, string filePath)
    {
        // un nuovo download non è ancora installato nel gioco, ma il backup
        // della versione precedente resta valido finché non viene ripristinato
        _entries.TryGetValue(id, out var existing);

        _entries[id] = new LocalEntry
        {
            Id = id,
            Version = version,
            FilePath = filePath,
            DownloadedAt = DateTime.Now.ToString("o"),
            InstallFolder = existing?.InstallFolder ?? "",
            BackupPath = existing?.BackupPath ?? "",
        };

        Persist();
    }

    /// <summary>Registra l'installazione nel gioco e il backup da cui ripristinare.</summary>
    public void MarkInstalled(string id, string installFolder, string backupPath)
    {
        if (!_entries.TryGetValue(id, out var entry)) return;

        entry.InstallFolder = installFolder;
        entry.BackupPath = backupPath;
        Persist();
    }

    public void ClearInstall(string id)
    {
        if (!_entries.TryGetValue(id, out var entry)) return;

        entry.InstallFolder = "";
        entry.BackupPath = "";
        Persist();
    }

    /// <summary>Rimuove la voce dalla libreria. Il file scaricato resta sul disco.</summary>
    public void Remove(string id)
    {
        if (!_entries.Remove(id)) return;
        Persist();
    }

    private void Persist()
    {
        var file = new LibraryFile { Entries = _entries.Values.OrderBy(e => e.Id).ToList() };
        AtomicFile.WriteAllText(AppPaths.Library, JsonSerializer.Serialize(file, JsonOptions));
    }
}
