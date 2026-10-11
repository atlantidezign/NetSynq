using System.Diagnostics;
using System.Net;
using System.Text;
using NetSynq.Core;
using static NetSynq.Core.I18n;

namespace NetSynq.UI;

public sealed partial class MainForm
{
    // ---- connessione
    private readonly TextBox _txtHost = new() { Width = 150 };
    private readonly NumericUpDown _numPort = new() { Minimum = 1024, Maximum = 65535, Width = 80 };
    private readonly TextBox _txtToken = new() { Width = 190, Font = new Font("Consolas", 10f) };
    private readonly Button _btnFind = MakeButton("cli.btn.find");
    private readonly Button _btnTest = MakeButton("cli.btn.test");
    // ---- cartella e opzioni
    private readonly TextBox _txtLocal = new();
    private readonly ComboBox _cmbMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    private readonly CheckBox _chkDelete = Loc.Text(new CheckBox() { AutoSize = true, Checked = true }, "cli.chk.delete");
    private readonly CheckBox _chkDry = Loc.Text(new CheckBox() { AutoSize = true }, "cli.chk.dry");
    private readonly NumericUpDown _numPar = new() { Minimum = 1, Maximum = 8, Width = 50 };
    private readonly TextBox _txtExcl = new() { Width = 260 };
    // ---- azioni e filtri
    private readonly Button _btnCompare = MakeButton("cli.btn.compare", 130, ButtonKind.Primary);
    private readonly Button _btnSync = MakeButton("cli.btn.sync", 130, ButtonKind.Primary);
    private readonly Button _btnCancel = MakeButton("cli.btn.cancel", 90, ButtonKind.Danger);
    private readonly RadioButton _rbDiff = Loc.Text(new RadioButton() { AutoSize = true, Checked = true }, "cli.rb.diff");
    private readonly RadioButton _rbAll = Loc.Text(new RadioButton() { AutoSize = true }, "cli.rb.all");
    private readonly TextBox _txtSearch = new() { Width = 200 };
    private readonly Button _btnSelAll = MakeButton("cli.btn.sel_all");
    private readonly Button _btnSelNone = MakeButton("cli.btn.sel_none");
    private readonly Label _lblSummary = new() { AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
    private readonly DataGridView _grid = new();
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 1000, Height = 18 };
    private readonly Label _lblProgress = new() { AutoSize = true, Text = Tr("cli.prog.ready") };
    private readonly List<Control> _cliInputs = new();

    private SyncPlan? _plan;
    private List<DiffItem> _view = new();
    private CancellationTokenSource? _cts;
    private bool _busy;
    private int _sortCol = 2;
    private bool _sortAsc = true;
    private readonly Stopwatch _opSw = new();
    private string? _progKey = "cli.prog.ready"; // chiave del testo di avanzamento "fisso" (null = testo dinamico)

    private void BuildClientTab(TabPage page)
    {
        page.Padding = new Padding(10);
        page.AutoScroll = true; // la barra verticale compare solo se serve
        var t = new TableLayoutPanel { ColumnCount = 1 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // --- connessione, cartella e opzioni: etichetta | campo | suggerimento a destra
        var g1 = FieldTable();
        var f1 = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0) };
        f1.Controls.Add(MakeLabel("cli.lbl.ip")); f1.Controls.Add(_txtHost);
        f1.Controls.Add(MakeLabel("cli.lbl.port")); f1.Controls.Add(_numPort);
        f1.Controls.Add(MakeLabel("cli.lbl.token")); f1.Controls.Add(_txtToken);
        f1.Controls.Add(_btnFind); f1.Controls.Add(_btnTest);
        AddField(g1, "cli.fld.master", f1, "cli.hint.master");

        var btnBrowse = MakeButton("cli.btn.browse");
        btnBrowse.Click += (_, _) => { var p = PickFolder(_txtLocal.Text, Tr("cli.dlg.slave_folder")); if (p != null) _txtLocal.Text = p; };
        AddField(g1, "cli.fld.folder", Pair(_txtLocal, btnBrowse), "cli.hint.folder");

