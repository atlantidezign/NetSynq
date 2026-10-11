using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace NetSynq.Core;

/// <summary>
/// Cache degli hash SHA-256 indicizzata per (percorso, dimensione, data di modifica):
/// un file non cambiato non viene mai riletto. Salvata su disco in JSON.
/// </summary>
public sealed class HashCache
{
    private sealed class Rec { public long S { get; set; } public long M { get; set; } public string H { get; set; } = ""; }

    private readonly ConcurrentDictionary<string, Rec> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _file;
    private int _dirty;

    public HashCache(string? file)
    {
        _file = file;
        if (file == null || !File.Exists(file)) return;
        try
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, Rec>>(File.ReadAllText(file));
            if (d != null) foreach (var kv in d) _map[kv.Key] = kv.Value;
        }
        catch { /* cache corrotta: si ricostruisce */ }
    }

    public async Task<string> GetAsync(string fullPath, long size, long mtimeTicks,
        Action<long>? onBytes = null, CancellationToken ct = default)
    {
        if (_map.TryGetValue(fullPath, out var r) && r.S == size && r.M == mtimeTicks)
        {
            onBytes?.Invoke(size);
            return r.H;
        }
        var h = await Task.Run(() => Compute(fullPath, onBytes, ct), ct).ConfigureAwait(false);
        _map[fullPath] = new Rec { S = size, M = mtimeTicks, H = h };
        Interlocked.Increment(ref _dirty);
        return h;
    }

    public static string Compute(string path, Action<long>? onBytes, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var buf = new byte[1 << 20];
        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.TransformBlock(buf, 0, n, null, 0);
            onBytes?.Invoke(n);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    public void Save()
    {
        if (_file == null || Interlocked.Exchange(ref _dirty, 0) == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_map));
            File.Move(tmp, _file, true);
        }
        catch { /* best effort */ }
    }
}
