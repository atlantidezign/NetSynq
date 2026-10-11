using System.Text.RegularExpressions;

namespace NetSynq.Core;

public static class PathUtil
{
    public const FileAttributes ReplicatedAttrs = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;
    public const string PartExtension = ".nspart";
    public static readonly long MTimeToleranceTicks = TimeSpan.TicksPerSecond * 2;

    public static StringComparer PathComparer => StringComparer.OrdinalIgnoreCase;

    public static string ToRel(string root, string full) =>
        Path.GetRelativePath(root, full).Replace('\\', '/');

    /// <summary>
    /// Risolve un percorso relativo ricevuto dalla rete dentro la radice, rifiutando
    /// qualsiasi tentativo di uscirne (.., percorsi assoluti, drive, stream alternativi).
    /// </summary>
    public static string? Resolve(string root, string? rel)
    {
        if (string.IsNullOrEmpty(rel) || rel.Contains('\0')) return null;
        if (rel.StartsWith('/')) return null;
        if (OperatingSystem.IsWindows() && (rel.Contains(':') || rel.Contains('\\'))) return null;
        foreach (var seg in rel.Split('/'))
            if (seg.Length == 0 || seg == "." || seg == "..") return null;

        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(rootFull, rel.Replace('/', Path.DirectorySeparatorChar)));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, cmp)) return null;
        return full;
    }

    public static bool SameTime(long a, long b) => Math.Abs(a - b) <= MTimeToleranceTicks;

    public static string FormatBytes(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : $"{v:0.##} {u[i]}";
    }

    /// <summary>Toglie l'attributo sola-lettura (i file di .svn/pristine lo sono) prima di sovrascrivere o cancellare.</summary>
    public static void ClearReadOnly(string path)
    {
        try
        {
            var a = File.GetAttributes(path);
            if ((a & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
                File.SetAttributes(path, a & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    public static void ForceDeleteDirectory(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) { ClearReadOnly(f); File.Delete(f); }
        foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
        { ClearReadOnly(d); Directory.Delete(d); }
        ClearReadOnly(dir);
        Directory.Delete(dir);
    }
}

/// <summary>
/// Pattern di esclusione separati da ';' o a capo. "*.tmp" e "node_modules" valgono per qualunque nome
/// di file/cartella; un pattern con '/' (es. "build/obj*") vale sul percorso relativo completo.
/// </summary>
public sealed class ExcludeMatcher
{
    private readonly List<Regex> _names = new();
    private readonly List<Regex> _paths = new();

    public ExcludeMatcher(string? patterns)
    {
        if (string.IsNullOrWhiteSpace(patterns)) return;
        foreach (var raw in patterns.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = raw.Trim().Replace('\\', '/');
            if (p.Length == 0) continue;
            var rx = new Regex("^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            (p.Contains('/') ? _paths : _names).Add(rx);
        }
    }

    public bool IsExcluded(string rel)
    {
        foreach (var r in _paths) if (r.IsMatch(rel)) return true;
        if (_names.Count == 0) return false;
        foreach (var seg in rel.Split('/'))
            foreach (var r in _names) if (r.IsMatch(seg)) return true;
        return false;
    }
}
