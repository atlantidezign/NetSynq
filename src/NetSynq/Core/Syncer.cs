using static NetSynq.Core.I18n;

namespace NetSynq.Core;

/// <summary>Applica il piano: crea cartelle, scarica i file nuovi/modificati, aggiorna le date, elimina gli extra.</summary>
public static class Syncer
{
    /// <summary>Controlla lo spazio libero. Restituisce null se ok, altrimenti un messaggio d'errore.</summary>
    public static string? CheckSpace(SyncPlan plan, SyncOptions opt)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(plan.LocalRoot));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return null;
            var free = new DriveInfo(root).AvailableFreeSpace;
            long need = plan.DownloadBytes - plan.ReplacedBytes - (opt.DeleteExtra ? plan.DeleteBytes : 0) + 200L * 1024 * 1024;
            if (need > free)
                return Tr("sync.sync.space_low", root, PathUtil.FormatBytes(need), PathUtil.FormatBytes(free));
        }
        catch { /* se non riesco a verificare, non blocco */ }
        return null;
    }

    public static async Task<SyncResult> ApplyAsync(SyncPlan plan, RemoteClient rc, SyncOptions opt,
        IProgress<ProgressInfo>? progress, Action<string> log, CancellationToken ct)
    {
        var res = new SyncResult();
        var root = plan.LocalRoot;

        var downloads = plan.Items.Where(i => i.Selected && !i.IsDir && (i.Status == DiffStatus.New || i.Status == DiffStatus.Modified)).ToList();
        var touches = plan.Items.Where(i => i.Selected && !i.IsDir && i.Status == DiffStatus.TouchOnly).ToList();
        long totalBytes = downloads.Sum(i => i.Remote!.Size);
        long bytesDone = 0; long filesDone = 0;
        long filesTotal = downloads.Count;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        void Report(string? cur, bool force = false)
        {
            if (!force && sw.ElapsedMilliseconds < 150) return;
            sw.Restart();
            progress?.Report(new ProgressInfo
            {
                Phase = Tr("sync.sync.phase_copy"), Done = Interlocked.Read(ref bytesDone), Total = totalBytes,
                FilesDone = Interlocked.Read(ref filesDone), FilesTotal = filesTotal, Current = cur
            });
        }

        try
        {
            // 1) file al posto di cartelle (o viceversa): libero il posto
            foreach (var i in plan.Items.Where(x => x.Selected && x.TypeConflict))
            {
                var full = PathUtil.Resolve(root, i.Path);
                if (full == null) continue;
                try
                {
                    if (Directory.Exists(full)) PathUtil.ForceDeleteDirectory(full);
                    else if (File.Exists(full)) { PathUtil.ClearReadOnly(full); File.Delete(full); }
                }
                catch (Exception e) { res.Errors.Add($"{i.Path}: {e.Message}"); }
            }

            // 2) cartelle nuove (anche vuote), dalla più corta
            foreach (var d in plan.Items.Where(x => x.Selected && x.IsDir && (x.Status == DiffStatus.New || x.TypeConflict))
                                        .OrderBy(x => x.Path.Length))
            {
                ct.ThrowIfCancellationRequested();
                var full = PathUtil.Resolve(root, d.Path);
                if (full == null) { res.Errors.Add(Tr("sync.sync.invalid_path", d.Path)); continue; }
                try
                {
                    Directory.CreateDirectory(full);
                    ApplyAttrs(full, d.Remote!.Attr, true);
                    res.DirsCreated++;
                }
                catch (Exception e) { res.Errors.Add($"{d.Path}: {e.Message}"); }
            }

            // 3) solo data diversa: contenuto già identico
            foreach (var t in touches)
            {
                ct.ThrowIfCancellationRequested();
                var full = PathUtil.Resolve(root, t.Path);
                if (full == null) continue;
                try
                {
                    PathUtil.ClearReadOnly(full);
                    File.SetLastWriteTimeUtc(full, t.Remote!.MTimeUtc);
                    ApplyAttrs(full, t.Remote.Attr, false);
                    res.Touched++;
                }
                catch (Exception e) { res.Errors.Add($"{t.Path}: {e.Message}"); }
            }

            // 4) copia file (in parallelo)
            long copiedBytes = 0;
            int copied = 0;
            await Parallel.ForEachAsync(downloads,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(opt.Parallel, 1, 8), CancellationToken = ct },
                async (item, t) =>
                {
                    var full = PathUtil.Resolve(root, item.Path);
                    if (full == null) { lock (res) res.Errors.Add(Tr("sync.sync.invalid_path", item.Path)); return; }
                    var r = item.Remote!;
                    var part = full + "." + r.MTimeTicks.ToString("x") + PathUtil.PartExtension;
                    long counted = 0;
                    void OnBytes(long n) { counted += n; Interlocked.Add(ref bytesDone, n); Report(item.Path); }

                    for (int attempt = 1; ; attempt++)
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                            await rc.DownloadAsync(item.Path, part, r.Size, OnBytes, t).ConfigureAwait(false);
                            if (Directory.Exists(full)) PathUtil.ForceDeleteDirectory(full);
                            if (File.Exists(full)) { PathUtil.ClearReadOnly(full); }
                            File.Move(part, full, true);
                            File.SetLastWriteTimeUtc(full, r.MTimeUtc);
                            ApplyAttrs(full, r.Attr, false);
                            Interlocked.Add(ref copiedBytes, r.Size);
                            Interlocked.Increment(ref copied);
                            break;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception e) when (attempt < 3 && e is not UnauthorizedAccessException && e is not FileNotFoundException)
                        {
                            Interlocked.Add(ref bytesDone, -counted); counted = 0; // il retry riparte dal parziale
                            log(Tr("sync.sync.log_retry", item.Path, e.Message, attempt + 1));
                            await Task.Delay(1000 * attempt, t).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            lock (res) res.Errors.Add($"{item.Path}: {e.Message}");
                            log(Tr("sync.sync.log_error", item.Path, e.Message));
                            break;
                        }
                    }
                    Interlocked.Increment(ref filesDone);
                    Report(item.Path);
                }).ConfigureAwait(false);
            res.Copied = copied; res.BytesCopied = copiedBytes;
            Report(null, true);

            // 5) eliminazioni
            if (opt.DeleteExtra)
            {
                progress?.Report(new ProgressInfo { Phase = Tr("sync.sync.phase_delete") });
                foreach (var d in plan.Items.Where(x => x.Selected && !x.IsDir && x.Status == DiffStatus.Delete))
                {
                    ct.ThrowIfCancellationRequested();
                    var full = PathUtil.Resolve(root, d.Path);
                    if (full == null) continue;
                    try { PathUtil.ClearReadOnly(full); File.Delete(full); res.Deleted++; }
                    catch (Exception e) { res.Errors.Add($"{d.Path}: {e.Message}"); }
                }
                foreach (var d in plan.Items.Where(x => x.Selected && x.IsDir && x.Status == DiffStatus.Delete)
                                            .OrderByDescending(x => x.Path.Length))
                {
                    var full = PathUtil.Resolve(root, d.Path);
                    if (full == null || !Directory.Exists(full)) continue;
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(full).Any())
                        { PathUtil.ClearReadOnly(full); Directory.Delete(full); res.DirsDeleted++; }
                        else log(Tr("sync.sync.log_dir_notempty", d.Path));
                    }
                    catch (Exception e) { res.Errors.Add($"{d.Path}: {e.Message}"); }
                }
            }

            // 6) pulizia dei parziali di vecchi trasferimenti interrotti
            if (res.Errors.Count == 0)
                foreach (var p in plan.StaleParts) { try { File.Delete(p); } catch { } }
        }
        catch (OperationCanceledException)
        {
            res.Cancelled = true;
            res.Copied = 0; // i contatori parziali non sono affidabili
        }
        return res;
    }

    private static void ApplyAttrs(string path, int attr, bool dir)
    {
        try
        {
            var a = (FileAttributes)attr & PathUtil.ReplicatedAttrs;
            var cur = File.GetAttributes(path);
            var keep = cur & ~PathUtil.ReplicatedAttrs;
            var target = keep | a;
            if (dir && target == 0) target = FileAttributes.Directory;
            if (!dir && target == 0) target = FileAttributes.Normal;
            if (target != cur) File.SetAttributes(path, target);
        }
        catch { /* attributi non critici */ }
    }
}
