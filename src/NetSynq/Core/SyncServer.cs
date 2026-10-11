using System.Buffers;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static NetSynq.Core.I18n;

namespace NetSynq.Core;

/// <summary>
/// Mini server HTTP/1.1 in sola lettura (nessun ASP.NET, nessun permesso di amministratore, nessun urlacl):
///   GET /ping            -> info
///   GET /manifest        -> elenco file/cartelle (gzip se richiesto)
///   GET /hash?path=      -> SHA-256 di un file (con cache)
///   GET /file?path=      -> contenuto (supporta Range per riprendere)
/// Si mette in ascolto SOLO sull'IP indicato (quello della LAN), mai su tutte le interfacce.
/// Ogni richiesta richiede l'header X-Token.
/// </summary>
public sealed class SyncServer : IDisposable
{
    public const int ProtocolVersion = 1;

    private readonly string _root;
    private readonly IPAddress _bind;
    private readonly int _port;
    private readonly byte[] _token;
    private readonly ExcludeMatcher _ex;
    private readonly HashCache _cache;
    private readonly Action<string> _log;
    private readonly string _name;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private long _bytesSent;
    private int _clients;

    public int Port { get; private set; }
    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public int ActiveConnections => Volatile.Read(ref _clients);
    public bool IsRunning => _listener != null;

    public SyncServer(string root, IPAddress bind, int port, string token, ExcludeMatcher ex,
        HashCache cache, Action<string> log, string? name = null)
    {
        _root = Path.GetFullPath(root);
        _bind = bind; _port = port; _ex = ex; _cache = cache; _log = log;
        _token = Encoding.UTF8.GetBytes(token);
        _name = name ?? Environment.MachineName;
    }

    public void Start()
    {
        if (!Directory.Exists(_root)) throw new DirectoryNotFoundException(Tr("sync.srv.dir_missing", _root));
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(_bind, _port);
        _listener.Start(64);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _log(Tr("sync.srv.log_listen", _bind, Port, _root));
        _ = Task.Run(AcceptLoop);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cache.Save();
        _log(Tr("sync.srv.log_stopped"));
    }

    public void Dispose() => Stop();

