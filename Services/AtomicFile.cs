using System.IO;

namespace PatchIt.Client.Services;

/// <summary>
/// Scrive su un file temporaneo e poi sostituisce l'originale: se l'app si chiude
/// a metà salvataggio non resta un JSON troncato (che al load verrebbe scartato).
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }
}
