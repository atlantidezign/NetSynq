using System.Net;
using NetSynq.Core;
using static NetSynq.Core.I18n;

namespace NetSynq.UI;

/// <summary>Finestra principale: tre schede: "Slave" (sincronizza), "Master" (pubblica la cartella) e "Compatta" (zippa cartelle con molti file piccoli).</summary>
public sealed partial class MainForm : Form
{
    private readonly AppSettings _cfg = AppSettings.Load();
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private TabPage[] _logicalTabs = Array.Empty<TabPage>();
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        Font = new Font("Consolas", 9f), Tag = "theme:log"
    };
    private readonly HashCache _serverCache = new(Path.Combine(AppSettings.DataDir, "hash-master.json"));
    private readonly HashCache _clientCache = new(Path.Combine(AppSettings.DataDir, "hash-slave.json"));

    public MainForm()
    {
        var ver = typeof(MainForm).Assembly.GetName().Version;
        Text = ver == null ? "NetSynq" : $"NetSynq v{ver.Major}.{ver.Minor}";
        Width = 1240; Height = 860; MinimumSize = new Size(1000, 680);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);
        AppIcon.Apply(this);

        BackColor = Theme.Bg;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Theme.Bg };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));

        var tabClient = Loc.Page("app.tab.slave");
        var tabServer = Loc.Page("app.tab.master");
        var tabCompact = Loc.Page("app.tab.compact");
        // ordine: Master, Slave, Compatta
        _tabs.TabPages.Add(tabServer);
        _tabs.TabPages.Add(tabClient);
        _tabs.TabPages.Add(tabCompact);
        _logicalTabs = new[] { tabClient, tabServer, tabCompact }; // stesso ordine usato nelle impostazioni salvate
        BuildClientTab(tabClient);
        BuildServerTab(tabServer);
        BuildCompactTab(tabCompact);
        _tabs.SelectedTab = _logicalTabs[Math.Clamp(_cfg.SelectedTab, 0, 2)];
        var tabStrip = Theme.BuildTabStrip(_tabs, tabServer, tabClient, tabCompact);

        var logBox = Loc.Text(new GroupBox { Dock = DockStyle.Fill, Padding = new Padding(6, 4, 6, 6) }, "app.log.title");
        logBox.Controls.Add(_log);

        _log.BackColor = Theme.LogBg;
        _log.BorderStyle = BorderStyle.FixedSingle;
        var header = Theme.Header("app.subtitle", out var cmbLang, out var cmbTheme);
        _cmbLang = cmbLang;
        _cmbTheme = cmbTheme;
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(tabStrip, 0, 1);
        root.Controls.Add(_tabs, 0, 2);
        root.Controls.Add(logBox, 0, 3);
        Controls.Add(root);

        // selettore della lingua (a destra, sulla riga del logo)
        _cmbLang.SelectedIndex = Math.Max(0, Array.IndexOf(I18n.Languages, I18n.Lang));
        _cmbLang.SelectedIndexChanged += (_, _) =>
        {
            var code = I18n.Languages[Math.Clamp(_cmbLang.SelectedIndex, 0, I18n.Languages.Length - 1)];
            if (code == I18n.Lang) return;
            _cfg.Language = code;
            I18n.SetLanguage(code); // → ApplyLanguage()
        };
        I18n.Changed += ApplyLanguage;

        // selettore del tema chiaro/scuro (accanto a quello della lingua)
        _cmbTheme.SelectedIndex = Theme.Dark ? 1 : 0;
        _cmbTheme.SelectedIndexChanged += (_, _) =>
        {
            bool dark = _cmbTheme.SelectedIndex == 1;
            if (dark == Theme.Dark) return;
            _cfg.Dark = dark;
            Theme.SetDark(dark); // → OnThemeChanged()
        };
        Theme.Changed += OnThemeChanged;
        Theme.Attach(this); // colori dei controlli standard e barra del titolo

        Load += (_, _) => { RefreshNics(); };
        FormClosing += OnFormClosing;
        Log(Tr("app.started"));
    }

    private readonly ComboBox _cmbLang;
    private readonly ComboBox _cmbTheme;

    /// <summary>Dopo il cambio di tema: ridisegna griglie e riscrive i testi/colori che dipendono dallo stato (es. stato del server).</summary>
    private void OnThemeChanged()
    {
        if (IsDisposed) return;
        RefreshServerLanguage();
        _grid.Invalidate();
        _cpGrid.Invalidate();
    }

    /// <summary>Riscrive tutti i testi collegati al dizionario e quelli che dipendono dallo stato corrente.</summary>
    private void ApplyLanguage()
    {
        if (IsDisposed) return;
        SuspendLayout();
        Loc.Refresh();
        RefreshServerLanguage();
        RefreshClientLanguage();
        RefreshCompactLanguage();
        ResumeLayout(true);
        PerformLayout();
        Log(Tr("app.language.changed"));
    }

    // ------------------------------------------------------------------ utilità comuni
    private void Log(string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(new Action(() => Log(message))); } catch { } return; }
        _log.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
        if (_log.TextLength > 300_000) _log.Text = _log.Text[^150_000..];
    }

    private static void Warn(string text) =>
        MessageBox.Show(text, "NetSynq", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    /// <summary>Suggerimento breve in grigio, sempre visibile accanto/sotto un campo.</summary>
    /// <param name="key">chiave del dizionario del testo.</param>
    private static Label Hint(string key) => Loc.Text(new Label
    {
        AutoSize = true, Tag = "muted", Margin = new Padding(3, 6, 3, 3), MaximumSize = new Size(900, 0)
    }, key);

    /// <param name="key">chiave del dizionario del testo.</param>
    private static Label MakeLabel(string key, bool bold = false) => Loc.Text(new Label
    {
        AutoSize = true, Anchor = AnchorStyles.Left,
        Font = bold ? new Font("Segoe UI", 9f, FontStyle.Bold) : null!, Margin = new Padding(3, 6, 6, 3)
    }, key);

    /// <summary>Un controllo che si espande + alcuni pulsanti a destra, sulla stessa riga.</summary>
    private static Control Pair(Control fill, params Control[] buttons)
    {
        var t = new TableLayoutPanel { ColumnCount = 1 + buttons.Length, RowCount = 1, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fill.Dock = DockStyle.Fill;
        t.Controls.Add(fill, 0, 0);
        for (int i = 0; i < buttons.Length; i++)
        {
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            buttons[i].Anchor = AnchorStyles.Left;
            t.Controls.Add(buttons[i], i + 1, 0);
        }
        return t;
    }

    /// <summary>
    /// Dimensiona il contenuto di una scheda: occupa tutta l'area visibile, ma non meno di quanto serve
    /// (parti fisse + altezza minima della lista). Se non basta lo spazio, la scheda scorre in verticale.
    /// </summary>
    /// <summary>Pila "riga alta" + riga con pulsanti di selezione a sinistra e Annulla tutto a destra.</summary>
    private static Control StackWithCancel(Control top, Control selection, Control cancel)
    {
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        selection.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        cancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        cancel.Margin = new Padding(3, 3, 6, 3);
        bottom.Controls.Add(selection, 0, 0);
        bottom.Controls.Add(cancel, 1, 0);
        var stack = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        top.Dock = DockStyle.Fill;
        stack.Controls.Add(top, 0, 0);
        stack.Controls.Add(bottom, 0, 1);
        return stack;
    }

    private static Action MakeFit(TabPage page, Control root, Control[] fixedRows, int tail, int minList)
    {
        bool busy = false;
        void Fit()
        {
            if (busy) return;
            busy = true;
            try
            {
                var pad = page.Padding;
                int w = Math.Max(300, page.ClientSize.Width - pad.Horizontal);
                int need = tail + minList + 16;
                foreach (var c in fixedRows) need += c.GetPreferredSize(new Size(w, 0)).Height + c.Margin.Vertical;
                int h = Math.Max(page.ClientSize.Height - pad.Vertical, need);
                var pos = page.AutoScrollPosition; // negativo quando la scheda è scorsa
                root.SetBounds(pad.Left + pos.X, pad.Top + pos.Y, w, h);
            }
            finally { busy = false; }
        }
        page.Layout += (_, _) => Fit();
        page.Resize += (_, _) => Fit();
        return Fit;
    }

    /// <summary>Tabella a tre colonne: etichetta | campo | suggerimento a destra.</summary>
    private static TableLayoutPanel FieldTable()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return t;
    }

    /// <param name="label">chiave dell'etichetta; <paramref name="hint"/>: chiave del suggerimento (null = nessuno).</param>
    private static void AddField(TableLayoutPanel t, string label, Control c, string? hint = null, int hintWidth = 560)
    {
        int r = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var lbl = MakeLabel(label);
        lbl.Anchor = AnchorStyles.Top | AnchorStyles.Left; // etichetta allineata in alto anche se la riga è alta
        t.Controls.Add(lbl, 0, r);
        if (c is TextBox) c.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        c.Margin = new Padding(3);
        t.Controls.Add(c, 1, r);
        if (hint == null) { t.SetColumnSpan(c, 2); return; }
        var h = Hint(hint);
        h.MaximumSize = new Size(hintWidth, 0);
        h.Margin = new Padding(8, 7, 3, 3);
        t.Controls.Add(h, 2, r);
    }

    /// <param name="key">chiave del dizionario del testo.</param>
    private static Button MakeButton(string key, int width = 0, ButtonKind kind = ButtonKind.Secondary)
    {
        var b = Loc.Text(new Button { AutoSize = width == 0, Height = 30, Margin = new Padding(4, 3, 0, 3) }, key);
        if (width > 0) b.Width = width;
        else b.Padding = new Padding(8, 0, 8, 0);
        Theme.StyleButton(b, kind);
        return b;
    }

    /// <param name="description">testo già tradotto da chi chiama.</param>
    private static string? PickFolder(string current, string description)
    {
        using var dlg = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (Directory.Exists(current)) dlg.SelectedPath = current;
        return dlg.ShowDialog() == DialogResult.OK ? dlg.SelectedPath : null;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_busy)
        {
            if (MessageBox.Show(Tr("app.closing.busy"), "NetSynq",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) { e.Cancel = true; return; }
            _cts?.Cancel();
        }
        I18n.Changed -= ApplyLanguage;
        Theme.Changed -= OnThemeChanged;
        _cfg.SelectedTab = Math.Max(0, Array.IndexOf(_logicalTabs, _tabs.SelectedTab));
        SaveServerCfg();
        SaveClientCfg();
        SaveCompactCfg();
        StopServer();
        _clientCache.Save();
        _serverCache.Save();
    }
}