    private async Task AcceptLoop()
    {
        var ct = _cts!.Token;
        var l = _listener!;
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch { break; }
            _ = Task.Run(() => HandleClient(c, ct));
        }
    }

    private async Task HandleClient(TcpClient c, CancellationToken serverCt)
    {
        Interlocked.Increment(ref _clients);
        var remote = (c.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
        try
        {
            c.NoDelay = true;
            using (c)
            using (var ns = c.GetStream())
            {
                var buf = new byte[16 * 1024];
                int len = 0;
                while (!serverCt.IsCancellationRequested)
                {
                    // --- leggi l'intestazione della richiesta (con timeout di inattività) ---
                    int end = -1;
                    using (var idle = CancellationTokenSource.CreateLinkedTokenSource(serverCt))
                    {
                        idle.CancelAfter(TimeSpan.FromSeconds(60));
                        while ((end = IndexOfHeaderEnd(buf, len)) < 0)
                        {
                            if (len >= buf.Length) return;
                            int n = await ns.ReadAsync(buf.AsMemory(len), idle.Token).ConfigureAwait(false);
                            if (n == 0) return;
                            len += n;
                        }
                    }
                    var head = Encoding.ASCII.GetString(buf, 0, end);
                    Buffer.BlockCopy(buf, end + 4, buf, 0, len - (end + 4));
                    len -= end + 4;

                    if (!await HandleRequest(ns, head, remote, serverCt).ConfigureAwait(false)) return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        catch (Exception e) { _log(Tr("sync.srv.log_conn_error", remote, e.Message)); }
        finally { Interlocked.Decrement(ref _clients); }
    }

    private static int IndexOfHeaderEnd(byte[] b, int len)
    {
        for (int i = 3; i < len; i++)
            if (b[i] == '\n' && b[i - 1] == '\r' && b[i - 2] == '\n' && b[i - 3] == '\r') return i - 3;
        return -1;
    }

    /// <returns>true se la connessione può restare aperta (keep-alive).</returns>
    private async Task<bool> HandleRequest(NetworkStream ns, string head, string remote, CancellationToken ct)
    {
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 2 || parts[0] != "GET")
        {
            await SendText(ns, 405, "Method Not Allowed", "solo GET").ConfigureAwait(false);
            return false;
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int k = lines[i].IndexOf(':');
            if (k > 0) headers[lines[i][..k].Trim()] = lines[i][(k + 1)..].Trim();
        }

        var target = parts[1];
        string route = target, query = "";
        int q = target.IndexOf('?');
        if (q >= 0) { route = target[..q]; query = target[(q + 1)..]; }

        headers.TryGetValue("X-Token", out var tok);
        if (!TokenOk(tok))
        {
            _log(Tr("sync.srv.log_rejected", remote, route));
            await SendText(ns, 401, "Unauthorized", "token non valido").ConfigureAwait(false);
            return false;
        }

        switch (route)
        {
            case "/ping":
                await SendJson(ns, new { name = _name, proto = ProtocolVersion, root = Path.GetFileName(_root) }).ConfigureAwait(false);
                return true;

            case "/manifest":
                return await ServeManifest(ns, headers, remote).ConfigureAwait(false);

            case "/hash":
            {
                var full = ResolveQuery(query);
                if (full == null || !File.Exists(full)) { await SendText(ns, 404, "Not Found", "file non trovato").ConfigureAwait(false); return true; }
                try
                {
                    var fi = new FileInfo(full);
                    var h = await _cache.GetAsync(full, fi.Length, fi.LastWriteTimeUtc.Ticks, null, ct).ConfigureAwait(false);
                    await SendJson(ns, new { sha256 = h, size = fi.Length }).ConfigureAwait(false);
                }
                catch (IOException e) { await SendText(ns, 423, "Locked", e.Message).ConfigureAwait(false); }
                return true;
            }

            case "/file":
            {
                var full = ResolveQuery(query);
                if (full == null || !File.Exists(full)) { await SendText(ns, 404, "Not Found", "file non trovato").ConfigureAwait(false); return true; }
                return await ServeFile(ns, full, headers, remote, ct).ConfigureAwait(false);
            }

            default:
                await SendText(ns, 404, "Not Found", "route sconosciuta").ConfigureAwait(false);
                return true;
        }
    }

    private bool TokenOk(string? tok)
    {
        if (string.IsNullOrEmpty(tok)) return false;
        var t = Encoding.UTF8.GetBytes(tok);
        return CryptographicOperations.FixedTimeEquals(t, _token) ;
    }

    private string? ResolveQuery(string query)
    {
        foreach (var kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int i = kv.IndexOf('=');
            if (i > 0 && kv[..i] == "path")
            {
                string rel;
                try { rel = Uri.UnescapeDataString(kv[(i + 1)..]); } catch { return null; }
                var full = PathUtil.Resolve(_root, rel);
                if (full == null) return null;
                if (_ex.IsExcluded(rel) || full.EndsWith(PathUtil.PartExtension, StringComparison.OrdinalIgnoreCase)) return null;
                return full;
            }
        }
        return null;
    }

    private async Task<bool> ServeManifest(NetworkStream ns, Dictionary<string, string> headers, string remote)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var entries = await Task.Run(() => Scanner.Scan(_root, _ex, w => _log(Tr("sync.srv.log_scan", w)))).ConfigureAwait(false);
        var man = new Manifest { Name = _name, Protocol = ProtocolVersion, Entries = entries };
        var json = JsonSerializer.SerializeToUtf8Bytes(man);
        byte[] body = json; string? enc = null;
        if (headers.TryGetValue("Accept-Encoding", out var ae) && ae.Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true)) gz.Write(json, 0, json.Length);
            body = ms.ToArray(); enc = "gzip";
        }
        await SendHead(ns, 200, "OK", body.Length, "application/json", enc == null ? null : new() { ["Content-Encoding"] = enc }).ConfigureAwait(false);
        await ns.WriteAsync(body).ConfigureAwait(false);
        Interlocked.Add(ref _bytesSent, body.Length);
        _log(Tr("sync.srv.log_manifest", remote, entries.Count, sw.ElapsedMilliseconds));
        return true;
    }

    private async Task<bool> ServeFile(NetworkStream ns, string full, Dictionary<string, string> headers, string remote, CancellationToken ct)
    {
        FileStream fs;
        try { fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { await SendText(ns, 423, "Locked", e.Message).ConfigureAwait(false); return true; }

        using (fs)
        {
            long total = fs.Length, start = 0, endIncl = total - 1;
            bool partial = false;
            if (headers.TryGetValue("Range", out var range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                var spec = range[6..].Split('-');
                if (spec.Length == 2 && long.TryParse(spec[0], out var s))
                {
                    start = s;
                    if (long.TryParse(spec[1], out var e)) endIncl = Math.Min(e, total - 1);
                    partial = true;
                }
            }
            if (start > total || (start == total && total > 0))
            {
                await SendHead(ns, 416, "Range Not Satisfiable", 0, "text/plain", new() { ["Content-Range"] = $"bytes */{total}" }).ConfigureAwait(false);
                return true;
            }
            long count = Math.Max(0, endIncl - start + 1);
            if (partial)
                await SendHead(ns, 206, "Partial Content", count, "application/octet-stream",
                    new() { ["Content-Range"] = $"bytes {start}-{endIncl}/{total}" }).ConfigureAwait(false);
            else
                await SendHead(ns, 200, "OK", count, "application/octet-stream", null).ConfigureAwait(false);

            var rel = PathUtil.ToRel(_root, full);
            _log(partial && start > 0
                ? Tr("sync.srv.log_send_resume", remote, rel, PathUtil.FormatBytes(count), PathUtil.FormatBytes(start))
                : Tr("sync.srv.log_send", remote, rel, PathUtil.FormatBytes(count)));

            fs.Seek(start, SeekOrigin.Begin);
            var buf = ArrayPool<byte>.Shared.Rent(1 << 20);
            try
            {
                long left = count;
                while (left > 0)
                {
                    int n = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, left)), ct).ConfigureAwait(false);
                    if (n <= 0) return false; // file accorciato durante l'invio: chiudo la connessione
                    await ns.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    Interlocked.Add(ref _bytesSent, n);
                    left -= n;
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }
        return true;
    }

    // ------------------------------------------------------------------ helper HTTP
    private static async Task SendHead(NetworkStream ns, int code, string reason, long length, string ctype, Dictionary<string, string>? extra)
    {
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {code} {reason}\r\n");
        sb.Append($"Content-Type: {ctype}\r\nContent-Length: {length}\r\nAccept-Ranges: bytes\r\nConnection: keep-alive\r\n");
        if (extra != null) foreach (var kv in extra) sb.Append($"{kv.Key}: {kv.Value}\r\n");
        sb.Append("\r\n");
        var b = Encoding.ASCII.GetBytes(sb.ToString());
        await ns.WriteAsync(b).ConfigureAwait(false);
    }

    private static async Task SendText(NetworkStream ns, int code, string reason, string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        await SendHead(ns, code, reason, body.Length, "text/plain; charset=utf-8", null).ConfigureAwait(false);
        await ns.WriteAsync(body).ConfigureAwait(false);
    }

    private static async Task SendJson(NetworkStream ns, object o)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(o);
        await SendHead(ns, 200, "OK", body.Length, "application/json", null).ConfigureAwait(false);
        await ns.WriteAsync(body).ConfigureAwait(false);
    }
}
