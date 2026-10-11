using System.Text.Json;

namespace NetSynq.Core;

/// <summary>
/// Testi dell'applicazione in più lingue. I dizionari sono file JSON piatti (chiave → testo) incorporati nell'exe
/// (Lang/it.json, Lang/en.json). Se accanto all'exe esiste una cartella "lang" con un it.json / en.json, le sue voci
/// hanno la precedenza: così i testi si possono correggere senza ricompilare.
/// Nei testi i segnaposto sono {0}, {1}… (anche con formato: {0:N0}); le graffe letterali vanno raddoppiate solo
/// nei testi che hanno argomenti.
/// </summary>
public static class I18n
{
    public static readonly string[] Languages = { "it", "en" };

    private static readonly Dictionary<string, string> Italian = Load("it");
    private static Dictionary<string, string> _cur = Italian;

    public static string Lang { get; private set; } = "it";

    /// <summary>Sollevato (sul thread che chiama SetLanguage) dopo ogni cambio di lingua.</summary>
    public static event Action? Changed;

    public static void SetLanguage(string code)
    {
        code = Languages.Contains(code, StringComparer.OrdinalIgnoreCase) ? code.ToLowerInvariant() : "it";
        if (code == Lang) return;
        Lang = code;
        _cur = code == "it" ? Italian : Load(code);
        Changed?.Invoke();
    }

    /// <summary>Testo per la chiave nella lingua corrente (se manca: in italiano; se manca anche lì: la chiave stessa).</summary>
    public static string Tr(string key) =>
        _cur.TryGetValue(key, out var v) ? v : Italian.TryGetValue(key, out var d) ? d : key;

    /// <summary>Come <see cref="Tr(string)"/>, sostituendo {0}, {1}… con gli argomenti (senza argomenti il testo resta com'è).</summary>
    public static string Tr(string key, params object?[] args)
    {
        var t = Tr(key);
        if (args.Length == 0) return t;
        try { return string.Format(t, args); } catch (FormatException) { return t; }
    }

    internal static Dictionary<string, string> Load(string code)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var s = typeof(I18n).Assembly.GetManifestResourceStream("lang." + code + ".json");
            if (s != null) Merge(dict, s);
        }
        catch { /* dizionario incorporato illeggibile: si usano le chiavi */ }
        try
        {
            var f = Path.Combine(AppContext.BaseDirectory, "lang", code + ".json");
            if (File.Exists(f)) { using var fs = File.OpenRead(f); Merge(dict, fs); }
        }
        catch { /* file esterno non valido: restano i testi incorporati */ }
        return dict;
    }

    internal static void Merge(Dictionary<string, string> dict, Stream s)
    {
        var o = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using var doc = JsonDocument.Parse(s, o);
        foreach (var p in doc.RootElement.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String) dict[p.Name] = p.Value.GetString() ?? "";
    }
}
