using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace PatchIt.Client.Services;

public sealed record InstallResult(
    int Files, int Replaced, string BackupDirectory, string? RemovedBackup = null);

/// <summary>
/// Copia i file di una patch nella cartella del gioco. Prima di sovrascrivere
/// qualcosa salva l'originale in un backup con un manifest, così "Ripristina"
/// può rimettere la cartella esattamente come era.
/// </summary>
public sealed class PatchInstaller
{
    private static readonly string[] IgnoredNames = ["__MACOSX", ".DS_Store", "Thumbs.db"];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string BackupsRoot { get; } = Path.Combine(AppPaths.Root, "backups");

    public static bool CanExtract(string path) =>
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    /// <summary>Nomi dei file contenuti nello zip, per la conferma prima di installare.</summary>
    public static IReadOnlyList<string> ListFiles(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            return archive.Entries
                .Where(e => e.Name.Length > 0 && !IsIgnored(e.FullName))
                .Select(e => e.FullName.Replace('\\', '/'))
                .Where(name => TryResolve(Path.GetTempPath(), name, out _))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public InstallResult Install(
        string zipPath, string destinationFolder, string gameId, string? existingBackup = null)
    {
        if (!CanExtract(zipPath))
            throw new InvalidOperationException("Il file scaricato non è uno zip valido.");

        Directory.CreateDirectory(destinationFolder);

        using var archive = ZipFile.OpenRead(zipPath);

        var entries = archive.Entries
            .Where(e => e.Name.Length > 0 && !IsIgnored(e.FullName))
            .Select(e => (Entry: e, Relative: e.FullName.Replace('\\', '/')))
            .Where(x => TryResolve(destinationFolder, x.Relative, out _))
            .ToList();

        if (entries.Count == 0)
            throw new InvalidOperationException("Lo zip non contiene file da estrarre.");

        // Un backup già presente per la stessa cartella viene riusato: contiene i file
        // originali, mentre quelli sul disco sono già stati modificati da una patch.
        var reusable = TryLoadManifest(existingBackup, destinationFolder, out var reusedManifest);

        var backupDirectory = reusable
            ? existingBackup!
            : Path.Combine(BackupsRoot, SafeName(gameId), DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));

        // cartella del gioco cambiata: il backup vecchio non è più raggiungibile dal ripristino
        var removedBackup =
            !reusable && !string.IsNullOrWhiteSpace(existingBackup)
            && Directory.Exists(existingBackup) && TryDeleteBackup(existingBackup)
                ? existingBackup
                : null;

        Directory.CreateDirectory(backupDirectory);

        var manifest = reusable
            ? reusedManifest!
            : new BackupManifest { Destination = destinationFolder };

        var savedEarlier = manifest.Files
            .Select(f => f.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var replaced = 0;

        foreach (var (_, relative) in entries)
        {
            TryResolve(destinationFolder, relative, out var full);
            var exists = File.Exists(full);
            if (exists) replaced++;

            // l'originale di un file già salvato non va ricopiato: ora è la versione patchata
            if (!savedEarlier.Contains(relative))
                manifest.Files.Add(new BackupEntry { Path = relative, Existed = exists });
        }

        // il manifest si scrive prima dell'estrazione: se il processo si interrompe
        // a metà, il ripristino sa comunque cosa rimettere a posto
        AtomicFile.WriteAllText(
            Path.Combine(backupDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions));

        foreach (var (entry, relative) in entries)
        {
            TryResolve(destinationFolder, relative, out var full);

            var folder = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            if (File.Exists(full) && !savedEarlier.Contains(relative))
            {
                var backupFile = Path.Combine(backupDirectory, relative);
                var backupFolder = Path.GetDirectoryName(backupFile);
                if (!string.IsNullOrEmpty(backupFolder)) Directory.CreateDirectory(backupFolder);
                File.Copy(full, backupFile, overwrite: true);
            }

            entry.ExtractToFile(full, overwrite: true);
        }

        return new InstallResult(entries.Count, replaced, backupDirectory, removedBackup);
    }

    /// <summary>Rimette i file com'erano e cancella il backup. Restituisce i file toccati.</summary>
    public static int Restore(string backupDirectory)
    {
        var manifestPath = Path.Combine(backupDirectory, "manifest.json");

        if (!File.Exists(manifestPath))
            throw new InvalidOperationException("Backup non trovato: è già stato ripristinato.");

        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath))
            ?? throw new InvalidOperationException("Backup illeggibile.");

        var touched = 0;

        foreach (var entry in manifest.Files)
        {
            if (!TryResolve(manifest.Destination, entry.Path, out var full)) continue;

            if (entry.Existed)
            {
                var backupFile = Path.Combine(backupDirectory, entry.Path);
                if (!File.Exists(backupFile)) continue;

                var folder = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

                File.Copy(backupFile, full, overwrite: true);
            }
            else if (File.Exists(full))
            {
                File.Delete(full);
            }

            touched++;
        }

        Directory.Delete(backupDirectory, recursive: true);
        return touched;
    }

    /// <summary>Cancella un backup creato da noi. False se non è possibile o se il percorso è estraneo.</summary>
    public static bool TryDeleteBackup(string? backupDirectory)
    {
        if (string.IsNullOrWhiteSpace(backupDirectory)) return true;
        if (!Directory.Exists(backupDirectory)) return true;
        if (!IsBackupPath(backupDirectory)) return false;

        try
        {
            Directory.Delete(backupDirectory, recursive: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Legge il manifest di un backup esistente, se vale per questa destinazione.</summary>
    private static bool TryLoadManifest(
        string? backupDirectory, string destination, out BackupManifest? manifest)
    {
        manifest = null;

        if (string.IsNullOrWhiteSpace(backupDirectory) || !IsBackupPath(backupDirectory))
            return false;

        var manifestPath = Path.Combine(backupDirectory, "manifest.json");
        if (!File.Exists(manifestPath)) return false;

        try
        {
            manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath));
        }
        catch
        {
            return false;
        }

        return manifest is not null && SamePath(manifest.Destination, destination);
    }

    /// <summary>Un backup si cancella solo se sta dentro la cartella dei backup dell'app.</summary>
    private static bool IsBackupPath(string path)
    {
        try
        {
            var root = Path.GetFullPath(BackupsRoot);
            if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;

            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Vero se il percorso resta dentro la destinazione: uno zip può contenere "../".</summary>
    private static bool TryResolve(string root, string relative, out string full)
    {
        full = "";

        try
        {
            var normalizedRoot = Path.GetFullPath(root);
            if (!normalizedRoot.EndsWith(Path.DirectorySeparatorChar))
                normalizedRoot += Path.DirectorySeparatorChar;

            var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relative));

            if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return false;

            full = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsIgnored(string name) =>
        name.Split('/', '\\').Any(part => IgnoredNames.Contains(part, StringComparer.OrdinalIgnoreCase));

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return clean.Length == 0 ? "patch" : clean;
    }

    private sealed class BackupManifest
    {
        public string Destination { get; set; } = "";
        public List<BackupEntry> Files { get; set; } = [];
    }

    private sealed class BackupEntry
    {
        public string Path { get; set; } = "";
        public bool Existed { get; set; }
    }
}
