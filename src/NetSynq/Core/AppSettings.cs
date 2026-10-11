using System.Security.Cryptography;
using System.Text.Json;
using static NetSynq.Core.I18n;

namespace NetSynq.Core;

public sealed class AppSettings
{
    public int SelectedTab { get; set; } = 1;
    /// <summary>Lingua dell'interfaccia: "it" o "en".</summary>
    public string Language { get; set; } = "it";
    /// <summary>true = tema scuro.</summary>
    public bool Dark { get; set; }

    // master (server)
    public string ServerFolder { get; set; } = "";
    public string ServerNic { get; set; } = "";
    public string PeerHint { get; set; } = "";
    public int ServerPort { get; set; } = 48765;
    public string ServerToken { get; set; } = NewToken();
    public string ServerExcludes { get; set; } = "";

    // slave (client)
    public string ClientHost { get; set; } = "";
    public int ClientPort { get; set; } = 48765;
    public string ClientToken { get; set; } = "";
    public string ClientFolder { get; set; } = "";
    public CompareMode Compare { get; set; } = CompareMode.Smart;
    public string ClientExcludes { get; set; } = "";
    public bool DeleteExtra { get; set; } = true;
    public bool DryRun { get; set; }
    // scheda Compatta
    public string CompactFolder { get; set; } = "";
    public int CompactN { get; set; } = 5;
    public int CompactKb { get; set; } = 10;
    public int CompactMaxFolderMb { get; set; } = 4096;
    public int CompactMaxFileMb { get; set; } = 50;
    public int CompactMode { get; set; }
    public string CompactExcludes { get; set; } = ".git; .svn";
    public bool CompactMerge { get; set; } = true;
    public bool CompactPartial { get; set; } = true;
    public string CompactAlways { get; set; } = "node_modules";
    public string CompactSubs { get; set; } = "profiles";
    public int Parallel { get; set; } = 3;

    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetSynq");
    public static string FilePath => Path.Combine(DataDir, "settings.json");

    /// <summary>Lunghezza dei token generati automaticamente (breve da digitare; è comunque modificabile a mano).</summary>
    public const int GeneratedTokenLength = 6;
    public const int MinTokenLength = 4;

    public static string NewToken()
    {
        // alfabeto di 32 simboli senza caratteri ambigui (niente 0/O, 1/I): 32 divide 256, nessun bias
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var b = RandomNumberGenerator.GetBytes(GeneratedTokenLength);
        return new string(b.Select(x => alphabet[x % alphabet.Length]).ToArray());
    }

    /// <summary>Il token viaggia in un header HTTP: solo ASCII stampabile, senza spazi.</summary>
    public static string? ValidateToken(string token, bool checkMinLength)
    {
        if (token.Length == 0) return Tr("file.token.missing");
        if (token.Any(c => c <= 32 || c >= 127))
            return Tr("file.token.chars");
        if (checkMinLength && token.Length < MinTokenLength)
            return Tr("file.token.short", MinTokenLength);
        return null;
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                // i vecchi token generati automaticamente erano di 16 caratteri: li accorcio (uno scelto a mano resta com'è)
                if (s.ServerToken.Length == 16 && s.ServerToken.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)))
                    s.ServerToken = NewToken();
                return s;
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
