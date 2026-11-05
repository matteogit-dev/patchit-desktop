namespace PatchIt.Client.Models;

public sealed class LocalEntry
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string DownloadedAt { get; set; } = "";

    /// <summary>Cartella del gioco in cui è stata copiata la patch.</summary>
    public string InstallFolder { get; set; } = "";

    /// <summary>Backup creato prima di sovrascrivere i file del gioco.</summary>
    public string BackupPath { get; set; } = "";
}

public sealed class LibraryFile
{
    public List<LocalEntry> Entries { get; set; } = [];
}

public sealed class SettingsFile
{
    public string DownloadDirectory { get; set; } = "";

    /// <summary>Cartelle scelte per singoli giochi: id gioco → percorso.</summary>
    public Dictionary<string, string> GameDirectories { get; set; } = new();

    /// <summary>Cartella del gioco (dove installare la patch): id gioco → percorso.</summary>
    public Dictionary<string, string> GameInstallDirectories { get; set; } = new();
}

public enum DownloadState
{
    Idle,
    Queued,
    Downloading,
    Failed,
}

public enum IntegrityState
{
    Unknown,
    Checking,
    Ok,
    Corrupt,
    Missing,
}

public sealed record InstructionItem(int Number, string Text);

public sealed record DownloadHistoryItem(
    string Title,
    string Version,
    string DateLabel,
    string FilePath,
    bool FileExists);

public sealed class ChangelogItem
{
    public ChangelogItem(ChangelogEntry entry, bool isLast, bool isNew = false)
    {
        Version = entry.Version;
        Date = FormatDate(entry.Date);
        Note = entry.Note;
        IsLast = isLast;
        IsNew = isNew;
    }

    public string Version { get; }
    public string Date { get; }
    public string Note { get; }
    public bool IsLast { get; }
    public bool IsNew { get; }

    private static string FormatDate(string iso) =>
        DateOnly.TryParse(iso, out var d) ? d.ToString("dd/MM/yyyy") : iso;
}
