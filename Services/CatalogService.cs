using System.IO;
using System.Net.Http;
using System.Text.Json;
using PatchIt.Client.Models;

namespace PatchIt.Client.Services;

public sealed class CatalogService
{
    private const string CatalogUrl = "https://patchit-traduzioni.vercel.app/catalog.json";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<List<Game>> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await Http.GetStringAsync(CatalogUrl, ct);
            var games = Parse(json);
            if (games.Count == 0) throw new InvalidOperationException("Catalogo vuoto.");
            await File.WriteAllTextAsync(AppPaths.CatalogCache, json, ct);
            return games;
        }
        catch (Exception) when (File.Exists(AppPaths.CatalogCache))
        {
            var cached = await File.ReadAllTextAsync(AppPaths.CatalogCache, ct);
            return Parse(cached);
        }
    }

    private static List<Game> Parse(string json) =>
        JsonSerializer.Deserialize<List<Game>>(json, JsonOptions) ?? [];
}
