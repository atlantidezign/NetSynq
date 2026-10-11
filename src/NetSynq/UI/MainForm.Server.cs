using System.Net;
using NetSynq.Core;
using static NetSynq.Core.I18n;
using Timer = System.Windows.Forms.Timer;

namespace NetSynq.UI;

public sealed partial class MainForm
{
    private readonly TextBox _txtSrvFolder = new();
    private readonly ComboBox _cmbNic = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _txtPeer = new();
    private readonly NumericUpDown _numSrvPort = new() { Minimum = 1024, Maximum = 65535, Width = 80 };
    private readonly TextBox _txtSrvToken = new();
    private readonly TextBox _txtSrvExcl = new();
    private readonly Button _btnSrvToggle = MakeButton("srv.btn.start", 160, ButtonKind.Primary);
    private readonly Label _lblSrvStatus = new() { AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), ForeColor = Theme.Muted, Tag = "theme:custom" };
    private readonly Label _lblSrvInfo = new() { AutoSize = true, Visible = false };
    private readonly Label _lblNicWarn = new() { AutoSize = true, ForeColor = Theme.Warn, Visible = false, Tag = "theme:custom" };
    private readonly Timer _srvTimer = new() { Interval = 1000 };
    private readonly List<Control> _srvInputs = new();

    private List<NicInfo> _nics = new();
    private SyncServer? _server;
    private Discovery.Responder? _responder;
    private bool _suppressNicEvents;

    private void BuildServerTab(TabPage page)
    {
        page.Padding = new Padding(10, 8, 10, 6);
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoScroll = true };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // il suggerimento sta sulla stessa riga, a destra del campo: meno altezza, niente scorrimento
        void AddRow(string? label, Control c, string? hint = null, params object?[] hintArgs)
        {
            int r = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var lbl = label == null ? new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) } : MakeLabel(label);
            lbl.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            t.Controls.Add(lbl, 0, r);
            c.Margin = new Padding(3, 3, 3, 3);
            t.Controls.Add(c, 1, r);
            if (hint == null) t.SetColumnSpan(c, 2);
            else
            {
                var h = Loc.Text(new Label { AutoSize = true, Tag = "muted", Margin = new Padding(3, 6, 3, 3) }, hint, hintArgs);
                h.MaximumSize = new Size(520, 0);
                h.Margin = new Padding(8, 7, 3, 3);
                t.Controls.Add(h, 2, r);
            }
        }

        var tip = new ToolTip { AutoPopDelay = 20000 };

        var btnBrowse = MakeButton("srv.btn.browse");
        btnBrowse.Click += (_, _) => { var p = PickFolder(_txtSrvFolder.Text, Tr("srv.dlg.pickfolder")); if (p != null) _txtSrvFolder.Text = p; };
        AddRow("srv.lbl.folder", Pair(_txtSrvFolder, btnBrowse), "srv.hint.folder");

        var btnRefresh = MakeButton("srv.btn.refresh");
        btnRefresh.Click += (_, _) => RefreshNics();
        AddRow("srv.lbl.nic", Pair(_cmbNic, btnRefresh), "srv.hint.nic");
        AddRow(null, _lblNicWarn);

        _txtPeer.Width = 160;
        var peerPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        peerPanel.Controls.Add(_txtPeer);
        AddRow("srv.lbl.peer", peerPanel, "srv.hint.peer");

        AddRow("srv.lbl.port", _numSrvPort, "srv.hint.port");

        var btnGen = MakeButton("srv.btn.gen");
        btnGen.Click += (_, _) => _txtSrvToken.Text = AppSettings.NewToken();
        var btnCopy = MakeButton("srv.btn.copy");
        btnCopy.Click += (_, _) => { try { Clipboard.SetText(_txtSrvToken.Text); Log(Tr("srv.log.token_copied")); } catch { } };
        _txtSrvToken.Font = new Font("Consolas", 10f);
        _txtSrvToken.MaxLength = 64;
        AddRow("srv.lbl.token", Pair(_txtSrvToken, btnGen, btnCopy), "srv.hint.token", AppSettings.GeneratedTokenLength, AppSettings.MinTokenLength);

        AddRow("srv.lbl.excl", _txtSrvExcl, "srv.hint.excl");

        _btnSrvToggle.Click += (_, _) => ToggleServer();
        _btnSrvToggle.Margin = new Padding(3, 8, 3, 4);
        AddRow(null, _btnSrvToggle);

        AddRow(null, _lblSrvStatus);
        AddRow(null, _lblSrvInfo);
        page.Controls.Add(t);

        // istruzioni e note: solo come tooltip
        Loc.Tip(tip, _txtSrvFolder, "srv.tip.folder");
        Loc.Tip(tip, _cmbNic, "srv.tip.nic");
        Loc.Tip(tip, _txtPeer, "srv.tip.peer");
        Loc.Add(() => tip.SetToolTip(_txtSrvToken, Tr("srv.tip.token", AppSettings.GeneratedTokenLength, AppSettings.MinTokenLength)));
        Loc.Tip(tip, _txtSrvExcl, "srv.tip.excl");
        Loc.Tip(tip, _btnSrvToggle, "srv.tip.toggle");

        _srvInputs.AddRange(new Control[] { _txtSrvFolder, _cmbNic, _txtPeer, _numSrvPort, _txtSrvToken, _txtSrvExcl, btnBrowse, btnRefresh, btnGen });

        _txtSrvFolder.Text = _cfg.ServerFolder;
        _txtPeer.Text = _cfg.PeerHint;
        _numSrvPort.Value = Math.Clamp(_cfg.ServerPort, 1024, 65535);
        _txtSrvToken.Text = _cfg.ServerToken;
        _txtSrvExcl.Text = _cfg.ServerExcludes;

        _txtPeer.TextChanged += (_, _) => { if (!_suppressNicEvents) AutoPickNic(); };
        _cmbNic.SelectedIndexChanged += (_, _) => UpdateNicWarning();
        _srvTimer.Tick += (_, _) => UpdateServerStats();
        UpdateServerUi();
    }

    /// <summary>Riscrive i testi della scheda Master che dipendono dallo stato (non legati a una chiave).</summary>
    private void RefreshServerLanguage()
    {
        UpdateServerUi();
        UpdateServerStats();
        UpdateNicWarning();
    }

    private void SaveServerCfg()
    {
        _cfg.ServerFolder = _txtSrvFolder.Text.Trim();
        _cfg.PeerHint = _txtPeer.Text.Trim();
        _cfg.ServerPort = (int)_numSrvPort.Value;
        _cfg.ServerToken = _txtSrvToken.Text.Trim();
        _cfg.ServerExcludes = _txtSrvExcl.Text;
        if (_cmbNic.SelectedItem is NicInfo n) _cfg.ServerNic = n.Ip.ToString();
        _cfg.Save();
    }

    // ------------------------------------------------------------------ interfacce di rete
    private void RefreshNics()
    {
        _suppressNicEvents = true;
        try
        {
            _nics = NetworkHelper.GetCandidates();
            _cmbNic.Items.Clear();
            foreach (var n in _nics.OrderBy(n => n.Suspect).ThenBy(n => n.Ip.ToString())) _cmbNic.Items.Add(n);

            NicInfo? sel = _cmbNic.Items.Cast<NicInfo>().FirstOrDefault(n => n.Ip.ToString() == _cfg.ServerNic);
            if (sel == null) sel = NetworkHelper.Pick(_nics, ParsePeer());
            if (sel != null) _cmbNic.SelectedItem = sel;
            Log(sel != null ? Tr("srv.log.nics_found_sel", _nics.Count, sel.Ip, sel.Name) : Tr("srv.log.nics_found", _nics.Count));
        }
        finally { _suppressNicEvents = false; }
        UpdateNicWarning();
    }

    private IPAddress? ParsePeer() => IPAddress.TryParse(_txtPeer.Text.Trim(), out var ip) ? ip : null;

    private void AutoPickNic()
    {
        var peer = ParsePeer();
        if (peer == null) return;
        var pick = NetworkHelper.Pick(_nics, peer);
        if (pick != null && _cmbNic.Items.Contains(pick)) _cmbNic.SelectedItem = pick;
    }

    private void UpdateNicWarning()
    {
        _lblNicWarn.Text = _cmbNic.SelectedItem is NicInfo { Suspect: true }
            ? Tr("srv.nic.warn_vpn")
            : "";
        _lblNicWarn.Visible = _lblNicWarn.Text.Length > 0;
    }

    // ------------------------------------------------------------------ avvio / stop
    private void ToggleServer()
    {
        if (_server != null) { StopServer(); return; }

        var folder = _txtSrvFolder.Text.Trim();
        if (!Directory.Exists(folder)) { Warn(Tr("srv.warn.folder")); return; }
        if (_cmbNic.SelectedItem is not NicInfo nic) { Warn(Tr("srv.warn.nonic")); return; }
        var token = _txtSrvToken.Text.Trim();
        var tokErr = AppSettings.ValidateToken(token, true);
        if (tokErr != null) { Warn(tokErr); return; }
        if (nic.Suspect && MessageBox.Show(
                Tr("srv.msg.vpn_confirm"),
                "NetSynq", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        SaveServerCfg();
        try
        {
            var s = new SyncServer(folder, nic.Ip, (int)_numSrvPort.Value, token, new ExcludeMatcher(_txtSrvExcl.Text), _serverCache, Log);
            s.Start();
            _server = s;
            try { _responder = new Discovery.Responder(nic, s.Port, Environment.MachineName, Log); }
            catch (Exception e) { Log(Tr("srv.log.discovery_fail", e.Message)); }
        }
        catch (Exception e)
        {
            Log(Tr("srv.log.start_error", e.Message));
            MessageBox.Show(Tr("srv.msg.start_error", e.Message),
                "NetSynq", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        UpdateServerUi();
    }

    private void StopServer()
    {
        try { _responder?.Dispose(); } catch { }
        _responder = null;
        try { _server?.Stop(); } catch { }
        _server = null;
        UpdateServerUi();
    }

    private void UpdateServerUi()
    {
        bool running = _server != null;
        foreach (var c in _srvInputs) c.Enabled = !running;
        _btnSrvToggle.Text = Tr(running ? "srv.btn.stop" : "srv.btn.start");
        _lblSrvInfo.Visible = running;
        _srvTimer.Enabled = running;
        if (running && _cmbNic.SelectedItem is NicInfo nic)
        {
            _lblSrvStatus.ForeColor = Theme.Ok;
            _lblSrvStatus.Text = Tr("srv.status.on", nic.Ip, _server!.Port, _txtSrvToken.Text.Trim());
        }
        else
        {
            _lblSrvStatus.ForeColor = Theme.Muted;
            _lblSrvStatus.Text = Tr("srv.status.off");
            _lblSrvInfo.Text = "";
        }
    }

    private void UpdateServerStats()
    {
        if (_server == null) return;
        _lblSrvInfo.Text = Tr("srv.info.stats", _server.ActiveConnections, PathUtil.FormatBytes(_server.BytesSent));
    }
}
