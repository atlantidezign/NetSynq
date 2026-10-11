using System.Text;
using static NetSynq.Core.I18n;

namespace NetSynq.Core;

public sealed class SimulationReport
{
    public List<string> Lines { get; } = new();
    public int Copied, CopiedNew, CopiedModified, Touched, Deleted, DirsCreated, DirsDeleted, ConflictsResolved;
    public int KeptLocal;        // file solo slave lasciati perché "elimina" è disattivato
    public int Excluded;         // differenze escluse dall'utente
    public long Bytes;           // da trasferire
    public long FreedBytes;      // liberati dalle eliminazioni
    public string? SpaceProblem; // messaggio se lo spazio non basterebbe

    public string Summary =>
        Tr("sync.sim.sum_title") + "\n" +
        Tr("sync.sim.sum_copy", Copied, PathUtil.FormatBytes(Bytes), CopiedNew, CopiedModified) + "\n" +
        Tr("sync.sim.sum_touch", Touched) + "\n" +
        Tr("sync.sim.sum_dirs", DirsCreated, Deleted, PathUtil.FormatBytes(FreedBytes), DirsDeleted) + "\n" +
        (KeptLocal > 0 ? Tr("sync.sim.sum_kept", KeptLocal) + "\n" : "") +
        Tr("sync.sim.sum_excluded", Excluded) + "\n" +
        (SpaceProblem != null ? Tr("sync.sim.sum_warn", SpaceProblem) + "\n" : Tr("sync.sim.sum_space_ok") + "\n");

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Summary).AppendLine(new string('-', 78));
        foreach (var l in Lines) sb.AppendLine(l);
        return sb.ToString();
    }
}

/// <summary>
/// "Prova a secco": applica esattamente gli stessi filtri e la stessa logica di <see cref="Syncer"/>
/// (selezione, elimina sì/no) ma senza toccare il disco né la rete. Serve a vedere in anticipo cosa succederebbe.
/// </summary>
public static class Simulator
{
    public static SimulationReport Run(SyncPlan plan, SyncOptions opt)
    {
        var r = new SimulationReport();
        string Sz(long b) => PathUtil.FormatBytes(b);

        // 1) conflitti file/cartella
        foreach (var i in plan.Items.Where(x => x.Selected && x.TypeConflict))
        {
            r.ConflictsResolved++;
            r.Lines.Add(Tr("sync.sim.line_remove", i.Path, i.Note));
        }

        // 2) cartelle nuove
        foreach (var d in plan.Items.Where(x => x.Selected && x.IsDir && (x.Status == DiffStatus.New || x.TypeConflict)).OrderBy(x => x.Path.Length))
        {
            r.DirsCreated++;
            r.Lines.Add(Tr("sync.sim.line_dir", d.Path));
        }

        // 3) solo data
        foreach (var t in plan.Items.Where(x => x.Selected && !x.IsDir && x.Status == DiffStatus.TouchOnly))
        {
            r.Touched++;
            r.Lines.Add(Tr("sync.sim.line_touch", t.Path, t.Remote!.MTimeUtc.ToLocalTime()));
        }

        // 4) copie
        foreach (var i in plan.Items.Where(x => x.Selected && !x.IsDir && (x.Status == DiffStatus.New || x.Status == DiffStatus.Modified)))
        {
            r.Copied++;
            r.Bytes += i.Remote!.Size;
            if (i.Status == DiffStatus.New)
            {
                r.CopiedNew++;
                r.Lines.Add(Tr("sync.sim.line_new", i.Path, Sz(i.Remote.Size)));
            }
            else
            {
                r.CopiedModified++;
                r.Lines.Add(Tr("sync.sim.line_replace", i.Path, Sz(i.Local?.Size ?? 0), Sz(i.Remote.Size), i.Note != null ? "; " + i.Note : ""));
            }
        }

        // 5) eliminazioni
        foreach (var d in plan.Items.Where(x => x.Selected && x.Status == DiffStatus.Delete).OrderBy(x => x.IsDir).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (!opt.DeleteExtra)
            {
                r.KeptLocal++;
                r.Lines.Add(Tr("sync.sim.line_keep", d.Path, d.IsDir ? "/" : ""));
                continue;
            }
            if (d.IsDir) { r.DirsDeleted++; r.Lines.Add(Tr("sync.sim.line_del_dir", d.Path)); }
            else { r.Deleted++; r.FreedBytes += d.Local?.Size ?? 0; r.Lines.Add(Tr("sync.sim.line_del_file", d.Path, Sz(d.Local?.Size ?? 0))); }
        }

        // 6) esclusi dall'utente
        foreach (var e in plan.Items.Where(x => !x.Selected && x.Status != DiffStatus.Same))
        {
            r.Excluded++;
            r.Lines.Add(Tr("sync.sim.line_excluded", e.Path, e.IsDir ? "/" : "", StatusName(e.Status)));
        }

        r.SpaceProblem = Syncer.CheckSpace(plan, opt);
        return r;
    }

    private static string StatusName(DiffStatus s) => s switch
    {
        DiffStatus.New => Tr("sync.sim.st_new"),
        DiffStatus.Modified => Tr("sync.sim.st_modified"),
        DiffStatus.TouchOnly => Tr("sync.sim.st_touch"),
        DiffStatus.Delete => Tr("sync.sim.st_delete"),
        _ => Tr("sync.sim.st_same")
    };
}
