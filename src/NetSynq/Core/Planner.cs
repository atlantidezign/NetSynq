using static NetSynq.Core.I18n;

namespace NetSynq.Core;

/// <summary>Confronta master e slave e produce l'elenco delle differenze (senza toccare nulla).</summary>
public static class Planner
{
    public static async Task<SyncPlan> BuildAsync(RemoteClient rc, string localRoot, CompareMode mode,
        ExcludeMatcher ex, HashCache localCache, IProgress<ProgressInfo>? progress, Action<string> log,
        CancellationToken ct)
    {
        if (!Directory.Exists(localRoot)) throw new DirectoryNotFoundException(Tr("sync.plan.local_missing", localRoot));

        progress?.Report(new ProgressInfo { Phase = Tr("sync.plan.phase_manifest") });
        var manifest = await rc.GetManifestAsync(ct).ConfigureAwait(false);
        var remote = manifest.Entries.Where(e => !ex.IsExcluded(e.Path)).ToList();
        log(Tr("sync.plan.log_master", manifest.Name, remote.Count(e => !e.IsDir), remote.Count(e => e.IsDir)));

        progress?.Report(new ProgressInfo { Phase = Tr("sync.plan.phase_scan") });
        var stale = new List<string>();
        var local = await Task.Run(() => Scanner.Scan(localRoot, ex, w => log(Tr("sync.plan.log_scan_warn", w)), stale,
            new Progress<int>(n => progress?.Report(new ProgressInfo { Phase = Tr("sync.plan.phase_scan_n", n) })), ct), ct)
            .ConfigureAwait(false);
        log(Tr("sync.plan.log_slave", local.Count(e => !e.IsDir), local.Count(e => e.IsDir)));

        if (remote.Count == 0 && local.Count > 0)
            throw new InvalidOperationException(Tr("sync.plan.master_empty"));

        var plan = new SyncPlan { LocalRoot = localRoot, Mode = mode, StaleParts = stale };
        var lmap = new Dictionary<string, Entry>(PathUtil.PathComparer);
        foreach (var l in local) lmap[l.Path] = l;
        var rmap = new HashSet<string>(PathUtil.PathComparer);
        var needHash = new List<DiffItem>();

        foreach (var r in remote)
        {
            rmap.Add(r.Path);
            lmap.TryGetValue(r.Path, out var l);
            var item = new DiffItem { Path = r.Path, IsDir = r.IsDir, Remote = r, Local = l };

            if (l == null) item.Status = DiffStatus.New;
            else if (l.IsDir != r.IsDir)
            {
                item.Status = DiffStatus.Modified; item.TypeConflict = true;
                item.Note = r.IsDir ? Tr("sync.plan.note_dir_vs_file") : Tr("sync.plan.note_file_vs_dir");
            }
            else if (r.IsDir) item.Status = DiffStatus.Same;
            else if (l.Size != r.Size) item.Status = DiffStatus.Modified;
            else
            {
                bool sameTime = PathUtil.SameTime(l.MTimeTicks, r.MTimeTicks);
                switch (mode)
                {
                    case CompareMode.Fast:
                        item.Status = sameTime ? DiffStatus.Same : DiffStatus.Modified;
                        if (!sameTime) item.Note = Tr("sync.plan.note_date_diff");
                        break;
                    case CompareMode.Smart:
                        if (sameTime) item.Status = DiffStatus.Same;
                        else { item.Status = DiffStatus.Modified; needHash.Add(item); }
                        break;
                    default: // Full
                        item.Status = DiffStatus.Same; needHash.Add(item);
                        break;
                }
            }
            plan.Items.Add(item);
        }

        foreach (var l in local)
            if (!rmap.Contains(l.Path))
                plan.Items.Add(new DiffItem { Path = l.Path, IsDir = l.IsDir, Local = l, Status = DiffStatus.Delete });

        // --- verifica del contenuto tramite hash (solo dove serve) ---
        if (needHash.Count > 0)
        {
            long total = needHash.Sum(i => i.Local!.Size) * 2;
            long done = 0; long filesDone = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            void Report(string cur)
            {
                if (sw.ElapsedMilliseconds < 150) return;
                sw.Restart();
                progress?.Report(new ProgressInfo
                {
                    Phase = Tr("sync.plan.phase_hash"), Done = Interlocked.Read(ref done), Total = total,
                    FilesDone = Interlocked.Read(ref filesDone), FilesTotal = needHash.Count, Current = cur
                });
            }
            log(Tr("sync.plan.log_hash_start", needHash.Count, PathUtil.FormatBytes(total / 2)));

            await Parallel.ForEachAsync(needHash, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct },
                async (item, t) =>
                {
                    try
                    {
                        var full = PathUtil.Resolve(localRoot, item.Path)!;
                        var lt = localCache.GetAsync(full, item.Local!.Size, item.Local.MTimeTicks,
                            n => { Interlocked.Add(ref done, n); Report(item.Path); }, t);
                        var rt = rc.GetHashAsync(item.Path, t);
                        await Task.WhenAll(lt, rt).ConfigureAwait(false);
                        Interlocked.Add(ref done, item.Local.Size);
                        bool eq = string.Equals(lt.Result, rt.Result, StringComparison.OrdinalIgnoreCase);
                        if (eq)
                        {
                            bool sameTime = PathUtil.SameTime(item.Local.MTimeTicks, item.Remote!.MTimeTicks);
                            item.Status = sameTime ? DiffStatus.Same : DiffStatus.TouchOnly;
                            item.Note = sameTime ? null : Tr("sync.plan.note_same_content");
                        }
                        else { item.Status = DiffStatus.Modified; item.Note = Tr("sync.plan.note_content_diff"); }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e)
                    {
                        item.Status = DiffStatus.Modified;
                        item.Note = Tr("sync.plan.note_hash_fail", e.Message);
                        log(Tr("sync.plan.log_hash_fail", item.Path, e.Message));
                    }
                    Interlocked.Increment(ref filesDone);
                    Report(item.Path);
                }).ConfigureAwait(false);
            localCache.Save();
        }

        plan.Items.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return plan;
    }
}
