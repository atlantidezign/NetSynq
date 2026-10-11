using static NetSynq.Core.I18n;
namespace NetSynq.Core;

/// <summary>Una voce (file) di un sottoalbero, fotografata al momento della scansione.</summary>
public sealed class SnapFile
{
    public string Rel { get; set; } = "";     // relativo alla cartella, con '/'
    public string Full { get; set; } = "";
    public long Size { get; set; }
    public long MTimeTicks { get; set; }
    public int Attr { get; set; }
}

public sealed class SnapDir
{
    public string Rel { get; set; } = "";
    public string Full { get; set; } = "";
    public int Attr { get; set; }
    public long MTimeTicks { get; set; }
}

/// <summary>Descrizione completa di un sottoalbero, con i motivi che impediscono di zipparlo.</summary>
public sealed class Subtree
{
    public List<SnapFile> Files { get; } = new();
    public List<SnapDir> Dirs { get; } = new();
    public long TotalBytes { get; set; }
    public long LargestFile { get; set; }
    public bool HasReparse { get; set; }
    public bool HasExcluded { get; set; }
    public string? Unreadable { get; set; }

    /// <summary>null = zippabile.</summary>
    public string? Problem(CompactOptions opt)
    {
        if (HasReparse) return Tr("cmp.analyze.problem_reparse");
        if (HasExcluded) return Tr("cmp.analyze.problem_excluded");
        if (Unreadable != null) return Tr("cmp.analyze.problem_unreadable", Unreadable);
        if (opt.MaxFolderBytes > 0 && TotalBytes > opt.MaxFolderBytes)
            return Tr("cmp.analyze.problem_size", PathUtil.FormatBytes(TotalBytes), PathUtil.FormatBytes(opt.MaxFolderBytes));
        if (opt.MaxFileBytes > 0 && LargestFile > opt.MaxFileBytes)
            return Tr("cmp.analyze.problem_file", PathUtil.FormatBytes(LargestFile), PathUtil.FormatBytes(opt.MaxFileBytes));
        return null;
    }
}

public static class CompactAnalyzer
{
    public static bool IsTemp(string name) =>
        name.EndsWith(PathUtil.PartExtension, StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".nstmp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fotografa un sottoalbero. <paramref name="baseRel"/> è il percorso della cartella rispetto alla radice di analisi
    /// (serve solo a valutare le esclusioni con gli stessi percorsi usati altrove).
    /// </summary>
    /// <param name="stopOn">se indicato, la scansione si ferma appena la cartella risulta non zippabile con queste opzioni
    /// (limiti superati, collegamenti, elementi esclusi o illeggibili): evita di leggere interi alberi enormi inutilmente.
    /// In tal caso i conteggi sono parziali.</param>
    /// <param name="tick">chiamata ogni tanto col numero di elementi esaminati (per mostrare il progresso).</param>
    public static Subtree Walk(string dir, string baseRel, ExcludeMatcher ex, CancellationToken ct = default,
        CompactOptions? stopOn = null, Action<long>? tick = null)
    {
        var st = new Subtree();
        long seen = 0;
        var stack = new Stack<(string Full, string Rel)>();
        stack.Push((dir, ""));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (full, rel) = stack.Pop();
            List<FileSystemInfo> children;
            try { children = new DirectoryInfo(full).EnumerateFileSystemInfos().ToList(); }
            catch (Exception e) { st.Unreadable ??= e.Message; continue; }

            foreach (var c in children)
            {
                if (stopOn != null && st.Problem(stopOn) != null) return st;
                if (tick != null && (++seen % 1000) == 0) { ct.ThrowIfCancellationRequested(); tick(seen); }
                var crel = rel.Length == 0 ? c.Name : rel + "/" + c.Name;
                var rootRel = baseRel.Length == 0 ? crel : baseRel + "/" + crel;
                FileAttributes at;
                try { at = c.Attributes; } catch (Exception e) { st.Unreadable ??= e.Message; continue; }
                if ((at & FileAttributes.ReparsePoint) != 0) { st.HasReparse = true; continue; }
                if (ex.IsExcluded(rootRel)) { st.HasExcluded = true; continue; }
                try
                {
                    if ((at & FileAttributes.Directory) != 0)
                    {
                        st.Dirs.Add(new SnapDir
                        {
                            Rel = crel, Full = c.FullName,
                            Attr = (int)(c.Attributes & PathUtil.ReplicatedAttrs),
                            MTimeTicks = c.LastWriteTimeUtc.Ticks
                        });
                        stack.Push((c.FullName, crel));
                    }
                    else
                    {
                        var fi = (FileInfo)c;
                        st.Files.Add(new SnapFile
                        {
                            Rel = crel, Full = c.FullName, Size = fi.Length,
                            MTimeTicks = fi.LastWriteTimeUtc.Ticks,
                            Attr = (int)(fi.Attributes & PathUtil.ReplicatedAttrs)
                        });
                        st.TotalBytes += fi.Length;
                        if (fi.Length > st.LargestFile) st.LargestFile = fi.Length;
                    }
                }
                catch (Exception e) { st.Unreadable ??= e.Message; }
            }
        }
        return st;
    }

