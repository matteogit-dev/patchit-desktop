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
        _entries[id] = new LocalEntry
        {
            Id = id,
            Version = version,
            FilePath = filePath,
            DownloadedAt = DateTime.Now.ToString("o"),
        };

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
