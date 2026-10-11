using System.Diagnostics;
using NetSynq.Core;
using static NetSynq.Core.I18n;

namespace NetSynq.UI;

/// <summary>Scheda "Compatta": zippa in loco le cartelle con molti file piccoli (e le ripristina).</summary>
public sealed partial class MainForm
{
    private sealed class CpRow
    {
        public string Path = "";
        public int Level;
        public string Note = "";
        public long Files, Dirs, Bytes;
        public string Result = "";
        public bool Selected;
        public bool CanSelect;
        public bool Dim;
        public Action<bool>? Set;
    }

    private readonly TextBox _cpFolder = new();
    private readonly NumericUpDown _cpN = new() { Minimum = 1, Maximum = 100000, Width = 70 };
    private readonly NumericUpDown _cpK = new() { Minimum = 1, Maximum = 1048576, Width = 80 };
    private readonly NumericUpDown _cpMaxFolder = new() { Minimum = 0, Maximum = 1000000, Width = 80 };
    private readonly NumericUpDown _cpMaxFile = new() { Minimum = 0, Maximum = 1000000, Width = 80 };
    private readonly ComboBox _cpMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly TextBox _cpExcl = new();
    private readonly TextBox _cpAlways = new();
    private readonly TextBox _cpSubs = new();
    private readonly CheckBox _cpMerge = Loc.Text(new CheckBox { AutoSize = true, Margin = new Padding(3, 6, 3, 3) }, "cp.chk.merge");
    private readonly CheckBox _cpPartial = Loc.Text(new CheckBox { AutoSize = true, Margin = new Padding(3, 6, 3, 3) }, "cp.chk.partial");
    private readonly Button _cpBtnAnalyze = MakeButton("cp.btn.analyze", 150, ButtonKind.Primary);
    private readonly Button _cpBtnPreview = MakeButton("cp.btn.preview", 130);
    private readonly Button _cpBtnRun = MakeButton("cp.btn.run", 130, ButtonKind.Primary);
    private readonly Button _cpBtnFindZip = MakeButton("cp.btn.findzip", 150, ButtonKind.Primary);
    private readonly Button _cpBtnRPreview = MakeButton("cp.btn.rpreview", 130);
    private readonly Button _cpBtnRRun = MakeButton("cp.btn.rrun", 130, ButtonKind.Primary);
    private readonly Button _cpBtnCancel = MakeButton("cp.btn.cancel", 90, ButtonKind.Danger);
    private readonly Button _cpBtnAll = MakeButton("cp.btn.all");
    private readonly Button _cpBtnNone = MakeButton("cp.btn.none");
    private readonly Label _cpExplain = new() { AutoSize = true, MaximumSize = new Size(1150, 0), Margin = new Padding(3, 6, 3, 3) };
    private readonly Label _cpSummary = new() { AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), MaximumSize = new Size(1150, 0), Margin = new Padding(3, 6, 3, 3) };
    private readonly Label _cpStatus = new() { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, Height = 22, Margin = new Padding(3, 4, 3, 3) };
    private readonly ProgressBar _cpBar = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 1000, Height = 18 };
    private readonly DataGridView _cpGrid = new();
    private readonly List<Control> _cpInputs = new();

    private CompactPlan? _cpPlan;
    private List<RestoreItem>? _cpRestore;
    private List<CpRow> _cpRows = new();
    private Action? _cpFit;

    private void BuildCompactTab(TabPage page)
    {
        page.Padding = new Padding(10);
        page.AutoScroll = true; // la barra verticale compare solo se serve
        var tip = new ToolTip { AutoPopDelay = 20000 };

        Loc.Combo(_cpMode, "cp.mode.auto", "cp.mode.none", "cp.mode.max");
        var btnBrowse = MakeButton("cp.btn.browse");
        btnBrowse.Click += (_, _) => { var p = PickFolder(_cpFolder.Text, Tr("cp.pick.folder")); if (p != null) _cpFolder.Text = p; };

        var top = FieldTable();
        void Row(string label, Control c, string? hint = null, int hw = 560) => AddField(top, label, c, hint, hw);
        Row("cp.field.folder", Pair(_cpFolder, btnBrowse));

        FlowLayoutPanel Flow(params Control[] cs)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0), MaximumSize = new Size(1150, 0) };
            foreach (var c in cs) f.Controls.Add(c);
            return f;
        }
        Label Lbl(string key) => Loc.Text(new Label { AutoSize = true, Margin = new Padding(8, 6, 3, 3) }, key);
        var lblKb = Lbl("cp.lbl.kb");
        var crit = Flow(Lbl("cp.lbl.more_than"), _cpN, Lbl("cp.lbl.smaller_than"), _cpK, lblKb, _cpMerge, _cpPartial);
        crit.SetFlowBreak(lblKb, true); // le due opzioni vanno sulla riga sotto
        Row("cp.field.criteria", crit);
        Row("cp.field.limits", Flow(Lbl("cp.lbl.folder_max"), _cpMaxFolder, Lbl("cp.lbl.file_max"), _cpMaxFile, Lbl("cp.lbl.no_limit"), _cpMode));
        Row("cp.field.always", _cpAlways, "cp.hint.always", 600);
        Row("cp.field.subs", _cpSubs, "cp.hint.subs", 600);
        Row("cp.field.excl", _cpExcl, "cp.hint.excl", 600);

        var btns = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0), MaximumSize = new Size(1150, 0) };
        var lblComp = MakeLabel("cp.lbl.compact", true); lblComp.MinimumSize = new Size(95, 0);
        btns.Controls.Add(lblComp);
        btns.Controls.AddRange(new Control[] { _cpBtnAnalyze, _cpBtnPreview, _cpBtnRun });
        btns.SetFlowBreak(_cpBtnRun, true); // "Ripristinare" sulla riga sotto
        var lblRip = MakeLabel("cp.lbl.restore", true); lblRip.MinimumSize = new Size(95, 0);
        btns.Controls.Add(lblRip);
        btns.Controls.AddRange(new Control[] { _cpBtnFindZip, _cpBtnRPreview, _cpBtnRRun });
        var btnsSel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        btnsSel.Controls.AddRange(new Control[] { _cpBtnAll, _cpBtnNone });
        var btnsW = StackWithCancel(btns, btnsSel, _cpBtnCancel); // Annulla tutto a destra

        SetupCompactGrid();

        var root = new TableLayoutPanel { ColumnCount = 1, RowCount = 7 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // la colonna non deve allargarsi col testo di stato
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        root.Controls.Add(top, 0, 0);
        root.Controls.Add(btnsW, 0, 1);
        var cardExplain = Theme.Card(_cpExplain, () => Theme.Surface, () => Theme.Border);
        var cardSummary = Theme.Card(_cpSummary, () => Theme.AccentSoft, () => Theme.Accent);
        root.Controls.Add(cardExplain, 0, 2);
        root.Controls.Add(cardSummary, 0, 3);
        root.Controls.Add(_cpGrid, 0, 4);
        root.Controls.Add(_cpBar, 0, 5);
        root.Controls.Add(_cpStatus, 0, 6);
        page.Controls.Add(root);
        _cpFit = MakeFit(page, root, new Control[] { top, btnsW, cardExplain, cardSummary }, 22 + 26, 220);

        Loc.Tip(tip, _cpN, "cp.tip.n");
        Loc.Tip(tip, _cpK, "cp.tip.k");
        Loc.Tip(tip, _cpMerge, "cp.tip.merge");
        Loc.Tip(tip, _cpPartial, "cp.tip.partial");
        Loc.Tip(tip, _cpMaxFolder, "cp.tip.maxfolder");
        Loc.Tip(tip, _cpMaxFile, "cp.tip.maxfile");
        Loc.Tip(tip, _cpMode, "cp.tip.mode");
        Loc.Tip(tip, _cpSubs, "cp.tip.subs");
        Loc.Tip(tip, _cpAlways, "cp.tip.always");
        Loc.Tip(tip, _cpExcl, "cp.tip.excl");
        Loc.Tip(tip, _cpBtnPreview, "cp.tip.preview");
        Loc.Tip(tip, _cpBtnRun, "cp.tip.run");
        Loc.Tip(tip, _cpBtnFindZip, "cp.tip.findzip");
        Loc.Tip(tip, _cpBtnRPreview, "cp.tip.rpreview");
        Loc.Tip(tip, _cpBtnRRun, "cp.tip.rrun");

        _cpBtnAnalyze.Click += async (_, _) => await CpAnalyzeAsync();
        _cpBtnFindZip.Click += async (_, _) => await CpFindZipAsync();
        _cpBtnPreview.Click += (_, _) => CpPreview();
        _cpBtnRun.Click += async (_, _) => await CpRunAsync();
        _cpBtnRPreview.Click += (_, _) => CpPreview();
        _cpBtnRRun.Click += async (_, _) => await CpRunAsync();
        _cpBtnCancel.Click += (_, _) => { _cts?.Cancel(); Log(Tr("cp.log.cancel_req")); };
        _cpBtnAll.Click += (_, _) => CpSetAll(true);
        _cpBtnNone.Click += (_, _) => CpSetAll(false);

        _cpInputs.AddRange(new Control[] { _cpFolder, btnBrowse, _cpN, _cpK, _cpMaxFolder, _cpMaxFile, _cpMode, _cpExcl, _cpAlways, _cpSubs, _cpMerge, _cpPartial, _cpBtnAnalyze, _cpBtnFindZip });

        _cpFolder.Text = _cfg.CompactFolder;
        _cpN.Value = Math.Clamp(_cfg.CompactN, 1, 100000);
        _cpK.Value = Math.Clamp(_cfg.CompactKb, 1, 1048576);
        _cpMaxFolder.Value = Math.Clamp(_cfg.CompactMaxFolderMb, 0, 1000000);
        _cpMaxFile.Value = Math.Clamp(_cfg.CompactMaxFileMb, 0, 1000000);
        _cpMode.SelectedIndex = Math.Clamp(_cfg.CompactMode, 0, 2);
        _cpExcl.Text = _cfg.CompactExcludes;
        _cpAlways.Text = _cfg.CompactAlways;
        _cpSubs.Text = _cfg.CompactSubs;
        _cpMerge.Checked = _cfg.CompactMerge;
        _cpPartial.Checked = _cfg.CompactPartial;
        CpUpdateUi();
    }

    private void SaveCompactCfg()
    {
        _cfg.CompactFolder = _cpFolder.Text.Trim();
        _cfg.CompactN = (int)_cpN.Value;
        _cfg.CompactKb = (int)_cpK.Value;
        _cfg.CompactMaxFolderMb = (int)_cpMaxFolder.Value;
        _cfg.CompactMaxFileMb = (int)_cpMaxFile.Value;
        _cfg.CompactMode = Math.Max(0, _cpMode.SelectedIndex);
        _cfg.CompactExcludes = _cpExcl.Text;
        _cfg.CompactAlways = _cpAlways.Text;
        _cfg.CompactSubs = _cpSubs.Text;
        _cfg.CompactMerge = _cpMerge.Checked;
        _cfg.CompactPartial = _cpPartial.Checked;
        _cfg.Save();
    }

    private CompactOptions CpOptions() => new()
    {
        MinSmallFiles = (int)_cpN.Value,
        SmallFileBytes = (long)_cpK.Value * 1024,
        MaxFolderBytes = (long)_cpMaxFolder.Value * 1024 * 1024,
        MaxFileBytes = (long)_cpMaxFile.Value * 1024 * 1024,
        Compression = (CompactCompression)Math.Max(0, _cpMode.SelectedIndex),
        Excludes = _cpExcl.Text,
        MergeParents = _cpMerge.Checked,
        PartialPacks = _cpPartial.Checked,
        AlwaysZip = _cpAlways.Text,
        ZipSubfolders = _cpSubs.Text
    };

    // ------------------------------------------------------------------ griglia
    private void SetupCompactGrid()
    {
        var g = _cpGrid;
        g.Dock = DockStyle.Fill;
        g.ReadOnly = true;
        g.AllowUserToAddRows = false; g.AllowUserToDeleteRows = false; g.AllowUserToResizeRows = false;
        g.RowHeadersVisible = false;
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        g.MultiSelect = false;
        g.VirtualMode = true;
        Theme.StyleGrid(g);
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        void Col(DataGridViewColumn c, string? key) { g.Columns.Add(c); if (key != null) Loc.Header(c, key); }
        var colSel = new DataGridViewCheckBoxColumn { HeaderText = "✔", Width = 36, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable };
        Col(colSel, null);
        Loc.Add(() => colSel.ToolTipText = Tr("cp.grid.sel_tip"));
        Col(new DataGridViewTextBoxColumn { AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 40, MinimumWidth = 200, SortMode = DataGridViewColumnSortMode.NotSortable }, "cp.grid.path");
        Col(new DataGridViewTextBoxColumn { AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 60, MinimumWidth = 250, SortMode = DataGridViewColumnSortMode.NotSortable }, "cp.grid.note");
        Col(new DataGridViewTextBoxColumn { Width = 70, SortMode = DataGridViewColumnSortMode.NotSortable }, "cp.grid.files");
        Col(new DataGridViewTextBoxColumn { Width = 70, SortMode = DataGridViewColumnSortMode.NotSortable }, "cp.grid.dirs");
        Col(new DataGridViewTextBoxColumn { Width = 90, SortMode = DataGridViewColumnSortMode.NotSortable }, "cp.grid.size");
        Col(new DataGridViewTextBoxColumn { Width = 220, SortMode = DataGridViewColumnSortMode.NotSortable }, "cp.grid.result");
        foreach (int i in new[] { 3, 4, 5 }) g.Columns[i].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;

        g.CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _cpRows.Count) return;
            var r = _cpRows[e.RowIndex];
            e.Value = e.ColumnIndex switch
            {
                0 => r.Selected,
                1 => new string(' ', r.Level * 4) + (r.Level > 0 ? "↳ " : "") + r.Path,
                2 => r.Note,
                3 => r.Files.ToString("N0"),
                4 => r.Dirs.ToString("N0"),
                5 => PathUtil.FormatBytes(r.Bytes),
                6 => r.Result,
                _ => null
            };
        };
        g.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _cpRows.Count) return;
            var r = _cpRows[e.RowIndex];
            e.CellStyle!.ForeColor = r.Dim || !r.Selected ? Theme.Muted : Theme.Text;
        };
        g.CellContentClick += (_, e) => { if (e.ColumnIndex == 0) CpToggle(e.RowIndex); };
        g.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Space && g.CurrentRow != null) { CpToggle(g.CurrentRow.Index); e.Handled = true; }
        };
    }

    private void CpToggle(int row)
    {
        if (_busy || row < 0 || row >= _cpRows.Count) return;
        var r = _cpRows[row];
        if (!r.CanSelect) return;
        r.Set?.Invoke(!r.Selected);
        CpRebuildRows();
    }

    private void CpSetAll(bool on)
    {
        if (_busy) return;
        foreach (var r in _cpRows.ToList()) if (r.CanSelect) r.Set?.Invoke(on);
        CpRebuildRows();
    }

    private void CpRebuildRows()
    {
        var rows = new List<CpRow>();
        if (_cpPlan != null)
        {
            var opt = _cpPlan.Options;
            void AddUnit(CompactItem i, int level, bool dim)
            {
                var item = i;
                string result = item.Status == CompactStatus.Skipped ? Tr("cp.row.untouched") : item.Selected && !dim ? (item.Kind == CompactKind.SmallFiles ? Tr("cp.row.to_zip_inside", item.ZipName) : Tr("cp.row.to_zip", item.ZipName)) : Tr("cp.row.stays");
                rows.Add(new CpRow
                {
                    Path = item.Path, Level = level, Files = item.TotalFiles, Dirs = item.TotalDirs, Bytes = item.TotalBytes,
                    Note = item.Status == CompactStatus.Skipped ? Tr("cp.row.discarded", item.Reason) : item.Note,
                    Result = result, Selected = item.Status == CompactStatus.Candidate && item.Selected,
                    CanSelect = item.Status == CompactStatus.Candidate, Dim = dim || item.Status == CompactStatus.Skipped,
                    Set = v => item.Selected = v
                });
                if (item.IsMerged)
                    foreach (var c in item.Children) AddUnit(c, level + 1, dim || item.Selected);
            }
            foreach (var i in _cpPlan.Items) AddUnit(i, 0, false);
        }
        else if (_cpRestore != null)
        {
            foreach (var it in _cpRestore)
            {
                var item = it;
                rows.Add(new CpRow
                {
                    Path = item.RelZip, Files = item.Files, Dirs = item.Dirs, Bytes = item.Bytes,
                    Note = item.TargetExists ? Tr("cp.row.exists") : Tr("cp.row.extracts"),
                    Result = item.TargetExists ? Tr("cp.row.skipped") : Tr("cp.row.to_dir", Path.GetFileName(item.Target)),
                    Selected = item.Selected && !item.TargetExists, CanSelect = !item.TargetExists, Dim = item.TargetExists,
                    Set = v => item.Selected = v
                });
            }
        }
        _cpRows = rows;
        _cpGrid.RowCount = 0;
        _cpGrid.RowCount = rows.Count;
        _cpGrid.Invalidate();
        CpUpdateUi();
    }

    // riepilogo "dinamico" (non derivato dal piano): conservato come funzione per riscriverlo al cambio lingua
    private Func<string>? _cpSummaryDyn;

    private void CpSetSummary(Func<string> f) { _cpSummaryDyn = f; _cpSummary.Text = f(); }

    private void RefreshCompactLanguage()
    {
        var dyn = _cpSummaryDyn;
        CpRebuildRows(); // righe della griglia, spiegazione e riepilogo derivati dal piano
        if (dyn != null) CpSetSummary(dyn);
        _cpFit?.Invoke();
    }

    private void CpUpdateUi()
    {
        bool has = _cpPlan != null || _cpRestore != null;
        if (_cpPlan != null)
        {
            var p = _cpPlan; var o = p.Options;
            _cpExplain.Text = Tr("cp.explain.plan", o.MinSmallFiles, PathUtil.FormatBytes(o.SmallFileBytes));
            _cpSummaryDyn = null;
            _cpSummary.Text = p.SelectedCount == 0
                ? Tr("cp.sum.plan_none", p.ScannedDirs, p.ScannedFiles, p.SkippedCount)
                : Tr("cp.sum.plan", p.SelectedCount, p.ScannedFiles, p.FilesAfter, p.SelectedFiles - p.SelectedCount, p.SelectedFiles, p.SelectedDirs, PathUtil.FormatBytes(p.SelectedBytes), p.SkippedCount);
        }
        else if (_cpRestore != null)
        {
            var sel = _cpRestore.Where(r => r.Selected && !r.TargetExists).ToList();
            _cpSummaryDyn = null;
            _cpExplain.Text = Tr("cp.explain.restore");
            _cpSummary.Text = _cpRestore.Count == 0 ? Tr("cp.sum.restore_none") :
                Tr("cp.sum.restore", sel.Count, sel.Sum(r => (long)r.Files), PathUtil.FormatBytes(sel.Sum(r => r.Bytes)));
        }
        else
        {
            _cpExplain.Text = Tr("cp.explain.idle");
            _cpSummaryDyn = null;
            _cpSummary.Text = "";
        }
        bool any = (_cpPlan?.SelectedCount ?? 0) > 0 || (_cpRestore?.Any(r => r.Selected && !r.TargetExists) ?? false);
        _cpBtnPreview.Enabled = !_busy && _cpPlan != null;
        _cpBtnRun.Enabled = !_busy && _cpPlan != null && any;
        _cpBtnRPreview.Enabled = !_busy && _cpRestore != null;
        _cpBtnRRun.Enabled = !_busy && _cpRestore != null && any;
        _cpBtnAll.Enabled = _cpBtnNone.Enabled = !_busy && has;
        _cpBtnCancel.Enabled = _busy;
        _cpFit?.Invoke(); // i riquadri di testo cambiano altezza: si ricalcola l'area scorrevole
    }

    private void CpSetBusy(bool busy)
    {
        _busy = busy;
        foreach (var c in _cpInputs) c.Enabled = !busy;
        if (busy) { _cpBar.Style = ProgressBarStyle.Marquee; }
        else { _cpBar.Style = ProgressBarStyle.Continuous; _cpBar.Value = 0; }
        CpUpdateUi();
    }

    private void CpOnProgress(ProgressInfo p)
    {
        if (p.FilesTotal > 0)
        {
            _cpBar.Style = ProgressBarStyle.Continuous;
            _cpBar.Value = (int)Math.Clamp(p.FilesDone * 1000 / p.FilesTotal, 0, 1000);
        }
        var status = p.Phase + (string.IsNullOrEmpty(p.Current) ? "" : "  —  " + p.Current);
        if (_cpStatus.Text != status) _cpStatus.Text = status;
        // durante l'analisi (nessun piano ancora) il progresso compare anche nel riquadro in alto
        if (_cpPlan == null && _cpRestore == null && _cpSummary.Text != p.Phase) { _cpSummaryDyn = null; _cpSummary.Text = p.Phase; }
    }

    // ------------------------------------------------------------------ azioni
    private async Task CpAnalyzeAsync()
    {
        var folder = _cpFolder.Text.Trim();
        if (!Directory.Exists(folder)) { Warn(Tr("cp.warn.bad_folder")); return; }
        if (Path.GetPathRoot(Path.GetFullPath(folder))?.TrimEnd('\\', '/') == Path.GetFullPath(folder).TrimEnd('\\', '/'))
        { Warn(Tr("cp.warn.root")); return; }
        SaveCompactCfg();
        var opt = CpOptions();
        CpSetBusy(true);
        _cts = new CancellationTokenSource();
        _cpRestore = null; _cpPlan = null; CpRebuildRows();
        CpSetSummary(() => Tr("cp.sum.analyzing"));
        Log(Tr("cp.log.analyze_start", folder, opt.MinSmallFiles, _cpK.Value));
        var sw = Stopwatch.StartNew();
        try
        {
            var prog = new Progress<ProgressInfo>(CpOnProgress); // creato nel thread dell'interfaccia
            var token = _cts.Token;
            var plan = await Task.Run(() => CompactAnalyzer.Analyze(folder, opt, prog, Log, token));
            _cpPlan = plan;
            Log(Tr("cp.log.analyze_done", sw.Elapsed.TotalSeconds, plan.CandidateCount, plan.SkippedCount));
            foreach (var i in plan.Items.Where(i => i.Status == CompactStatus.Skipped))
                Log(Tr("cp.log.not_eligible", i.Path, i.Reason));
        }
        catch (OperationCanceledException) { Log(Tr("cp.log.analyze_cancelled")); }
        catch (Exception e) { Log(Tr("cp.log.err_analyze", e.Message)); Warn(e.Message); }
        finally { CpSetBusy(false); _cpStatus.Text = ""; CpRebuildRows(); }
    }

    private async Task CpFindZipAsync()
    {
        var folder = _cpFolder.Text.Trim();
        if (!Directory.Exists(folder)) { Warn(Tr("cp.warn.bad_folder")); return; }
        SaveCompactCfg();
        CpSetBusy(true);
        _cts = new CancellationTokenSource();
        _cpRestore = null; _cpPlan = null; CpRebuildRows();
        CpSetSummary(() => Tr("cp.sum.searching"));
        Log(Tr("cp.log.search_start", folder));
        try
        {
            var token = _cts.Token;
            _cpRestore = await Task.Run(() => CompactRestorer.Analyze(folder, token, Log));
            Log(Tr("cp.log.search_done", _cpRestore.Count));
        }
        catch (OperationCanceledException) { Log(Tr("cp.log.search_cancelled")); }
        catch (Exception e) { Log(Tr("cp.log.err_search", e.Message)); Warn(e.Message); }
        finally { CpSetBusy(false); _cpStatus.Text = ""; CpRebuildRows(); }
    }

    private void CpPreview()
    {
        CompactReport? rep = _cpPlan != null ? CompactPacker.Simulate(_cpPlan)
                           : _cpRestore != null ? CompactRestorer.Simulate(_cpRestore) : null;
        if (rep == null) return;
        Log(Tr("cp.log.preview"));
        using var f = new SimulationForm(rep.SummaryText, rep.Lines, rep.ToText());
        if (f.ShowDialog(this) == DialogResult.Yes) _ = CpRunAsync();
    }

    private async Task CpRunAsync()
    {
        if (_busy) return;
        if (_cpPlan != null) await CpPackAsync(_cpPlan);
        else if (_cpRestore != null) await CpRestoreAsync(_cpRestore);
    }

    private async Task CpPackAsync(CompactPlan plan)
    {
        if (plan.SelectedCount == 0) return;
        var msg = Tr("cp.msg.pack_confirm", plan.SelectedCount, plan.SelectedFiles, PathUtil.FormatBytes(plan.SelectedBytes));
        if (MessageBox.Show(msg, Tr("cp.msg.pack_title"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        SaveCompactCfg();
        long filesBefore = plan.ScannedFiles;
        CpSetBusy(true);
        _cts = new CancellationTokenSource();
        Log(Tr("cp.log.pack_start", plan.SelectedCount));
        var sw = Stopwatch.StartNew();
        CompactResult? res = null;
        var prog = new Progress<ProgressInfo>(CpOnProgress);
        var token = _cts.Token;
        try { res = await Task.Run(() => CompactPacker.Pack(plan, prog, Log, token)); }
        catch (Exception e) { Log(Tr("cp.log.error", e.Message)); Warn(e.Message); }
        finally { CpSetBusy(false); _cpStatus.Text = ""; }
        if (res == null) return;
        long filesAfter = filesBefore - res.FilesArchived + res.Packed;
        long saved = res.BytesBefore - res.ZipBytes;
        double elapsed = sw.Elapsed.TotalSeconds;
        // il riepilogo è composto a righe: la versione per il riquadro (label) toglie l'intestazione e unisce con "  ·  "
        string Summary(bool label)
        {
            var lines = new List<string>();
            if (!label) lines.Add(Tr("cp.sum.pack_head"));
            lines.Add(Tr("cp.sum.pack_files", filesBefore, filesAfter, filesBefore - filesAfter));
            lines.Add(Tr("cp.sum.pack_size", PathUtil.FormatBytes(res.BytesBefore), PathUtil.FormatBytes(res.ZipBytes)) + " " +
                      (res.BytesBefore > 0 ? $"({(saved >= 0 ? "−" : "+")}{PathUtil.FormatBytes(Math.Abs(saved))}, {(saved >= 0 ? "−" : "+")}{Math.Abs(saved) * 100.0 / res.BytesBefore:0.#}%)" : ""));
            lines.Add(Tr("cp.sum.pack_zips", res.Packed) + (res.SplitArchives > 0 ? Tr("cp.sum.pack_split", res.SplitArchives) : "") + Tr("cp.sum.pack_errors", res.Failed) +
                      (res.Leftover > 0 ? Tr("cp.sum.pack_leftover", res.Leftover) : "") + (res.Cancelled ? Tr("cp.sum.cancelled") : ""));
            lines.Add(Tr("cp.sum.pack_time", elapsed));
            return label ? Tr("cp.sum.last_prefix") + string.Join("  ·  ", lines) : string.Join("\n", lines);
        }
        var summary = Summary(false);
        Log(Tr("cp.log.pack_done", summary.Replace("\n", " | ")));
        MessageBox.Show(summary + (res.Failed > 0 ? Tr("cp.msg.pack_failed") : ""), Tr("cp.msg.pack_title"),
            MessageBoxButtons.OK, res.Failed > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        _cpPlan = null; CpRebuildRows();
        CpSetSummary(() => Summary(true));
    }

    private async Task CpRestoreAsync(List<RestoreItem> items)
    {
        var sel = items.Where(i => i.Selected && !i.TargetExists).ToList();
        if (sel.Count == 0) return;
        if (MessageBox.Show(Tr("cp.msg.restore_confirm", sel.Count, sel.Sum(i => (long)i.Files)),
                Tr("cp.msg.restore_title"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        CpSetBusy(true);
        _cts = new CancellationTokenSource();
        RestoreResult? res = null;
        var prog = new Progress<ProgressInfo>(CpOnProgress);
        var token = _cts.Token;
        try { res = await Task.Run(() => CompactRestorer.Restore(sel, true, prog, Log, token)); }
        catch (Exception e) { Log(Tr("cp.log.error", e.Message)); Warn(e.Message); }
        finally { CpSetBusy(false); _cpStatus.Text = ""; }
        if (res == null) return;
        var summary = Tr("cp.sum.restored", res.Restored, res.Failed) + (res.Cancelled ? Tr("cp.sum.cancelled") : "");
        Log(Tr("cp.log.restore_done", summary));
        MessageBox.Show(summary + (res.Failed > 0 ? Tr("cp.msg.restore_failed") : ""), Tr("cp.msg.restore_title"),
            MessageBoxButtons.OK, res.Failed > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        _cpRestore = null; CpRebuildRows();
        CpSetSummary(() => Tr("cp.sum.done"));
    }
}
