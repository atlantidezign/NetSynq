using static NetSynq.Core.I18n;
using System.Text.RegularExpressions;

namespace NetSynq.Core;

/// <summary>
/// Zip divisi in volumi "a byte": <c>nome.zip.001</c>, <c>nome.zip.002</c>, … Concatenando i volumi in ordine si ottiene
/// esattamente lo zip originale (<c>copy /b nome.zip.001+nome.zip.002 nome.zip</c>); 7-Zip li apre direttamente dal .001.
/// Serve perché System.IO.Compression non gestisce gli zip multivolume e non si vuole dipendere da librerie esterne.
/// </summary>
public static class ZipVolumes
{
    private static readonly Regex VolumeRx = new(@"\.zip\.\d{3,}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsVolumeName(string name) => VolumeRx.IsMatch(name);
    public static bool IsFirstVolume(string name) => name.EndsWith(".zip.001", StringComparison.OrdinalIgnoreCase);
    /// <summary>Uno zip normale o un volume di zip (non conta come "file piccolo" né come file da compattare).</summary>
    public static bool IsZipLike(string name) => name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || IsVolumeName(name);

    public static string VolumeName(string zipPath, int index) => zipPath + "." + index.ToString("D3");

    /// <summary>I volumi consecutivi a partire dal primo (si ferma al primo numero mancante).</summary>
    public static List<string> Find(string firstVolume)
    {
        var basePath = firstVolume.Substring(0, firstVolume.Length - 4); // senza ".001"
        var list = new List<string>();
        for (int i = 1; ; i++)
        {
            var p = VolumeName(basePath, i);
            if (!File.Exists(p)) break;
            list.Add(p);
        }
        return list;
    }

    /// <summary>Apre in lettura uno zip normale oppure, se il percorso è il primo volume, l'insieme dei volumi come un unico flusso.</summary>
    public static Stream OpenRead(string zipPath)
    {
        if (!IsFirstVolume(zipPath)) return new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var vols = Find(zipPath);
        if (vols.Count == 0) throw new FileNotFoundException(Tr("cmp.zipvol.err_not_found"), zipPath);
        return new VolumeReadStream(vols);
    }

    /// <summary>Cancella lo zip, oppure tutti i suoi volumi se il percorso è il primo volume.</summary>
    public static void Delete(string zipPath)
    {
        var parts = IsFirstVolume(zipPath) ? Find(zipPath) : new List<string> { zipPath };
        foreach (var p in parts) { PathUtil.ClearReadOnly(p); File.Delete(p); }
    }
}

/// <summary>Flusso di sola lettura, con posizionamento, che presenta più file consecutivi come uno solo.</summary>
public sealed class VolumeReadStream : Stream
{
    private readonly string[] _paths;
    private readonly long[] _starts;
    private readonly long _length;
    private long _pos;
    private int _cur = -1;
    private FileStream? _fs;

    public VolumeReadStream(IEnumerable<string> paths)
    {
        _paths = paths.ToArray();
        if (_paths.Length == 0) throw new ArgumentException(Tr("cmp.zipvol.err_none"), nameof(paths));
        _starts = new long[_paths.Length];
        long t = 0;
        for (int i = 0; i < _paths.Length; i++) { _starts[i] = t; t += new FileInfo(_paths[i]).Length; }
        _length = t;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _pos; set => Seek(value, SeekOrigin.Begin); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (count > 0 && _pos < _length)
        {
            int seg = Array.BinarySearch(_starts, _pos);
            if (seg < 0) seg = ~seg - 1;
            if (seg != _cur)
            {
                _fs?.Dispose();
                _fs = new FileStream(_paths[seg], FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                _cur = seg;
            }
            long inSeg = _pos - _starts[seg];
            long segLen = (seg + 1 < _paths.Length ? _starts[seg + 1] : _length) - _starts[seg];
            if (_fs!.Position != inSeg) _fs.Position = inSeg;
            int want = (int)Math.Min(count, segLen - inSeg);
            int k = _fs.Read(buffer, offset, want);
            if (k <= 0) break;
            _pos += k; offset += k; count -= k; total += k;
        }
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long p = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => _length + offset };
        if (p < 0) throw new IOException(Tr("cmp.zipvol.err_negative"));
        _pos = p;
        return _pos;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _fs?.Dispose(); _fs = null; }
        base.Dispose(disposing);
    }
}