    /// <summary>I file piccoli direttamente in una cartella (quelli che finiscono nello zip "_piccoli").</summary>
    public static Subtree SmallFiles(string dir, string rel, CompactOptions opt, ExcludeMatcher ex)
    {
        var st = new Subtree();
        foreach (var c in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            try
            {
                var at = c.Attributes;
                if ((at & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) continue;
                var crel = rel.Length == 0 ? c.Name : rel + "/" + c.Name;
                if (ex.IsExcluded(crel) || IsTemp(c.Name) || ZipVolumes.IsZipLike(c.Name)) continue;
                var fi = (FileInfo)c;
                if (fi.Length >= opt.SmallFileBytes) continue;
                st.Files.Add(new SnapFile { Rel = c.Name, Full = c.FullName, Size = fi.Length, MTimeTicks = fi.LastWriteTimeUtc.Ticks, Attr = (int)(fi.Attributes & PathUtil.ReplicatedAttrs) });
                st.TotalBytes += fi.Length;
                if (fi.Length > st.LargestFile) st.LargestFile = fi.Length;
            }
            catch { /* file sparito o illeggibile: non viene incluso né toccato */ }
        }
        return st;
    }

    /// <summary>
    /// Cerca le cartelle da compattare, dall'alto verso il basso: se una cartella contiene più di N file
    /// più piccoli di K (direttamente al suo interno) e rispetta i limiti, viene scelta per intero e non si scende
    /// oltre; altrimenti si esaminano le sottocartelle. La cartella di partenza non viene mai scelta.
    /// I file .zip non contano come file piccoli.
    /// </summary>
    public static CompactPlan Analyze(string root, CompactOptions opt, IProgress<ProgressInfo>? progress,
        Action<string> log, CancellationToken ct)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(Tr("cmp.analyze.err_not_found", root));
        root = Path.GetFullPath(root);
        var ex = new ExcludeMatcher(opt.Excludes);
        var plan = new CompactPlan { Root = root, Options = opt };

        var always = new HashSet<string>((opt.AlwaysZip ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
        var subZip = new HashSet<string>((opt.ZipSubfolders ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<(string Full, string Rel)>();
        stack.Push((root, ""));
        long visited = 0;
        // al massimo 5 aggiornamenti al secondo: troppi messaggi ingolfano l'interfaccia e ritardano la comparsa del risultato
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long lastReport = -1000;
        void Report(string current)
        {
            if (progress == null || clock.ElapsedMilliseconds - lastReport < 200) return;
            lastReport = clock.ElapsedMilliseconds;
            progress.Report(new ProgressInfo { Phase = Tr("cmp.analyze.phase", visited, plan.ScannedFiles), Current = current });
        }
        var seen = new Dictionary<string, (string Full, int Files, List<string> Subs)>(StringComparer.OrdinalIgnoreCase);
        var units = new Dictionary<string, CompactItem>(StringComparer.OrdinalIgnoreCase);

        // Zippa per intero una cartella senza esaminarne l'interno né applicare i limiti di dimensione (node_modules, figlie di "profiles").
        // true = scelta come unità da zippare; false = vuota o non zippabile (se non zippabile compare comunque tra le non idonee).
        bool ForceFolder(string fdir, string frel, int already, string okNote, string badNote)
        {
            Report(frel);
            var fst = Walk(fdir, frel, ex, ct, opt.WithoutSizeLimits(), n => Report(Tr("cmp.analyze.current", frel, n)));
            if (fst.Files.Count == 0) return false;
            var fproblem = fst.Problem(opt.WithoutSizeLimits());
            var fitem = new CompactItem
            {
                Path = frel, FullPath = fdir, Forced = true,
                SmallFiles = fst.Files.Count(f => f.Rel.IndexOf('/') < 0 && f.Size < opt.SmallFileBytes && !IsTemp(f.Rel) && !ZipVolumes.IsZipLike(f.Rel)),
                TotalFiles = fst.Files.Count, TotalDirs = fst.Dirs.Count, TotalBytes = fst.TotalBytes, LargestFile = fst.LargestFile,
                Status = fproblem == null ? CompactStatus.Candidate : CompactStatus.Skipped,
                Reason = fproblem, Selected = fproblem == null,
                Note = fproblem == null ? okNote : badNote
            };
            plan.Items.Add(fitem);
            if (fproblem != null) return false;
            plan.ScannedFiles += fst.Files.Count - already;
            units[frel] = fitem;
            return true;
        }

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (full, rel) = stack.Pop();
            plan.ScannedDirs++;
            if ((++visited & 0x1F) == 0) Report(rel);

            List<FileSystemInfo> children;
            try { children = new DirectoryInfo(full).EnumerateFileSystemInfos().ToList(); }
            catch (Exception e) { log(Tr("cmp.analyze.log_unreadable", rel.Length == 0 ? full : rel, e.Message)); continue; }

            int small = 0, direct = 0; long smallBytes = 0;
            var subdirs = new List<(string Full, string Rel)>();
            foreach (var c in children)
            {
                FileAttributes at;
                try { at = c.Attributes; } catch { continue; }
                if ((at & FileAttributes.ReparsePoint) != 0) continue;
                var crel = rel.Length == 0 ? c.Name : rel + "/" + c.Name;
                if (ex.IsExcluded(crel)) continue;
                if ((at & FileAttributes.Directory) != 0) { subdirs.Add((c.FullName, crel)); continue; }
                plan.ScannedFiles++;
                if (!IsTemp(c.Name)) direct++;
                try
                {
                    var fi = (FileInfo)c;
                    if (fi.Length < opt.SmallFileBytes && !IsTemp(fi.Name) && !ZipVolumes.IsZipLike(fi.Name)) { small++; smallBytes += fi.Length; }
                }
                catch { /* file sparito durante la scansione */ }
            }

            if (rel.Length > 0 && always.Contains(Path.GetFileName(full)))
            {
                var nm = Path.GetFileName(full);
                if (ForceFolder(full, rel, direct,
                        Tr("cmp.analyze.note_always_ok", nm),
                        Tr("cmp.analyze.note_always_bad", nm)))
                    continue;
                // non zippabile (collegamenti, file illeggibili…): si prosegue con l'analisi normale al suo interno
            }
            if (subZip.Contains(Path.GetFileName(full)))
            {
                // la cartella NON viene zippata: ogni sua sottocartella diretta sì, per intero e senza limiti di dimensione
                var nm = Path.GetFileName(full);
                foreach (var sd in subdirs)
                {
                    if (!ForceFolder(sd.Full, sd.Rel, 0,
                            Tr("cmp.analyze.note_sub_ok", nm),
                            Tr("cmp.analyze.note_sub_bad", nm)))
                        stack.Push(sd); // vuota o non zippabile: si analizza normalmente al suo interno
                }
                if (rel.Length > 0 && opt.PartialPacks && small > opt.MinSmallFiles)
                    plan.Items.Add(new CompactItem
                    {
                        Path = rel, FullPath = full, SmallFiles = small, Kind = CompactKind.SmallFiles,
                        TotalFiles = small, TotalDirs = 0, TotalBytes = smallBytes,
                        Status = CompactStatus.Candidate, Selected = true,
                        Note = Tr("cmp.analyze.note_subzip_small", nm, small, PathUtil.FormatBytes(opt.SmallFileBytes), CompactItem.SmallZipBase)
                    });
                continue; // niente accorpamento: ogni sottocartella deve restare un suo zip
            }
            if (rel.Length > 0 && small > opt.MinSmallFiles)
            {
                Report(rel);
                var st = Walk(full, rel, ex, ct, opt, n => Report(Tr("cmp.analyze.current", rel, n)));
                var problem = st.Problem(opt);
                if (problem != null && opt.PartialPacks)
                {
                    var part = new CompactItem
                    {
                        Path = rel, FullPath = full, SmallFiles = small, Kind = CompactKind.SmallFiles,
                        TotalFiles = small, TotalDirs = 0, TotalBytes = smallBytes, LargestFile = 0,
                        Status = CompactStatus.Candidate, Selected = true, Reason = problem,
                        Note = Tr("cmp.analyze.note_partial", problem, small, PathUtil.FormatBytes(opt.SmallFileBytes), CompactItem.SmallZipBase)
                    };
                    plan.Items.Add(part);
                    foreach (var sd in subdirs) stack.Push(sd);
                    if (rel.Length > 0) seen[rel] = (full, direct, subdirs.Select(x => x.Rel).ToList());
                    continue;
                }
                var item = new CompactItem
                {
                    Path = rel, FullPath = full, SmallFiles = small,
                    TotalFiles = st.Files.Count, TotalDirs = st.Dirs.Count,
                    TotalBytes = st.TotalBytes, LargestFile = st.LargestFile,
                    Status = problem == null ? CompactStatus.Candidate : CompactStatus.Skipped,
                    Reason = problem,
                    Selected = problem == null,
                    Note = problem == null
                        ? Tr("cmp.analyze.note_small_ok", small, PathUtil.FormatBytes(opt.SmallFileBytes), opt.MinSmallFiles)
                        : Tr("cmp.analyze.note_small_bad", small)
                };
                plan.Items.Add(item);
                if (problem == null) { plan.ScannedFiles += st.Files.Count - direct; units[rel] = item; continue; } // candidata: non si scende
            }
            if (rel.Length > 0) seen[rel] = (full, direct, subdirs.Select(s => s.Rel).ToList());
            foreach (var s in subdirs) stack.Push(s);
        }

        // Secondo passaggio (dal basso): una cartella che conterrebbe solo zip delle sue sottocartelle
        // (più di N voci tra zip e file) viene zippata una volta sola.
        if (opt.MergeParents)
        {
            foreach (var kv in seen.OrderByDescending(k => k.Key.Count(ch => ch == '/')).ThenBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                var (full, files, subs) = kv.Value;
                if (subs.Count == 0 || !subs.All(units.ContainsKey)) continue;
                if (files + subs.Count <= opt.MinSmallFiles) continue;
                Report(kv.Key);
                var st = Walk(full, kv.Key, ex, ct, opt, n => Report(Tr("cmp.analyze.current", kv.Key, n)));
                if (st.Problem(opt) != null) continue;
                var kids = subs.Select(s => units[s]).ToList();
                var merged = new CompactItem
                {
                    Path = kv.Key, FullPath = full, SmallFiles = kids.Sum(k => k.SmallFiles),
                    TotalFiles = st.Files.Count, TotalDirs = st.Dirs.Count, TotalBytes = st.TotalBytes, LargestFile = st.LargestFile,
                    Status = CompactStatus.Candidate, Selected = true, Children = kids,
                    Note = files > 0
                        ? Tr("cmp.analyze.note_merge_files", subs.Count, files, files + subs.Count, opt.MinSmallFiles)
                        : Tr("cmp.analyze.note_merge", subs.Count, files + subs.Count, opt.MinSmallFiles)
                };
                plan.ScannedFiles += st.Files.Count - files - kids.Sum(k => (long)k.TotalFiles);
                foreach (var k in kids) { plan.Items.Remove(k); units.Remove(k.Path); }
                plan.Items.Add(merged);
                units[kv.Key] = merged;
            }
        }

        plan.Items.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return plan;
    }
}
