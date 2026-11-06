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

    /// <summary>Dimensione delle card della libreria: "s", "m" o "l".</summary>
    public string CardSize { get; set; } = "m";
}

/// <summary>Una voce della palette dei comandi: un gioco, un'azione o una destinazione.</summary>
public sealed record PaletteItem(
    string Label,
    string Detail,
    string Group,
    string? GameId = null,
    string? Action = null);

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

public enum ToastKind
{
    Success,
    Info,
}

/// <summary>Conferma non bloccante mostrata in basso a destra.</summary>
public sealed record ToastMessage(string Text, ToastKind Kind);

/// <summary>
/// Una conferma da mostrare dentro la finestra, al posto dei MessageBox di sistema:
/// stesso tema del resto dell'app, tastiera gestita (↵ conferma, esc annulla).
/// </summary>
public sealed class ConfirmRequest
{
    /// <summary>Riga piccola sopra il titolo: dice in che area si sta agendo.</summary>
    public string Eyebrow { get; init; } = "";

    public string Title { get; init; } = "";
    public string Message { get; init; } = "";

    /// <summary>Percorso o dato concreto su cui si agisce, mostrato in monospaziato.</summary>
    public string DetailLabel { get; init; } = "DESTINAZIONE";
    public string Detail { get; init; } = "";

    /// <summary>Elenco completo dei file: la trasparenza vale più dello spazio.</summary>
    public string FilesLabel { get; init; } = "FILE";
    public IReadOnlyList<string> Files { get; init; } = [];

    public string Note { get; init; } = "";
    public string ConfirmText { get; init; } = "Conferma";
    public string CancelText { get; init; } = "Annulla";

    public bool HasEyebrow => Eyebrow.Length > 0;
    public bool HasDetail => Detail.Length > 0;
    public bool HasFiles => Files.Count > 0;
    public bool HasNote => Note.Length > 0;
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
