namespace PatchIt.Client.Services;

/// <summary>
/// Confronta versioni tipo "v1.0.10" / "1.2.0" per componenti numeriche,
/// così 1.0.10 risulta più recente di 1.0.9 (il confronto a stringa sbaglia).
/// </summary>
public static class VersionComparer
{
    /// <summary>True se <paramref name="candidate"/> è più recente di <paramref name="baseline"/>.</summary>
    public static bool IsNewer(string? candidate, string? baseline)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (string.IsNullOrWhiteSpace(baseline)) return true;
        return Compare(candidate, baseline) > 0;
    }

    public static int Compare(string left, string right)
    {
        // versioni non numeriche: si torna al confronto testuale
        if (!TryParse(left, out var a, out var preA) || !TryParse(right, out var b, out var preB))
            return string.CompareOrdinal(Normalize(left), Normalize(right));

        var length = Math.Max(a.Length, b.Length);
        for (var i = 0; i < length; i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }

        // numeri uguali: una versione senza suffisso è più recente della sua pre-release
        if (preA.Length == 0 && preB.Length == 0) return 0;
        if (preA.Length == 0) return 1;
        if (preB.Length == 0) return -1;
        return string.CompareOrdinal(preA, preB);
    }

    private static bool TryParse(string value, out int[] numbers, out string prerelease)
    {
        numbers = [];
        prerelease = "";

        var text = Normalize(value);
        if (text.Length == 0) return false;

        var plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];

        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = text[(dash + 1)..];
            text = text[..dash];
        }

        var parts = text.Split('.');
        numbers = new int[parts.Length];

        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out numbers[i])) return false;

        return true;
    }

    private static string Normalize(string value)
    {
        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        return text.Trim();
    }
}
