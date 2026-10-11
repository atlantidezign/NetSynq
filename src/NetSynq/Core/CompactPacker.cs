using static NetSynq.Core.I18n;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetSynq.Core;

/// <summary>
/// Crea gli zip e cancella le cartelle originali SOLO dopo aver verificato lo zip.
/// Sequenza per ogni cartella: fotografia → zip temporaneo (.nstmp) con hash dei sorgenti → riapertura e verifica
/// degli hash → nuova fotografia (se qualcosa è cambiato: stop, nulla viene cancellato) → rinomina in .zip
/// (mai sovrascrivendo) → cancellazione dei soli file archiviati, poi delle cartelle rimaste vuote.
/// </summary>
public static class CompactPacker
{
    public const string ManifestName = ".netsynq-manifest.json";
    public const string TempExtension = ".nstmp";

    /// <summary>
    /// Dimensione massima di un singolo file zip: oltre questo valore lo zip viene diviso in volumi
    /// (nome.zip.001, nome.zip.002…). 4 GB meno un byte = limite dei file su FAT32. Valore fisso, non è un'impostazione:
    /// è modificabile solo perché i test possano provarlo con file piccoli.
    /// </summary>
    public static long VolumeBytes { get; set; } = 0xFFFFFFFFL;

    private static readonly HashSet<string> Incompressible = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".gz", ".bz2", ".xz", ".zst", ".cab",
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".avif",
        ".mp3", ".aac", ".ogg", ".flac", ".m4a", ".mp4", ".mkv", ".avi", ".mov", ".webm", ".wmv",
        ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".pdf", ".jar", ".apk", ".nupkg"
    };

    internal static CompressionLevel LevelFor(CompactCompression mode, string fileName) => mode switch
    {
        CompactCompression.None => CompressionLevel.NoCompression,
        CompactCompression.Max => CompressionLevel.SmallestSize,
        _ => Incompressible.Contains(Path.GetExtension(fileName)) ? CompressionLevel.NoCompression : CompressionLevel.Fastest
    };

    public static string UniqueZipPath(string parentDir, string folderName)
    {
        var p = Path.Combine(parentDir, folderName + ".zip");
        for (int i = 2; File.Exists(p) || Directory.Exists(p) || File.Exists(p + TempExtension) || File.Exists(ZipVolumes.VolumeName(p, 1)); i++)
            p = Path.Combine(parentDir, $"{folderName} ({i}).zip");
        return p;
    }

    internal static DateTimeOffset ZipTime(long ticks)
    {
        var d = new DateTime(Math.Clamp(ticks, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), DateTimeKind.Utc).ToLocalTime();
        if (d.Year < 1980) d = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
        if (d.Year > 2107) d = new DateTime(2107, 12, 31, 0, 0, 0, DateTimeKind.Local);
        return new DateTimeOffset(d);
    }

    // ------------------------------------------------------------------ esecuzione
    public static CompactResult Pack(CompactPlan plan, IProgress<ProgressInfo>? progress, Action<string> log, CancellationToken ct)
    {
        var res = new CompactResult();
        var ex = new ExcludeMatcher(plan.Options.Excludes);
        var items = plan.SelectedCandidates.ToList();
        int n = 0;
        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) { res.Cancelled = true; break; }
            n++;
            progress?.Report(new ProgressInfo { Phase = Tr("cmp.pack.phase_pack", n, items.Count), Current = item.Path, FilesDone = n - 1, FilesTotal = items.Count });
            var r = new CompactItemResult { Path = item.Path };
            try { PackOne(item, plan.Options, ex, r, progress, log, ct); }
            catch (OperationCanceledException) { r.Ok = false; r.Message = Tr("cmp.pack.msg_cancelled"); res.Cancelled = true; res.Items.Add(r); break; }
            catch (Exception e) { r.Ok = false; r.Message = Tr("cmp.pack.msg_intact", e.Message); }
            log(r.Ok ? (r.Volumes > 1
                         ? Tr("cmp.pack.log_ok_vols", item.Path, Path.GetFileName(r.ZipPath), r.Volumes - 1, r.Files, PathUtil.FormatBytes(r.BytesBefore), PathUtil.FormatBytes(r.ZipBytes))
                         : Tr("cmp.pack.log_ok", item.Path, Path.GetFileName(r.ZipPath), r.Files, PathUtil.FormatBytes(r.BytesBefore), PathUtil.FormatBytes(r.ZipBytes)))
                     : Tr("cmp.pack.log_err", item.Path, r.Message));
            if (r.Ok && r.Message.Length > 0) log("   " + r.Message);
            res.Items.Add(r);
        }
        return res;
    }

    private static void PackOne(CompactItem item, CompactOptions opt, ExcludeMatcher ex, CompactItemResult r,
        IProgress<ProgressInfo>? progress, Action<string> log, CancellationToken ct)
    {
        var dir = item.FullPath;
        if (!Directory.Exists(dir)) throw new IOException(Tr("cmp.pack.err_not_found"));
        bool partial = item.Kind == CompactKind.SmallFiles;
        Subtree snap;
        if (partial)
        {
            snap = CompactAnalyzer.SmallFiles(dir, item.Path, opt, ex);
            if (snap.Files.Count <= opt.MinSmallFiles) throw new IOException(Tr("cmp.pack.err_small_threshold"));
        }
        else
        {
            snap = CompactAnalyzer.Walk(dir, item.Path, ex, ct);
            var problem = snap.Problem(item.Forced ? opt.WithoutSizeLimits() : opt);
            if (problem != null) throw new IOException(Tr("cmp.pack.err_not_eligible", problem));
        }

        var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!;
        var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var prefix = partial ? "" : name + "/";
        var zipPath = partial ? UniqueZipPath(dir, CompactItem.SmallZipBase) : UniqueZipPath(parent, name);
        var tmp = zipPath + TempExtension;

        // spazio: nel caso peggiore lo zip è grande come i sorgenti (e se va diviso in volumi serve ancora altrettanto, vedi sotto)
        void EnsureSpace(long need)
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(parent))!);
                if (drive.AvailableFreeSpace < need)
                    throw new IOException(Tr("cmp.pack.err_space", PathUtil.FormatBytes(drive.AvailableFreeSpace), PathUtil.FormatBytes(need)));
            }
            catch (IOException) { throw; }
            catch { /* percorsi di rete senza info sullo spazio: si prosegue, eventuali errori di scrittura fermano tutto */ }
        }
        EnsureSpace(snap.TotalBytes + 32L * 1024 * 1024);

        var manifest = new ZipManifest
        {
            Root = name, Partial = partial,
            RootMTimeTicks = Directory.GetLastWriteTimeUtc(dir).Ticks,
            RootAttr = (int)(File.GetAttributes(dir) & PathUtil.ReplicatedAttrs),
            CreatedUtc = DateTime.UtcNow.ToString("o")
        };

        bool moved = false;
        var volTmps = new List<string>();    // volumi temporanei (solo se lo zip supera VolumeBytes)
        var volFinals = new List<string>();  // volumi rinominati in modo definitivo
        try
        {
            // 1) scrittura
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
            {
                if (!partial) zip.CreateEntry(prefix, CompressionLevel.NoCompression).LastWriteTime = ZipTime(manifest.RootMTimeTicks);
                foreach (var d in snap.Dirs.OrderBy(d => d.Rel, StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    var e = zip.CreateEntry(prefix + d.Rel + "/", CompressionLevel.NoCompression);
                    e.LastWriteTime = ZipTime(d.MTimeTicks);
                    manifest.Dirs.Add(new ZipManifestDir { Path = d.Rel, MTimeTicks = d.MTimeTicks, Attr = d.Attr });
                }
                var buf = new byte[1 << 16];
                foreach (var f in snap.Files.OrderBy(f => f.Rel, StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    var e = zip.CreateEntry(prefix + f.Rel, LevelFor(opt.Compression, f.Rel));
                    e.LastWriteTime = ZipTime(f.MTimeTicks);
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long total = 0;
                    using (var src = new FileStream(f.Full, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                    using (var dst = e.Open())
                    {
                        int k;
                        while ((k = src.Read(buf, 0, buf.Length)) > 0)
                        {
                            dst.Write(buf, 0, k);
                            sha.AppendData(buf, 0, k);
                            total += k;
                        }
                    }
                    if (total != f.Size) throw new IOException(Tr("cmp.pack.err_file_changed_read", f.Rel));
                    manifest.Files.Add(new ZipManifestFile { Path = f.Rel, Size = f.Size, MTimeTicks = f.MTimeTicks, Attr = f.Attr, Sha256 = Convert.ToHexString(sha.GetHashAndReset()) });
                }
                var me = zip.CreateEntry(ManifestName, CompressionLevel.Fastest);
                using (var ms = me.Open()) JsonSerializer.Serialize(ms, manifest);
            }

            // 2) se lo zip supera il limite di un volume lo si divide in volumi a byte (nome.zip.001, .002…),
            //    poi si rilegge da disco (dai volumi, se divisi) e si confrontano gli hash
            long zipLen = new FileInfo(tmp).Length;
            if (zipLen > VolumeBytes)
            {
                progress?.Report(new ProgressInfo { Phase = Tr("cmp.pack.phase_split"), Current = item.Path });
                EnsureSpace(zipLen + 32L * 1024 * 1024);
                volTmps.AddRange(SplitFile(tmp, i => ZipVolumes.VolumeName(zipPath, i) + TempExtension, VolumeBytes, ct));
                File.Delete(tmp);
                progress?.Report(new ProgressInfo { Phase = Tr("cmp.pack.phase_verify"), Current = item.Path });
                using var vs = new VolumeReadStream(volTmps);
                Verify(vs, name, manifest, ct);
            }
            else
            {
                progress?.Report(new ProgressInfo { Phase = Tr("cmp.pack.phase_verify"), Current = item.Path });
                Verify(tmp, name, manifest, ct);
            }

            // 3) i sorgenti sono ancora identici a quelli archiviati?
            var after = partial ? CompactAnalyzer.SmallFiles(dir, item.Path, opt, ex) : CompactAnalyzer.Walk(dir, item.Path, ex, ct);
            var diff = Differences(snap, after);
            if (diff != null) throw new IOException(Tr("cmp.pack.err_folder_changed", diff));

            // 4) rinomina definitiva (senza sovrascrivere)
            if (volTmps.Count > 0)
            {
                for (int i = 0; i < volTmps.Count; i++)
                {
                    var fin = ZipVolumes.VolumeName(zipPath, i + 1);
                    File.Move(volTmps[i], fin);
                    volFinals.Add(fin);
                }
            }
            else File.Move(tmp, zipPath);
            moved = true;
        }
        finally
        {
            if (!moved)
            {
                try { File.Delete(tmp); } catch { }
                foreach (var p in volTmps.Concat(volFinals)) { try { File.Delete(p); } catch { } }
            }
        }

        var parts = volFinals.Count > 0 ? volFinals : new List<string> { zipPath };
        r.ZipPath = parts[0];
        r.Volumes = parts.Count;
        r.Files = snap.Files.Count;
        r.Dirs = snap.Dirs.Count;
        r.BytesBefore = snap.TotalBytes;
        r.ZipBytes = parts.Sum(p => new FileInfo(p).Length);
        foreach (var p in parts) { try { File.SetLastWriteTimeUtc(p, new DateTime(manifest.RootMTimeTicks, DateTimeKind.Utc)); } catch { } }

        // 5) cancellazione: solo ciò che è nello zip e non è cambiato
        progress?.Report(new ProgressInfo { Phase = Tr("cmp.pack.phase_remove"), Current = item.Path });
        int left = 0;
        foreach (var f in snap.Files)
        {
            try
            {
                var fi = new FileInfo(f.Full);
                if (!fi.Exists) continue;
                if (fi.Length != f.Size || fi.LastWriteTimeUtc.Ticks != f.MTimeTicks) { left++; continue; }
                PathUtil.ClearReadOnly(f.Full);
                File.Delete(f.Full);
            }
            catch { left++; }
        }
        if (partial)
        {
            // la cartella resta: se ne ripristina la data di modifica originale
            try { Directory.SetLastWriteTimeUtc(dir, new DateTime(manifest.RootMTimeTicks, DateTimeKind.Utc)); } catch { }
        }
        else
        {
            foreach (var d in snap.Dirs.OrderByDescending(d => d.Rel.Length))
            {
                try { PathUtil.ClearReadOnly(d.Full); Directory.Delete(d.Full, false); } catch { left++; }
            }
            try { PathUtil.ClearReadOnly(dir); Directory.Delete(dir, false); } catch { left++; }
        }
        r.Leftover = left;
        r.Ok = true;
        r.Message = left == 0 ? "" : Tr("cmp.pack.msg_leftover", left);
    }

    /// <summary>Divide un file in volumi di al massimo <paramref name="vol"/> byte. In caso di errore cancella i volumi già scritti.</summary>
    internal static List<string> SplitFile(string src, Func<int, string> volumePath, long vol, CancellationToken ct)
    {
        var created = new List<string>();
        try
        {
            using var inp = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            var buf = new byte[1 << 20];
            long remaining = inp.Length;
            int idx = 0;
            while (remaining > 0)
            {
                var p = volumePath(++idx);
                created.Add(p);
                using var o = new FileStream(p, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
                long left = Math.Min(vol, remaining);
                remaining -= left;
                while (left > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int k = inp.Read(buf, 0, (int)Math.Min(buf.Length, left));
                    if (k <= 0) throw new IOException(Tr("cmp.pack.err_temp_incomplete"));
                    o.Write(buf, 0, k);
                    left -= k;
                }
            }
            return created;
        }
        catch
        {
            foreach (var p in created) { try { File.Delete(p); } catch { } }
            throw;
        }
    }

    internal static void Verify(string zipFile, string root, ZipManifest m, CancellationToken ct)
    {
        using var fs = new FileStream(zipFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        Verify(fs, root, m, ct);
    }

    /// <summary>Rilegge lo zip e controlla che contenga esattamente ciò che il manifest dichiara, con gli stessi hash.</summary>
    internal static void Verify(Stream stream, string root, ZipManifest m, CancellationToken ct)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        string pre = m.Partial ? "" : root + "/";
        int expected = m.Partial ? 1 + m.Files.Count : 1 + 1 + m.Dirs.Count + m.Files.Count; // [radice] + manifest + cartelle + file
        if (zip.Entries.Count != expected) throw new IOException(Tr("cmp.pack.err_zip_incoherent", zip.Entries.Count, expected));
        var buf = new byte[1 << 16];
        foreach (var f in m.Files)
        {
            ct.ThrowIfCancellationRequested();
            var e = zip.GetEntry(pre + f.Path) ?? throw new IOException(Tr("cmp.pack.err_entry_missing", f.Path));
            if (e.Length != f.Size) throw new IOException(Tr("cmp.pack.err_size_wrong", f.Path));
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var s = e.Open();
            long total = 0; int k;
            while ((k = s.Read(buf, 0, buf.Length)) > 0) { sha.AppendData(buf, 0, k); total += k; }
            if (total != f.Size || !Convert.ToHexString(sha.GetHashAndReset()).Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException(Tr("cmp.pack.err_hash", f.Path));
        }
        if (zip.GetEntry(ManifestName) == null) throw new IOException(Tr("cmp.pack.err_manifest"));
    }

    /// <summary>null = identici; altrimenti una breve descrizione della prima differenza.</summary>
    internal static string? Differences(Subtree a, Subtree b)
    {
        if (a.Files.Count != b.Files.Count) return Tr("cmp.pack.diff_files_count");
        if (a.Dirs.Count != b.Dirs.Count) return Tr("cmp.pack.diff_dirs_count");
        var bf = b.Files.ToDictionary(f => f.Rel, StringComparer.Ordinal);
        foreach (var f in a.Files)
        {
            if (!bf.TryGetValue(f.Rel, out var g)) return Tr("cmp.pack.diff_file_gone", f.Rel);
            if (g.Size != f.Size || g.MTimeTicks != f.MTimeTicks) return Tr("cmp.pack.diff_file_modified", f.Rel);
        }
        var bd = new HashSet<string>(b.Dirs.Select(d => d.Rel), StringComparer.Ordinal);
        foreach (var d in a.Dirs) if (!bd.Contains(d.Rel)) return Tr("cmp.pack.diff_dir_gone", d.Rel);
        return null;
    }

    // ------------------------------------------------------------------ simulazione
    public static CompactReport Simulate(CompactPlan plan)
    {
        var o = plan.Options;
        var rep = new CompactReport();
        var sel = plan.SelectedCandidates.ToList();
        rep.Summary.Add(Tr("cmp.pack.sim_root", plan.Root));
        rep.Summary.Add(Tr("cmp.pack.sim_criteria", o.MinSmallFiles, PathUtil.FormatBytes(o.SmallFileBytes), o.Compression));
        rep.Summary.Add(Tr("cmp.pack.sim_will_zip", sel.Count, plan.SelectedFiles, plan.SelectedDirs, PathUtil.FormatBytes(plan.SelectedBytes)));
        rep.Summary.Add(Tr("cmp.pack.sim_deselected", plan.CandidateCount - sel.Count, plan.SkippedCount));
        rep.Summary.Add(Tr("cmp.pack.sim_each"));
        rep.Summary.Add(Tr("cmp.pack.sim_volumes", PathUtil.FormatBytes(VolumeBytes + 1)));
        foreach (var i in sel)
            rep.Lines.Add(Tr(i.Kind == CompactKind.SmallFiles ? "cmp.pack.line_small" : "cmp.pack.line_zip", i.Path, i.ZipName, i.TotalFiles, i.TotalDirs, PathUtil.FormatBytes(i.TotalBytes), i.Note));
        foreach (var i in plan.Candidates.Where(c => !c.Selected && !c.IsMerged))
            rep.Lines.Add(Tr("cmp.pack.line_excluded", i.Path));
        foreach (var i in plan.Items.Where(c => c.Status == CompactStatus.Skipped))
            rep.Lines.Add(Tr("cmp.pack.line_unfit", i.Path, i.Reason));
        return rep;
    }
}
