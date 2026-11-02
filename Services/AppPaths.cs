using System.IO;

namespace PatchIt.Client.Services;

public static class AppPaths
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PatchIT");

    public static string Covers { get; } = Path.Combine(Root, "covers");
    public static string CatalogCache { get; } = Path.Combine(Root, "catalog-cache.json");
    public static string Library { get; } = Path.Combine(Root, "library.json");
    public static string Settings { get; } = Path.Combine(Root, "settings.json");

    static AppPaths()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Covers);
    }
}
