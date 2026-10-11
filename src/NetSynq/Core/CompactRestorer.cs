using static NetSynq.Core.I18n;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace NetSynq.Core;

public sealed class RestoreItem
{
    public string ZipPath { get; set; } = "";
    public string RelZip { get; set; } = "";
    public string Target { get; set; } = "";
    public int Files { get; set; }
    public int Dirs { get; set; }
    public long Bytes { get; set; }
    public bool TargetExists { get; set; }
    public bool Partial { get; set; }
    /// <summary>Numero di volumi dello zip (1 = zip unico).</summary>
    public int Volumes { get; set; } = 1;
    public bool Selected { get; set; } = true;
    public ZipManifest Manifest { get; set; } = new();
}

public sealed class RestoreResult
{
    public List<(string Zip, bool Ok, string Message)> Items { get; } = new();
    public bool Cancelled { get; set; }
    public int Restored => Items.Count(i => i.Ok);
    public int Failed => Items.Count(i => !i.Ok);
}

/// <summary>Estrae gli zip creati da NetSynq riportando cartelle, date e attributi come prima.</summary>
public static class CompactRestorer
{
    public static List<RestoreItem> Analyze(string root, CancellationToken ct, Action<string> log)
    {
        var list = new List<RestoreItem>();
        root = Path.GetFullPath(root);
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            IEnumerable<FileSystemInfo> children;
            try { children = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
            catch (Exception e) { log(Tr("cmp.restore.log_unreadable", dir, e.Message)); continue; }
            foreach (var c in children)
            {
                if ((c.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((c.Attributes & FileAttributes.Directory) != 0) { stack.Push(c.FullName); continue; }
                bool first = ZipVolumes.IsFirstVolume(c.Name);
                if (!first && !c.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                var m = ReadManifest(c.FullName);
                if (m == null)
                {
                    if (first) log(Tr("cmp.restore.log_volumes_bad", c.Name));
                    continue;
                }
                int vols = first ? ZipVolumes.Find(c.FullName).Count : 1;
                var parent = Path.GetDirectoryName(c.FullName)!;
                if (m.Partial)
                {
                    bool conflict = m.Files.Any(f => PathUtil.Resolve(parent, f.Path) is not { } q || File.Exists(q) || Directory.Exists(q));
                    list.Add(new RestoreItem
                    {
                        ZipPath = c.FullName, RelZip = PathUtil.ToRel(root, c.FullName), Target = parent, Partial = true,
                        Files = m.Files.Count, Dirs = 0, Bytes = m.Files.Sum(f => f.Size), TargetExists = conflict, Manifest = m, Volumes = vols
                    });
                    continue;
                }
                var target = PathUtil.Resolve(parent, m.Root);
                if (target == null || m.Root.Contains('/') || m.Root.Contains('\\')) { log(Tr("cmp.restore.log_bad_name", c.Name)); continue; }
                list.Add(new RestoreItem
                {
                    ZipPath = c.FullName, RelZip = PathUtil.ToRel(root, c.FullName), Target = target,
                    Files = m.Files.Count, Dirs = m.Dirs.Count, Bytes = m.Files.Sum(f => f.Size),
                    TargetExists = Directory.Exists(target) || File.Exists(target), Manifest = m, Volumes = vols
                });
            }
        }
        list.Sort((a, b) => string.Compare(a.RelZip, b.RelZip, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    public static ZipManifest? ReadManifest(string zipFile)
    {
        try
        {
            using var fs = ZipVolumes.OpenRead(zipFile);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            var e = zip.GetEntry(CompactPacker.ManifestName);
            if (e == null) return null;
            using var s = e.Open();
            var m = JsonSerializer.Deserialize<ZipManifest>(s);
            return m is { Tool: "NetSynq" } && m.Root.Length > 0 ? m : null;
        }
        catch { return null; }
    }

    public static RestoreResult Restore(IEnumerable<RestoreItem> items, bool deleteZip, IProgress<ProgressInfo>? progress,
        Action<string> log, CancellationToken ct)
    {
        var res = new RestoreResult();
        var list = items.Where(i => i.Selected).ToList();
        int n = 0;
        foreach (var it in list)
        {
            if (ct.IsCancellationRequested) { res.Cancelled = true; break; }
            n++;
            progress?.Report(new ProgressInfo { Phase = Tr("cmp.restore.phase", n, list.Count), Current = it.RelZip, FilesDone = n - 1, FilesTotal = list.Count });
            try
            {
                var msg = RestoreOne(it, deleteZip, ct);
                res.Items.Add((it.RelZip, true, msg));
                log(msg.Length > 0 ? Tr("cmp.restore.log_ok_msg", it.RelZip, Path.GetFileName(it.Target), msg) : Tr("cmp.restore.log_ok", it.RelZip, Path.GetFileName(it.Target)));
            }
            catch (OperationCanceledException) { res.Items.Add((it.RelZip, false, Tr("cmp.restore.msg_cancelled"))); res.Cancelled = true; break; }
            catch (Exception e)
            {
                res.Items.Add((it.RelZip, false, e.Message));
                log(Tr("cmp.restore.log_err", it.RelZip, e.Message));
            }
        }
        return res;
    }

    private static string RestoreOne(RestoreItem it, bool deleteZip, CancellationToken ct)
    {
        var m = it.Manifest;
        if (it.Partial) return RestoreSmall(it, deleteZip, ct);
        var parent = Path.GetDirectoryName(it.Target)!;
        if (Directory.Exists(it.Target) || File.Exists(it.Target))
            throw new IOException(Tr("cmp.restore.msg_exists", Path.GetFileName(it.Target)));

        var tmp = Path.Combine(parent, "." + m.Root + ".nsrestore-" + Guid.NewGuid().ToString("N")[..8]);
        bool done = false;
        bool zipComplete;
        try
        {
            Directory.CreateDirectory(tmp);
            using (var fs = ZipVolumes.OpenRead(it.ZipPath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                // voci dello zip non descritte dal manifest: lo zip non va cancellato
                var known = new HashSet<string>(StringComparer.Ordinal) { CompactPacker.ManifestName, m.Root + "/" };
                foreach (var d in m.Dirs) known.Add(m.Root + "/" + d.Path + "/");
                foreach (var f in m.Files) known.Add(m.Root + "/" + f.Path);
                zipComplete = zip.Entries.All(e => known.Contains(e.FullName));

                foreach (var d in m.Dirs)
                {
                    var p = PathUtil.Resolve(tmp, d.Path) ?? throw new IOException(Tr("cmp.restore.err_bad_path", d.Path));
                    Directory.CreateDirectory(p);
                }
                var buf = new byte[1 << 16];
                foreach (var f in m.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    var p = PathUtil.Resolve(tmp, f.Path) ?? throw new IOException(Tr("cmp.restore.err_bad_path", f.Path));
                    var e = zip.GetEntry(m.Root + "/" + f.Path) ?? throw new IOException(Tr("cmp.restore.err_entry_missing", f.Path));
                    Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long total = 0;
                    using (var src = e.Open())
                    using (var dst = new FileStream(p, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                    {
                        int k;
                        while ((k = src.Read(buf, 0, buf.Length)) > 0) { dst.Write(buf, 0, k); sha.AppendData(buf, 0, k); total += k; }
                    }
                    if (total != f.Size || !Convert.ToHexString(sha.GetHashAndReset()).Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException(Tr("cmp.restore.err_hash", f.Path));
                }
            }

            // date e attributi: file, poi cartelle dal basso, infine la radice
            foreach (var f in m.Files)
            {
                var p = PathUtil.Resolve(tmp, f.Path)!;
                File.SetLastWriteTimeUtc(p, new DateTime(f.MTimeTicks, DateTimeKind.Utc));
                if (f.Attr != 0) File.SetAttributes(p, (FileAttributes)f.Attr);
            }
            foreach (var d in m.Dirs.OrderByDescending(d => d.Path.Length))
            {
                var p = PathUtil.Resolve(tmp, d.Path)!;
                Directory.SetLastWriteTimeUtc(p, new DateTime(d.MTimeTicks, DateTimeKind.Utc));
                if (d.Attr != 0) File.SetAttributes(p, (FileAttributes)d.Attr);
            }
            Directory.SetLastWriteTimeUtc(tmp, new DateTime(m.RootMTimeTicks, DateTimeKind.Utc));

            Directory.Move(tmp, it.Target);
            done = true;
            if (m.RootAttr != 0) { try { File.SetAttributes(it.Target, (FileAttributes)m.RootAttr); } catch { } }
        }
        finally
        {
            if (!done) { try { PathUtil.ForceDeleteDirectory(tmp); } catch { } }
        }

        if (!deleteZip) return Tr("cmp.restore.msg_zip_kept");
        if (!zipComplete) return Tr("cmp.restore.msg_zip_kept_extra");
        try { ZipVolumes.Delete(it.ZipPath); return ""; }
        catch (Exception e) { return Tr("cmp.restore.msg_zip_not_deleted", e.Message); }
    }

    private static string RestoreSmall(RestoreItem it, bool deleteZip, CancellationToken ct)
    {
        var m = it.Manifest;
        var dir = it.Target;
        if (!Directory.Exists(dir)) throw new IOException(Tr("cmp.restore.err_target_gone"));
        foreach (var f in m.Files)
        {
            var q = PathUtil.Resolve(dir, f.Path) ?? throw new IOException(Tr("cmp.restore.err_bad_path", f.Path));
            if (File.Exists(q) || Directory.Exists(q)) throw new IOException(Tr("cmp.restore.msg_exists", f.Path));
        }
        var tmp = Path.Combine(dir, ".nsrestore-" + Guid.NewGuid().ToString("N")[..8]);
        bool zipComplete;
        try
        {
            Directory.CreateDirectory(tmp);
            using (var fs = ZipVolumes.OpenRead(it.ZipPath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                var known = new HashSet<string>(StringComparer.Ordinal) { CompactPacker.ManifestName };
                foreach (var f in m.Files) known.Add(f.Path);
                zipComplete = zip.Entries.All(e => known.Contains(e.FullName));
                var buf = new byte[1 << 16];
                foreach (var f in m.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    var p = PathUtil.Resolve(tmp, f.Path) ?? throw new IOException(Tr("cmp.restore.err_bad_path", f.Path));
                    var e = zip.GetEntry(f.Path) ?? throw new IOException(Tr("cmp.restore.err_entry_missing", f.Path));
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long total = 0;
                    using (var src = e.Open())
                    using (var dst = new FileStream(p, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                    {
                        int k;
                        while ((k = src.Read(buf, 0, buf.Length)) > 0) { dst.Write(buf, 0, k); sha.AppendData(buf, 0, k); total += k; }
                    }
                    if (total != f.Size || !Convert.ToHexString(sha.GetHashAndReset()).Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException(Tr("cmp.restore.err_hash", f.Path));
                    File.SetLastWriteTimeUtc(p, new DateTime(f.MTimeTicks, DateTimeKind.Utc));
                }
            }
            foreach (var f in m.Files)
            {
                var from = PathUtil.Resolve(tmp, f.Path)!;
                if (f.Attr != 0) File.SetAttributes(from, (FileAttributes)f.Attr);
                File.Move(from, PathUtil.Resolve(dir, f.Path)!); // non sovrascrive
            }
        }
        finally { try { PathUtil.ForceDeleteDirectory(tmp); } catch { } }
        try { Directory.SetLastWriteTimeUtc(dir, new DateTime(m.RootMTimeTicks, DateTimeKind.Utc)); } catch { }

        if (!deleteZip) return Tr("cmp.restore.msg_zip_kept");
        if (!zipComplete) return Tr("cmp.restore.msg_zip_kept_extra");
        try { ZipVolumes.Delete(it.ZipPath); return ""; }
        catch (Exception e) { return Tr("cmp.restore.msg_zip_not_deleted", e.Message); }
    }

    public static CompactReport Simulate(IEnumerable<RestoreItem> items)
    {
        var rep = new CompactReport();
        var all = items.ToList();
        var sel = all.Where(i => i.Selected).ToList();
        rep.Summary.Add(Tr("cmp.restore.sim_found", all.Count, sel.Count(i => !i.TargetExists), sel.Count(i => i.TargetExists)));
        rep.Summary.Add(Tr("cmp.restore.sim_each"));
        foreach (var i in all)
            rep.Lines.Add(Tr(!i.Selected ? "cmp.restore.tag_excluded" : i.TargetExists ? "cmp.restore.tag_skipped" : "cmp.restore.tag_restore")
                + Tr("cmp.restore.line", i.RelZip, Path.GetFileName(i.Target), i.Files, PathUtil.FormatBytes(i.Bytes))
                + (i.TargetExists ? Tr("cmp.restore.tag_exists") : "") + (i.Volumes > 1 ? Tr("cmp.restore.tag_volumes", i.Volumes) : "") + (i.Partial ? Tr("cmp.restore.tag_partial") : ""));
        return rep;
    }
}
