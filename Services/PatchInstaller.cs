using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace PatchIt.Client.Services;

public sealed record InstallResult(int Files, int Replaced, string BackupDirectory);

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

    public InstallResult Install(string zipPath, string destinationFolder, string gameId)
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

        var backupDirectory = Path.Combine(
            BackupsRoot, SafeName(gameId), DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(backupDirectory);

        var manifest = new BackupManifest { Destination = destinationFolder };
        var replaced = 0;

        foreach (var (_, relative) in entries)
        {
            TryResolve(destinationFolder, relative, out var full);
            var exists = File.Exists(full);
            if (exists) replaced++;
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

            if (File.Exists(full))
            {
                var backupFile = Path.Combine(backupDirectory, relative);
                var backupFolder = Path.GetDirectoryName(backupFile);
                if (!string.IsNullOrEmpty(backupFolder)) Directory.CreateDirectory(backupFolder);
                File.Copy(full, backupFile, overwrite: true);
            }

            entry.ExtractToFile(full, overwrite: true);
        }

        return new InstallResult(entries.Count, replaced, backupDirectory);
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