        Loc.Combo(_cmbMode, "cli.mode.fast", "cli.mode.smart", "cli.mode.full");
        var f3 = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0) };
        f3.Controls.Add(_cmbMode);
        f3.Controls.Add(_chkDelete);
        f3.Controls.Add(_chkDry);
        f3.SetFlowBreak(_chkDry, true); // "Parallelismo" sulla riga sotto
        f3.Controls.Add(MakeLabel("cli.lbl.parallel")); f3.Controls.Add(_numPar);
        AddField(g1, "cli.fld.options", f3, "cli.hint.options", 600);

        AddField(g1, "cli.fld.exclude", _txtExcl, "cli.hint.exclude");

        // --- riga pulsanti e filtri
        var f4 = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0) };
        _btnSync.Enabled = false; _btnCancel.Enabled = false;
        f4.Controls.Add(_btnCompare); f4.Controls.Add(_btnSync);
        f4.Controls.Add(Loc.Text(new Label { AutoSize = true, Margin = new Padding(12, 8, 3, 3) }, "cli.lbl.view"));
        f4.Controls.Add(_rbDiff); f4.Controls.Add(_rbAll);
        f4.Controls.Add(Loc.Text(new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 3) }, "cli.lbl.search"));
        f4.Controls.Add(_txtSearch);
        var f4sel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        f4sel.Controls.Add(_btnSelAll); f4sel.Controls.Add(_btnSelNone);
        var f4w = StackWithCancel(f4, f4sel, _btnCancel); // Annulla tutto a destra
        _rbDiff.Margin = new Padding(3, 6, 3, 3); _rbAll.Margin = new Padding(3, 6, 3, 3);
        _lblSummary.Margin = new Padding(3, 4, 3, 4);

        // --- griglia
        SetupGrid();
        Theme.StyleGrid(_grid);

        // --- avanzamento
        var f6 = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
        f6.Controls.Add(_bar, 0, 0);
        f6.Controls.Add(_lblProgress, 0, 1);

        foreach (var c in new Control[] { g1, f4w, _lblSummary })
        { t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.Controls.Add(c, 0, t.RowCount++); }
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.Controls.Add(_grid, 0, t.RowCount++);
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.Controls.Add(f6, 0, t.RowCount++);
        page.Controls.Add(t);
        MakeFit(page, t, new Control[] { g1, f4w, _lblSummary, f6 }, 0, 220);

        _cliInputs.AddRange(new Control[] { _txtHost, _numPort, _txtToken, _btnFind, _btnTest, _txtLocal, btnBrowse, _cmbMode, _chkDelete, _chkDry, _numPar, _txtExcl });

        // --- valori salvati
        _txtHost.Text = _cfg.ClientHost;
        _numPort.Value = Math.Clamp(_cfg.ClientPort, 1024, 65535);
        _txtToken.Text = _cfg.ClientToken;
        _txtLocal.Text = _cfg.ClientFolder;
        _cmbMode.SelectedIndex = Math.Clamp((int)_cfg.Compare, 0, 2);
        _chkDelete.Checked = _cfg.DeleteExtra;
        _chkDry.Checked = _cfg.DryRun;
        _numPar.Value = Math.Clamp(_cfg.Parallel, 1, 8);
        _txtExcl.Text = _cfg.ClientExcludes;

        // --- eventi
        _btnFind.Click += async (_, _) => await FindMasterAsync();
        _btnTest.Click += async (_, _) => await TestConnectionAsync();
        _btnCompare.Click += async (_, _) => await RunCompareAsync();
        _btnSync.Click += async (_, _) => await RunSyncAsync();
        _btnCancel.Click += (_, _) => { _cts?.Cancel(); Log(Tr("cli.log.cancel_req")); };
        _rbDiff.CheckedChanged += (_, _) => ApplyFilter();
        _txtSearch.TextChanged += (_, _) => ApplyFilter();
        _chkDelete.CheckedChanged += (_, _) => { _grid.Invalidate(); UpdateSummary(); };
        _chkDry.CheckedChanged += (_, _) => UpdateSyncButton();
        _btnSelAll.Click += (_, _) => SetVisibleSelected(true);
        _btnSelNone.Click += (_, _) => SetVisibleSelected(false);
        var tip = new ToolTip();
        tip.AutoPopDelay = 20000;
        Loc.Tip(tip, _btnSelAll, "cli.tip.sel_all");
        Loc.Tip(tip, _btnSelNone, "cli.tip.sel_none");
        Loc.Tip(tip, _chkDry, "cli.tip.dry");
        Loc.Tip(tip, _chkDelete, "cli.tip.delete");
        Loc.Tip(tip, _btnFind, "cli.tip.find");
        Loc.Tip(tip, _btnTest, "cli.tip.test");
        Loc.Tip(tip, _txtHost, "cli.tip.host");
        Loc.Tip(tip, _txtLocal, "cli.tip.local");
        Loc.Tip(tip, _txtExcl, "cli.tip.exclude");
        Loc.Tip(tip, _numPar, "cli.tip.parallel");
        Loc.Tip(tip, _cmbMode, "cli.tip.mode");
        Loc.Tip(tip, _btnCompare, "cli.tip.compare");
        UpdateSyncButton();
        UpdateSummary();
    }

    /// <summary>Riscrive i testi della scheda Slave che dipendono dallo stato (dopo Loc.Refresh).</summary>
    private void RefreshClientLanguage()
    {
        UpdateSyncButton();
        UpdateSummary();
        if (!_busy && _progKey != null) _lblProgress.Text = Tr(_progKey);
        _grid.Invalidate(); // stati delle righe (StatusText)
    }

    private void SaveClientCfg()
    {
        _cfg.ClientHost = _txtHost.Text.Trim();
        _cfg.ClientPort = (int)_numPort.Value;
        _cfg.ClientToken = _txtToken.Text.Trim();
        _cfg.ClientFolder = _txtLocal.Text.Trim();
        _cfg.Compare = (CompareMode)Math.Max(0, _cmbMode.SelectedIndex);
        _cfg.DeleteExtra = _chkDelete.Checked;
        _cfg.DryRun = _chkDry.Checked;
        _cfg.Parallel = (int)_numPar.Value;
        _cfg.ClientExcludes = _txtExcl.Text;
        _cfg.Save();
    }

    // ------------------------------------------------------------------ griglia
    private void SetupGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.VirtualMode = true;
        _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.RowTemplate.Height = 22;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "✔", Width = 36, ReadOnly = true, SortMode = DataGridViewColumnSortMode.Programmatic });
        Loc.Add(() => _grid.Columns[0].ToolTipText = Tr("cli.col.included_tip"));
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Width = 190, SortMode = DataGridViewColumnSortMode.Programmatic });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 100, MinimumWidth = 200, SortMode = DataGridViewColumnSortMode.Programmatic });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Width = 95, SortMode = DataGridViewColumnSortMode.Programmatic });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Width = 95, SortMode = DataGridViewColumnSortMode.Programmatic });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Width = 135, SortMode = DataGridViewColumnSortMode.Programmatic });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Width = 135, SortMode = DataGridViewColumnSortMode.Programmatic });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Width = 230, SortMode = DataGridViewColumnSortMode.Programmatic });
        Loc.Header(_grid.Columns[1], "cli.col.status"); Loc.Header(_grid.Columns[2], "cli.col.path"); Loc.Header(_grid.Columns[3], "cli.col.size_master");
        Loc.Header(_grid.Columns[4], "cli.col.size_slave"); Loc.Header(_grid.Columns[5], "cli.col.date_master"); Loc.Header(_grid.Columns[6], "cli.col.date_slave");
        Loc.Header(_grid.Columns[7], "cli.col.note");
        _grid.Columns[3].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        _grid.Columns[4].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;

        _grid.CellValueNeeded += Grid_CellValueNeeded;
        _grid.CellFormatting += Grid_CellFormatting;
        _grid.ColumnHeaderMouseClick += (_, e) =>
        {
            if (e.ColumnIndex == 0) { ToggleAllVisible(); return; }
            if (_sortCol == e.ColumnIndex) _sortAsc = !_sortAsc; else { _sortCol = e.ColumnIndex; _sortAsc = true; }
            ApplyFilter();
        };
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex > 0) OpenContentDiff(e.RowIndex); };
        _grid.CellContentClick += (_, e) => { if (e.ColumnIndex == 0 && e.RowIndex >= 0) ToggleRow(e.RowIndex); };
        _grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Space && _grid.CurrentCell is { RowIndex: >= 0 } c) { ToggleRow(c.RowIndex); e.Handled = true; }
        };

        var menu = new ContextMenuStrip();
        var miView = new ToolStripMenuItem();
        miView.Click += (_, _) => { if (_grid.CurrentCell != null) OpenContentDiff(_grid.CurrentCell.RowIndex); };
        var miToggle = new ToolStripMenuItem();
        miToggle.Click += (_, _) => { if (_grid.CurrentCell != null) ToggleRow(_grid.CurrentCell.RowIndex); };
        var miCopy = new ToolStripMenuItem();
        miCopy.Click += (_, _) =>
        {
            if (_grid.CurrentCell is { RowIndex: >= 0 } c && c.RowIndex < _view.Count)
                try { Clipboard.SetText(_view[c.RowIndex].Path); } catch { }
        };
        Loc.Add(() =>
        {
            miView.Text = Tr("cli.menu.view_diff");
            miToggle.Text = Tr("cli.menu.toggle");
            miCopy.Text = Tr("cli.menu.copy_path");
        });
        menu.Items.Add(miToggle); menu.Items.Add(miView); menu.Items.Add(miCopy);
        _grid.ContextMenuStrip = menu;
        _grid.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0) { _grid.ClearSelection(); _grid.CurrentCell = _grid[Math.Max(e.ColumnIndex, 0), e.RowIndex]; }
        };
    }

    private string StatusText(DiffItem it) => it.Status switch
    {
        DiffStatus.Same => Tr("cli.st.same"),
        DiffStatus.New => it.IsDir ? Tr("cli.st.new_dir") : Tr("cli.st.new"),
        DiffStatus.Modified => Tr("cli.st.modified"),
        DiffStatus.TouchOnly => Tr("cli.st.touch"),
        DiffStatus.Delete => _chkDelete.Checked ? Tr("cli.st.delete") : Tr("cli.st.keep"),
        _ => ""
    };

    private static string Ts(Entry? e) =>
        e == null || e.IsDir ? "" : e.MTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private static string Sz(Entry? e) => e == null || e.IsDir ? "" : PathUtil.FormatBytes(e.Size);

    private void Grid_CellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _view.Count) return;
        var it = _view[e.RowIndex];
        e.Value = e.ColumnIndex switch
        {
            0 => (object)(it.Status != DiffStatus.Same && it.Selected),
            1 => StatusText(it),
            2 => it.IsDir ? it.Path + "/" : it.Path,
            3 => Sz(it.Remote),
            4 => Sz(it.Local),
            5 => Ts(it.Remote),
            6 => Ts(it.Local),
            7 => it.Note ?? "",
            _ => ""
        };
    }

    private void Grid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _view.Count || e.CellStyle == null) return;
        var it = _view[e.RowIndex];
        Color? c = it.Status switch
        {
            DiffStatus.New => Theme.RowNew,
            DiffStatus.Modified => Theme.RowModified,
            DiffStatus.TouchOnly => Theme.RowTouch,
            DiffStatus.Delete => _chkDelete.Checked ? Theme.RowDelete : Theme.RowDeleteOff,
            _ => null
        };
        if (c != null) e.CellStyle.BackColor = c.Value;
        if (!it.Selected && it.Status != DiffStatus.Same)
        {
            // esclusa dall'utente: grigia e sbiadita
            e.CellStyle.BackColor = Theme.RowOffBack;
            e.CellStyle.ForeColor = Theme.Muted;
        }
    }

    private void ApplyFilter()
    {
        if (_plan == null) { _view = new(); _grid.RowCount = 0; return; }
        IEnumerable<DiffItem> q = _plan.Items;
        if (_rbDiff.Checked) q = q.Where(i => i.Status != DiffStatus.Same);
        var s = _txtSearch.Text.Trim();
        if (s.Length > 0) q = q.Where(i => i.Path.Contains(s, StringComparison.OrdinalIgnoreCase));

        Func<DiffItem, IComparable> key = _sortCol switch
        {
            1 => i => (int)i.Status,
            3 => i => i.Remote?.Size ?? -1L,
            4 => i => i.Local?.Size ?? -1L,
            5 => i => i.Remote?.MTimeTicks ?? -1L,
            6 => i => i.Local?.MTimeTicks ?? -1L,
            7 => i => i.Note ?? "",
            _ => i => i.Path.ToLowerInvariant()
        };
        _view = (_sortAsc ? q.OrderBy(key) : q.OrderByDescending(key)).ToList();
        _grid.RowCount = 0;
        _grid.RowCount = _view.Count;
        _grid.Invalidate();
    }

    private void UpdateSummary()
    {
        if (_plan == null) { _lblSummary.Text = Tr("cli.sum.none"); return; }
        var p = _plan;
        int nNew = p.FileCount(DiffStatus.New), nMod = p.FileCount(DiffStatus.Modified), nTouch = p.FileCount(DiffStatus.TouchOnly);
        int nDel = p.FileCount(DiffStatus.Delete);
        int nSame = p.FileCount(DiffStatus.Same);
        int sCopy = p.SelectedFileCount(DiffStatus.New) + p.SelectedFileCount(DiffStatus.Modified);
        int sTouch = p.SelectedFileCount(DiffStatus.TouchOnly);
        int sDel = p.SelectedFileCount(DiffStatus.Delete);
        var excl = p.DeselectedCount > 0 ? Tr("cli.sum.excluded", p.DeselectedCount) : "";
        _lblSummary.Text =
            Tr("cli.sum.found", nNew, nMod, nTouch, nDel, nSame) + Environment.NewLine +
            Tr(_chkDelete.Checked ? "cli.sum.sel_del" : "cli.sum.sel_keep", sCopy, PathUtil.FormatBytes(p.DownloadBytes), sTouch, sDel, excl);
        if (!_busy) _btnSync.Enabled = p.HasSelectedChanges;
    }

    private void UpdateSyncButton() => _btnSync.Text = Tr(_chkDry.Checked ? "cli.btn.simulate" : "cli.btn.sync");

    private void AfterSelectionChanged()
    {
        _grid.Invalidate();
        UpdateSummary();
    }

    private void ToggleRow(int row)
    {
        if (_plan == null || _busy || row < 0 || row >= _view.Count) return;
        var it = _view[row];
        if (it.Status == DiffStatus.Same) return;
        _plan.SetSelected(it, !it.Selected);   // se è una cartella la scelta vale per tutto il contenuto
        AfterSelectionChanged();
    }

    private void SetVisibleSelected(bool value)
    {
        if (_plan == null || _busy) return;
        foreach (var it in _view) if (it.Status != DiffStatus.Same) it.Selected = value;
        AfterSelectionChanged();
    }

    private void ToggleAllVisible()
    {
        var changes = _view.Where(i => i.Status != DiffStatus.Same).ToList();
        if (changes.Count == 0) return;
        SetVisibleSelected(!changes.All(i => i.Selected));
    }

    // ------------------------------------------------------------------ operazioni
    private bool ValidateClient(out string host, out int port, out string token, out string folder)
    {
        host = _txtHost.Text.Trim(); port = (int)_numPort.Value; token = _txtToken.Text.Trim(); folder = _txtLocal.Text.Trim();
        if (host.Length == 0) { Warn(Tr("cli.err.no_host")); return false; }
        var tokErr = AppSettings.ValidateToken(token, false);
        if (tokErr != null) { Warn(tokErr); return false; }
        if (folder.Length == 0 || !Directory.Exists(folder)) { Warn(Tr("cli.err.bad_folder")); return false; }
        var full = Path.GetFullPath(folder);
        if (string.Equals(Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase))
        { Warn(Tr("cli.err.root_folder")); return false; }
        return true;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var c in _cliInputs) c.Enabled = !busy;
        _btnCompare.Enabled = !busy;
        _btnSync.Enabled = !busy && _plan != null && _plan.HasSelectedChanges;
        _btnCancel.Enabled = busy;
        _btnSelAll.Enabled = !busy; _btnSelNone.Enabled = !busy;
        if (busy) { _opSw.Restart(); _bar.Style = ProgressBarStyle.Marquee; }
        else { _bar.Style = ProgressBarStyle.Continuous; _bar.Value = 0; }
    }

    private void OnProgress(ProgressInfo p)
    {
        if (p.Total > 0)
        {
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = (int)Math.Clamp(p.Done * 1000 / p.Total, 0, 1000);
        }
        else _bar.Style = ProgressBarStyle.Marquee;

        _progKey = null;
        var sb = new StringBuilder(p.Phase);
        if (p.FilesTotal > 0) sb.Append(Tr("cli.prog.files", p.FilesDone, p.FilesTotal));
        if (p.Total > 0)
        {
            sb.Append(Tr("cli.prog.bytes", PathUtil.FormatBytes(p.Done), PathUtil.FormatBytes(p.Total)));
            var secs = _opSw.Elapsed.TotalSeconds;
            if (secs > 2 && p.Done > 0)
            {
                var speed = p.Done / secs;
                sb.Append($"   {PathUtil.FormatBytes((long)speed)}/s");
                var eta = TimeSpan.FromSeconds(Math.Max(0, (p.Total - p.Done) / speed));
                sb.Append(Tr("cli.prog.eta", (int)eta.TotalHours, eta.Minutes, eta.Seconds));
            }
        }
        if (!string.IsNullOrEmpty(p.Current)) sb.Append("    " + p.Current);
        _lblProgress.Text = sb.ToString();
    }

    private async Task FindMasterAsync()
    {
        _btnFind.Enabled = false;
        Log(Tr("cli.log.finding"));
        try
        {
            var found = await Discovery.FindAsync(TimeSpan.FromSeconds(2));
            if (found.Count == 0)
            {
                Log(Tr("cli.log.none_found"));
                Warn(Tr("cli.msg.none_found"));
            }
            else if (found.Count == 1) ApplyFound(found[0]);
            else
            {
                var menu = new ContextMenuStrip();
                foreach (var f in found) { var ff = f; menu.Items.Add(ff.ToString(), null, (_, _) => ApplyFound(ff)); }
                menu.Show(_btnFind, new Point(0, _btnFind.Height));
            }
        }
        catch (Exception e) { Log(Tr("cli.log.find_failed", e.Message)); }
        finally { _btnFind.Enabled = true; }
    }

    private void ApplyFound(Discovery.Found f)
    {
        _txtHost.Text = f.Ip.ToString();
        _numPort.Value = Math.Clamp(f.Port, 1024, 65535);
        Log(Tr("cli.log.found", f));
        _txtToken.Focus();
    }

    private async Task TestConnectionAsync()
    {
        if (_txtHost.Text.Trim().Length == 0 || _txtToken.Text.Trim().Length == 0) { Warn(Tr("cli.err.no_host_token")); return; }
        _btnTest.Enabled = false;
        try
        {
            using var rc = new RemoteClient(_txtHost.Text.Trim(), (int)_numPort.Value, _txtToken.Text.Trim());
            var name = await rc.PingAsync(CancellationToken.None);
            Log(Tr("cli.log.connected", name));
            MessageBox.Show(Tr("cli.msg.connected", name), "NetSynq", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (UnauthorizedAccessException e) { Log(Tr("cli.log.error", e.Message)); Warn(e.Message); }
        catch (Exception e)
        {
            Log(Tr("cli.log.conn_error", e.Message));
            Warn(Tr("cli.msg.conn_failed", e.Message));
        }
        finally { _btnTest.Enabled = true; }
    }

    private async Task<bool> RunCompareAsync()
    {
        if (!ValidateClient(out var host, out var port, out var token, out var folder)) return false;
        SaveClientCfg();
        SetBusy(true);
        _cts = new CancellationTokenSource();
        bool ok = false;
        try
        {
            using var rc = new RemoteClient(host, port, token);
            var mode = (CompareMode)Math.Max(0, _cmbMode.SelectedIndex);
            Log(Tr("cli.log.compare_start", mode));
            _plan = await Planner.BuildAsync(rc, folder, mode, new ExcludeMatcher(_txtExcl.Text), _clientCache,
                new Progress<ProgressInfo>(OnProgress), Log, _cts.Token);
            ok = true;
            Log(Tr("cli.log.compare_done", _opSw.Elapsed.TotalSeconds));
        }
        catch (OperationCanceledException) { Log(Tr("cli.log.compare_cancelled")); _plan = null; }
        catch (UnauthorizedAccessException e) { Log(Tr("cli.log.error", e.Message)); Warn(e.Message); _plan = null; }
        catch (Exception e)
        {
            Log(Tr("cli.log.error", e.Message));
            Warn(Tr("cli.msg.compare_failed", e.Message));
            _plan = null;
        }
        finally
        {
            _clientCache.Save();
            SetBusy(false);
            ApplyFilter();
            UpdateSummary();
            _progKey = _plan == null ? "cli.prog.ready" : (_plan.HasChanges ? "cli.prog.compare_done" : "cli.prog.no_diff");
            _lblProgress.Text = Tr(_progKey);
        }
        return ok;
    }

    private async Task RunSyncAsync()
    {
        if (_plan == null) return;
        if (!_plan.HasSelectedChanges)
        {
            Warn(Tr(_plan.HasChanges ? "cli.err.none_selected" : "cli.err.nothing_to_apply"));
            return;
        }
        if (!ValidateClient(out var host, out var port, out var token, out _)) return;
        var plan = _plan;
        var opt = new SyncOptions { DeleteExtra = _chkDelete.Checked, Parallel = (int)_numPar.Value };

        // --- prova a secco: mostra cosa farebbe, senza toccare nulla ---
        if (_chkDry.Checked)
        {
            var report = Simulator.Run(plan, opt);
            Log(Tr("cli.log.sim_done", report.Summary.Replace("\n", " ").Trim()));
            using var dlg = new SimulationForm(report);
            if (dlg.ShowDialog(this) != DialogResult.Yes) return;
            // l'utente ha scelto "Esegui davvero": si prosegue con la conferma normale
        }

        await ExecuteSyncAsync(plan, opt, host, port, token);
    }

    private async Task ExecuteSyncAsync(SyncPlan plan, SyncOptions opt, string host, int port, string token)
    {
        int nCopy = plan.Items.Count(i => i.Selected && !i.IsDir && (i.Status == DiffStatus.New || i.Status == DiffStatus.Modified));
        int nTouch = plan.SelectedFileCount(DiffStatus.TouchOnly);
        int nDel = plan.SelectedFileCount(DiffStatus.Delete);
        int localFiles = plan.Items.Count(i => !i.IsDir && i.Local != null);

        var space = Syncer.CheckSpace(plan, opt);
        if (space != null) { Warn(space); return; }

        var msg = new StringBuilder();
        msg.AppendLine(Tr("cli.cf.folder", plan.LocalRoot)).AppendLine();
        msg.AppendLine(Tr("cli.cf.copy", nCopy, PathUtil.FormatBytes(plan.DownloadBytes)));
        if (nTouch > 0) msg.AppendLine(Tr("cli.cf.touch", nTouch));
        msg.AppendLine(opt.DeleteExtra ? Tr("cli.cf.delete", nDel, PathUtil.FormatBytes(plan.DeleteBytes))
                                       : Tr("cli.cf.keep", nDel));
        if (plan.DeselectedCount > 0) msg.AppendLine(Tr("cli.cf.excluded", plan.DeselectedCount));
        bool massDelete = opt.DeleteExtra && nDel > 50 && localFiles > 0 && nDel * 5 > localFiles;
        if (massDelete)
            msg.AppendLine().AppendLine(Tr("cli.cf.mass_delete", nDel, localFiles, nDel * 100 / localFiles));
        msg.AppendLine().Append(Tr("cli.cf.proceed"));
        if (MessageBox.Show(msg.ToString(), Tr("cli.cf.title"), MessageBoxButtons.YesNo,
                massDelete ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
                massDelete ? MessageBoxDefaultButton.Button2 : MessageBoxDefaultButton.Button1) != DialogResult.Yes) return;

        SaveClientCfg();
        SetBusy(true);
        _cts = new CancellationTokenSource();
        SyncResult? res = null;
        try
        {
            using var rc = new RemoteClient(host, port, token);
            Log(Tr("cli.log.sync_start", nCopy, PathUtil.FormatBytes(plan.DownloadBytes)));
            res = await Syncer.ApplyAsync(plan, rc, opt, new Progress<ProgressInfo>(OnProgress), Log, _cts.Token);
        }
        catch (Exception e) { Log(Tr("cli.log.error", e.Message)); Warn(Tr("cli.msg.sync_aborted", e.Message)); }
        finally { SetBusy(false); }

        if (res == null) return;
        var secs = _opSw.Elapsed.TotalSeconds;
        var summary = res.Cancelled
            ? Tr("cli.sum.sync_cancelled")
            : Tr("cli.sum.sync_result", res.Copied, PathUtil.FormatBytes(res.BytesCopied), secs, res.Touched, res.Deleted, res.DirsDeleted, res.Errors.Count);
        Log(summary);
        foreach (var err in res.Errors.Take(50)) Log(Tr("cli.log.err_item", err));

        if (res.Errors.Count > 0)
            Warn(Tr("cli.msg.sync_errors", summary, string.Join("\n", res.Errors.Take(8))));
        else if (!res.Cancelled)
            MessageBox.Show(Tr(plan.DeselectedCount > 0 ? "cli.msg.sync_done_excl" : "cli.msg.sync_done", summary),
                "NetSynq", MessageBoxButtons.OK, MessageBoxIcon.Information);

        await RunCompareAsync(); // verifica finale
    }

    // ------------------------------------------------------------------ diff del contenuto
    private void OpenContentDiff(int row)
    {
        if (row < 0 || row >= _view.Count) return;
        var it = _view[row];
        if (it.IsDir) return;
        var host = _txtHost.Text.Trim(); var port = (int)_numPort.Value; var token = _txtToken.Text.Trim();
        var folder = _txtLocal.Text.Trim();
        var localFull = it.Local != null ? PathUtil.Resolve(folder, it.Path) : null;
        using var dlg = new DiffViewForm(it, localFull, () => new RemoteClient(host, port, token));
        dlg.ShowDialog(this);
    }
}
