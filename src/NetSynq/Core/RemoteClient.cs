using System.Net;
using System.Text.Json;
using static NetSynq.Core.I18n;

namespace NetSynq.Core;

/// <summary>Client HTTP verso il SyncServer del master.</summary>
public sealed class RemoteClient : IDisposable
{
    private readonly HttpClient _http;

    public RemoteClient(string host, int port, string token)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip,
            MaxConnectionsPerServer = 16,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30)
        };
        _http = new HttpClient(handler, true)
        {
            BaseAddress = new Uri($"http://{host}:{port}"),
            Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.Add("X-Token", token);
    }

    public void Dispose() => _http.Dispose();

    private static string Q(string path) => "?path=" + Uri.EscapeDataString(path);

    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct, HttpCompletionOption opt = HttpCompletionOption.ResponseContentRead)
    {
        var r = await _http.GetAsync(url, opt, ct).ConfigureAwait(false);
        if (r.StatusCode == HttpStatusCode.Unauthorized)
        {
            r.Dispose();
            throw new UnauthorizedAccessException(Tr("sync.client.token_invalid_dot"));
        }
        return r;
    }

    /// <summary>Verifica la connessione; restituisce il nome del PC master.</summary>
    public async Task<string> PingAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        using var r = await GetAsync("/ping", cts.Token).ConfigureAwait(false);
        r.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
        return doc.RootElement.GetProperty("name").GetString() ?? "?";
    }

    public async Task<Manifest> GetManifestAsync(CancellationToken ct)
    {
        using var r = await GetAsync("/manifest", ct).ConfigureAwait(false);
        r.EnsureSuccessStatusCode();
        await using var s = await r.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var m = await JsonSerializer.DeserializeAsync<Manifest>(s, cancellationToken: ct).ConfigureAwait(false);
        return m ?? throw new InvalidDataException(Tr("sync.client.manifest_invalid"));
    }

    public async Task<string> GetHashAsync(string path, CancellationToken ct)
    {
        using var r = await GetAsync("/hash" + Q(path), ct).ConfigureAwait(false);
        r.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.GetProperty("sha256").GetString() ?? "";
    }

    /// <summary>
    /// Scarica un file in <paramref name="partFile"/>. Se il file parziale esiste, riprende da dove era arrivato.
    /// <paramref name="onBytes"/> riceve anche i byte già presenti (così l'avanzamento resta coerente).
    /// </summary>
    public async Task DownloadAsync(string path, string partFile, long expectedSize, Action<long>? onBytes, CancellationToken ct)
    {
        long existing = 0;
        if (File.Exists(partFile))
        {
            existing = new FileInfo(partFile).Length;
            if (existing > expectedSize) { File.Delete(partFile); existing = 0; }
        }

        if (existing < expectedSize || expectedSize == 0)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/file" + Q(path));
            if (existing > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
            using var r = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (r.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException(Tr("sync.client.token_invalid"));
            if (r.StatusCode == HttpStatusCode.NotFound) throw new FileNotFoundException(Tr("sync.client.file_gone", path));
            r.EnsureSuccessStatusCode();

            bool resumed = r.StatusCode == HttpStatusCode.PartialContent && existing > 0;
            if (!resumed) existing = 0;

            await using var src = await r.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(partFile, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write,
                FileShare.None, 1 << 16, true);
            if (existing > 0) onBytes?.Invoke(existing);
            var buf = new byte[256 * 1024];
            int n;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                onBytes?.Invoke(n);
            }
        }
        else onBytes?.Invoke(existing);

        var len = new FileInfo(partFile).Length;
        if (len != expectedSize)
            throw new IOException(Tr("sync.client.size_mismatch", path, len, expectedSize));
    }
}
