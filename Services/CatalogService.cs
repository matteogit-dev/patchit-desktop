using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using PatchIt.Client.Models;

namespace PatchIt.Client.Services;

public enum CatalogSource
{
    /// <summary>Scaricato adesso dal sito.</summary>
    Network,

    /// <summary>Il server ha risposto 304: la copia locale era già aggiornata.</summary>
    NotModified,

    /// <summary>Rete non disponibile: si sta usando la copia salvata.</summary>
    Cache,
}

public sealed record CatalogResult(List<Game> Games, CatalogSource Source, DateTimeOffset? FetchedAt);

public sealed class CatalogService
{
    private const string CatalogUrl = "https://patchit-traduzioni.vercel.app/catalog.json";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<CatalogResult> LoadAsync(CancellationToken ct = default)
    {
        var meta = ReadMeta();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, CatalogUrl);

            // "è cambiato?" ha senso solo se abbiamo già una copia da riusare
            if (meta.Etag is { Length: > 0 } etag && File.Exists(AppPaths.CatalogCache))
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);

            using var response = await Http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.NotModified && File.Exists(AppPaths.CatalogCache))
            {
                var cachedGames = Parse(await File.ReadAllTextAsync(AppPaths.CatalogCache, ct));
                if (cachedGames.Count > 0)
                    return new CatalogResult(cachedGames, CatalogSource.NotModified, meta.FetchedAt);
            }

            response.EnsureSuccessStatusCode();

            var games = Parse(await response.Content.ReadAsStringAsync(ct));
            if (games.Count == 0) throw new InvalidOperationException("Catalogo vuoto.");

            var fetchedAt = DateTimeOffset.Now;
            await File.WriteAllTextAsync(AppPaths.CatalogCache, await response.Content.ReadAsStringAsync(ct), ct);
            WriteMeta(response.Headers.ETag?.Tag, fetchedAt);

            return new CatalogResult(games, CatalogSource.Network, fetchedAt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // rete assente o server in errore: si riusa la copia locale, se c'è
            if (!File.Exists(AppPaths.CatalogCache)) throw;

            var games = Parse(await File.ReadAllTextAsync(AppPaths.CatalogCache, ct));
            if (games.Count == 0) throw;

            var fetchedAt = meta.FetchedAt
                ?? new DateTimeOffset(File.GetLastWriteTime(AppPaths.CatalogCache));

            return new CatalogResult(games, CatalogSource.Cache, fetchedAt);
        }
    }

    private static List<Game> Parse(string json) =>
        JsonSerializer.Deserialize<List<Game>>(json, JsonOptions) ?? [];

    private static CatalogMeta ReadMeta()
    {
        try
        {
            if (!File.Exists(AppPaths.CatalogMeta)) return new CatalogMeta();
            return JsonSerializer.Deserialize<CatalogMeta>(File.ReadAllText(AppPaths.CatalogMeta))
                ?? new CatalogMeta();
        }
        catch
        {
            return new CatalogMeta();
        }
    }

    private static void WriteMeta(string? etag, DateTimeOffset fetchedAt)
    {
        try
        {
            AtomicFile.WriteAllText(
                AppPaths.CatalogMeta,
                JsonSerializer.Serialize(new CatalogMeta { Etag = etag, FetchedAt = fetchedAt }));
        }
        catch
        {
            // il catalogo è comunque salvato: si riproverà al prossimo avvio
        }
    }

    private sealed class CatalogMeta
    {
        public string? Etag { get; set; }
        public DateTimeOffset? FetchedAt { get; set; }
    }
}
