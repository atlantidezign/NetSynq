using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NetSynq.UI;

internal enum ButtonKind { Secondary, Primary, Danger }

/// <summary>
/// Palette (chiara/scura) e stili comuni dell'interfaccia (solo WinForms, nessuna dipendenza esterna).
/// I colori sono proprietà: cambiano con <see cref="SetDark"/>. I controlli con colori propri si registrano con
/// <see cref="Bind{T}"/> (riapplicati a ogni cambio); quelli standard (etichette, caselle di testo, pannelli…) sono
/// ricolorati in blocco da <see cref="ApplyTo"/>. Etichette speciali: Tag = "muted" (grigio tenue); controlli che si
/// colorano da soli: Tag = "theme:custom"; la casella del registro: Tag = "theme:log".
/// </summary>
internal static class Theme
{
    private sealed class Palette
    {
        public Color Navy, Accent, AccentDark, AccentSoft, AccentPress, Bg, Surface, Border, Text, Muted, Danger, LogBg;
        public Color BarBack, BarText, GridLine, HeaderBack, SelBack, AltRow, Ok, Warn;
        public Color DisabledBack, DisabledFore, DangerBorder, DangerHover, DangerDown, BtnDown;
        public Color RowNew, RowModified, RowTouch, RowDelete, RowDeleteOff, RowOffBack;
        public Color DiffDelFore, DiffDelBack, DiffAddFore, DiffAddBack;
    }

    private static readonly Palette Light = new()
    {
        Navy = Color.FromArgb(11, 58, 106), Accent = Color.FromArgb(10, 102, 216), AccentDark = Color.FromArgb(7, 80, 176),
        AccentSoft = Color.FromArgb(232, 241, 253), AccentPress = Color.FromArgb(11, 58, 106),
        Bg = Color.FromArgb(244, 246, 250), Surface = Color.White, Border = Color.FromArgb(205, 212, 222),
        Text = Color.FromArgb(31, 41, 55), Muted = Color.FromArgb(107, 114, 128), Danger = Color.FromArgb(180, 35, 24),
        LogBg = Color.FromArgb(250, 251, 253),
        BarBack = Color.White, BarText = Color.FromArgb(107, 114, 128),
        GridLine = Color.FromArgb(232, 236, 242), HeaderBack = Color.FromArgb(236, 241, 248), SelBack = Color.FromArgb(214, 229, 250),
        AltRow = Color.FromArgb(250, 251, 253), Ok = Color.SeaGreen, Warn = Color.DarkOrange,
        DisabledBack = Color.FromArgb(226, 229, 235), DisabledFore = Color.FromArgb(150, 156, 168),
        DangerBorder = Color.FromArgb(224, 164, 160), DangerHover = Color.FromArgb(254, 242, 241), DangerDown = Color.FromArgb(252, 226, 224),
        BtnDown = Color.FromArgb(214, 229, 250),
        RowNew = Color.FromArgb(226, 244, 230), RowModified = Color.FromArgb(255, 241, 208), RowTouch = Color.FromArgb(226, 238, 250),
        RowDelete = Color.FromArgb(252, 226, 226), RowDeleteOff = Color.FromArgb(240, 240, 240), RowOffBack = Color.FromArgb(243, 243, 243),
        DiffDelFore = Color.FromArgb(170, 30, 30), DiffDelBack = Color.FromArgb(253, 232, 232),
        DiffAddFore = Color.FromArgb(20, 110, 40), DiffAddBack = Color.FromArgb(228, 246, 232)
    };

    private static readonly Palette Dark_ = new()
    {
        Navy = Color.FromArgb(143, 190, 250), Accent = Color.FromArgb(46, 125, 225), AccentDark = Color.FromArgb(36, 100, 190),
        AccentSoft = Color.FromArgb(30, 46, 72), AccentPress = Color.FromArgb(24, 70, 140),
        Bg = Color.FromArgb(24, 26, 31), Surface = Color.FromArgb(34, 37, 44), Border = Color.FromArgb(68, 74, 86),
        Text = Color.FromArgb(226, 230, 237), Muted = Color.FromArgb(148, 157, 171), Danger = Color.FromArgb(240, 120, 110),
        LogBg = Color.FromArgb(19, 21, 26),
        BarBack = Color.FromArgb(165, 173, 187), BarText = Color.FromArgb(40, 48, 62),
        GridLine = Color.FromArgb(50, 55, 65), HeaderBack = Color.FromArgb(40, 45, 56), SelBack = Color.FromArgb(44, 66, 102),
        AltRow = Color.FromArgb(30, 33, 40), Ok = Color.FromArgb(80, 190, 130), Warn = Color.FromArgb(240, 160, 60),
        DisabledBack = Color.FromArgb(46, 50, 58), DisabledFore = Color.FromArgb(110, 117, 130),
        DangerBorder = Color.FromArgb(140, 70, 66), DangerHover = Color.FromArgb(58, 34, 34), DangerDown = Color.FromArgb(74, 38, 38),
        BtnDown = Color.FromArgb(40, 62, 98),
        RowNew = Color.FromArgb(28, 58, 40), RowModified = Color.FromArgb(70, 56, 24), RowTouch = Color.FromArgb(30, 50, 74),
        RowDelete = Color.FromArgb(74, 34, 34), RowDeleteOff = Color.FromArgb(46, 46, 50), RowOffBack = Color.FromArgb(40, 42, 48),
        DiffDelFore = Color.FromArgb(255, 140, 140), DiffDelBack = Color.FromArgb(74, 34, 34),
        DiffAddFore = Color.FromArgb(130, 220, 150), DiffAddBack = Color.FromArgb(28, 62, 40)
    };

    private static Palette P = Light;
    public static bool Dark { get; private set; }

    public static Color Navy => P.Navy;
    public static Color Accent => P.Accent;
    public static Color AccentDark => P.AccentDark;
    public static Color AccentSoft => P.AccentSoft;
    public static Color AccentPress => P.AccentPress;
    public static Color Bg => P.Bg;
    /// <summary>Fondo di caselle, griglie, pulsanti e pannelli "in rilievo" (bianco nel tema chiaro).</summary>
    public static Color Surface => P.Surface;
    /// <summary>Fondo della barra del logo: più chiaro nel tema scuro, così la scritta originale resta leggibile.</summary>
    public static Color BarBack => P.BarBack;
    public static Color BarText => P.BarText;
    public static Color Border => P.Border;
    public static Color Text => P.Text;
    public static Color Muted => P.Muted;
    public static Color Danger => P.Danger;
    public static Color LogBg => P.LogBg;
    public static Color Ok => P.Ok;
    public static Color Warn => P.Warn;
    public static Color RowNew => P.RowNew;
    public static Color RowModified => P.RowModified;
    public static Color RowTouch => P.RowTouch;
    public static Color RowDelete => P.RowDelete;
    public static Color RowDeleteOff => P.RowDeleteOff;
    public static Color RowOffBack => P.RowOffBack;
    public static Color DiffDelFore => P.DiffDelFore;
    public static Color DiffDelBack => P.DiffDelBack;
    public static Color DiffAddFore => P.DiffAddFore;
    public static Color DiffAddBack => P.DiffAddBack;

    /// <summary>Sollevato dopo ogni cambio di tema (dopo aver riapplicato i controlli registrati).</summary>
    public static event Action? Changed;

    public static void SetDark(bool dark)
    {
        if (dark == Dark) return;
        Dark = dark;
        P = dark ? Dark_ : Light;
        RefreshBinds();
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ registro dei controlli con colori propri
    private static readonly List<(WeakReference<Control> Ref, Action<Control> Apply)> Binds = new();

    /// <summary>Applica subito <paramref name="apply"/> al controllo e di nuovo a ogni cambio di tema (senza trattenere il controllo in memoria).</summary>
    public static T Bind<T>(T c, Action<T> apply) where T : Control
    {
        Binds.Add((new WeakReference<Control>(c), x => apply((T)x)));
        apply(c);
        return c;
    }

    private static void RefreshBinds()
    {
        Binds.RemoveAll(b => !b.Ref.TryGetTarget(out var c) || c.IsDisposed);
        foreach (var b in Binds.ToArray())
            if (b.Ref.TryGetTarget(out var c) && !c.IsDisposed) { try { b.Apply(c); } catch { /* controllo in chiusura */ } }
    }

    // ------------------------------------------------------------------ controlli standard
    private static readonly ConditionalWeakTable<Control, object> Hooked = new();

    /// <summary>Ricolora in blocco i controlli standard sotto <paramref name="root"/> (pannelli, etichette, caselle, elenchi, spunte…).</summary>
    public static void ApplyTo(Control root)
    {
        var tag = root.Tag as string;
        if (tag != "theme:custom")
        {
            switch (root)
            {
                case Label l: l.ForeColor = tag == "muted" ? Muted : Text; break;
                case TextBoxBase t:
                    t.BackColor = tag == "theme:log" ? LogBg : Surface; t.ForeColor = Text;
                    if (t.Multiline) NativeScroll(t);
                    break;
                case NumericUpDown n: n.BackColor = Surface; n.ForeColor = Text; break;
                case ComboBox cb: cb.BackColor = Surface; cb.ForeColor = Text; cb.FlatStyle = Dark ? FlatStyle.Flat : FlatStyle.Standard; break;
                case CheckBox or RadioButton: root.ForeColor = Text; break;
                case Button or DataGridView or PictureBox or TabControl: break; // pulsanti e griglie: Bind; scheda: nessun colore proprio
                case GroupBox g: g.ForeColor = Text; break;
                case Form or TabPage or Panel:
                    root.BackColor = Bg; root.ForeColor = Text;
                    if (root is ScrollableControl { AutoScroll: true }) NativeScroll(root);
                    break;
            }
        }
        foreach (Control c in root.Controls) ApplyTo(c);
    }

    /// <summary>Barre di scorrimento e barra del titolo scure (se Windows le supporta); altrimenti nessun effetto.</summary>
    private static void NativeScroll(Control c)
    {
        if (c.IsHandleCreated) { NativeTheme.Scrollbars(c, Dark); return; }
        if (Hooked.TryGetValue(c, out _)) return;
        Hooked.Add(c, new object());
        c.HandleCreated += (_, _) => NativeTheme.Scrollbars(c, Dark);
    }

    /// <summary>Applica il tema a una finestra e lo mantiene aggiornato finché è aperta (titolo scuro compreso).</summary>
    public static void Attach(Form f)
    {
        void Apply() { ApplyTo(f); NativeTheme.TitleBar(f, Dark); f.Invalidate(true); }
        f.HandleCreated += (_, _) => NativeTheme.TitleBar(f, Dark);
        Action handler = Apply;
        Changed += handler;
        f.FormClosed += (_, _) => Changed -= handler;
        Apply();
    }

    // ------------------------------------------------------------------ logo
    private static Image? _logo;
    private static bool _logoLoaded;

    /// <summary>Scritta "NetSynq" incorporata nell'exe (null se non disponibile). È la stessa in entrambi i temi.</summary>
    public static Image? Logo
    {
        get
        {
            if (_logoLoaded) return _logo;
            _logoLoaded = true;
            try
            {
                var asm = typeof(Theme).Assembly;
                var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("logo.png", StringComparison.OrdinalIgnoreCase));
                if (name != null)
                {
                    using var s = asm.GetManifestResourceStream(name)!;
                    using var tmp = Image.FromStream(s);
                    _logo = new Bitmap(tmp);
                }
            }
            catch { _logo = null; }
            return _logo;
        }
    }

    // ------------------------------------------------------------------ pulsanti, griglie, riquadri
    public static void StyleButton(Button b, ButtonKind kind)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.UseVisualStyleBackColor = false;
        b.FlatAppearance.BorderSize = 1;
        if (kind == ButtonKind.Primary)
        {
            b.Font = new Font(b.Font, FontStyle.Bold);
            b.EnabledChanged += (_, _) => ColorButton(b, kind);
        }
        Bind(b, x => ColorButton(x, kind));
    }

    private static void ColorButton(Button b, ButtonKind kind)
    {
        var p = P;
        b.Cursor = b.Enabled ? Cursors.Hand : Cursors.Default;
        switch (kind)
        {
            case ButtonKind.Primary:
                b.BackColor = b.Enabled ? p.Accent : p.DisabledBack;
                b.ForeColor = b.Enabled ? Color.White : p.DisabledFore;
                b.FlatAppearance.BorderColor = b.Enabled ? p.Accent : p.DisabledBack;
                b.FlatAppearance.MouseOverBackColor = p.AccentDark;
                b.FlatAppearance.MouseDownBackColor = p.AccentPress;
                break;
            case ButtonKind.Danger:
                b.BackColor = p.Surface; b.ForeColor = p.Danger;
                b.FlatAppearance.BorderColor = p.DangerBorder;
                b.FlatAppearance.MouseOverBackColor = p.DangerHover;
                b.FlatAppearance.MouseDownBackColor = p.DangerDown;
                break;
            default:
                b.BackColor = p.Surface; b.ForeColor = p.Text;
                b.FlatAppearance.BorderColor = p.Border;
                b.FlatAppearance.MouseOverBackColor = p.AccentSoft;
                b.FlatAppearance.MouseDownBackColor = p.BtnDown;
                break;
        }
    }

    public static void StyleGrid(DataGridView g)
    {
        g.BorderStyle = BorderStyle.FixedSingle;
        g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.ColumnHeadersHeight = 30;
        g.ColumnHeadersDefaultCellStyle.Font = new Font(g.Font, FontStyle.Bold);
        g.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
        g.DefaultCellStyle.Padding = new Padding(3, 0, 3, 0);
        g.RowTemplate.Height = 26;
        Bind(g, ColorGrid);
    }

    /// <summary>Colori della griglia secondo il tema corrente (le righe colorate per stato si calcolano al volo nei CellFormatting).</summary>
    public static void ColorGrid(DataGridView g)
    {
        var p = P;
        g.EnableHeadersVisualStyles = false;
        g.BackgroundColor = p.Surface;
        g.GridColor = p.GridLine;
        g.ColumnHeadersDefaultCellStyle.BackColor = p.HeaderBack;
        g.ColumnHeadersDefaultCellStyle.ForeColor = p.Navy;
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = p.HeaderBack;
        g.ColumnHeadersDefaultCellStyle.SelectionForeColor = p.Navy;
        g.DefaultCellStyle.BackColor = p.Surface;
        g.DefaultCellStyle.ForeColor = p.Text;
        g.DefaultCellStyle.SelectionBackColor = p.SelBack;
        g.DefaultCellStyle.SelectionForeColor = p.Text;
        g.AlternatingRowsDefaultCellStyle.BackColor = p.AltRow;
        NativeScroll(g);
        g.Invalidate();
    }

    /// <summary>Riquadro informativo con barra colorata a sinistra. I colori sono funzioni, rivalutate a ogni cambio di tema.</summary>
    public static Control Card(Label content, Func<Color> back, Func<Color> bar)
    {
        var t = new TableLayoutPanel
        {
            ColumnCount = 2, RowCount = 1, AutoSize = true, Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 0, 4), Tag = "theme:custom"
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 4));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var strip = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), Tag = "theme:custom" };
        t.Controls.Add(strip, 0, 0);
        content.Margin = new Padding(10, 8, 10, 8);
        content.BackColor = Color.Transparent;
        t.Controls.Add(content, 1, 0);
        Bind(t, x => x.BackColor = back());
        Bind(strip, x => x.BackColor = bar());
        return t;
    }

    /// <summary>Intestazione con la scritta del nome dell'applicazione.</summary>
    public static Control Header(string subtitleKey, out ComboBox language, out ComboBox theme)
    {
        var p = new Panel { Dock = DockStyle.Fill, Tag = "theme:custom" };
        Bind(p, x => { x.BackColor = BarBack; x.Invalidate(); });
        p.Paint += (_, e) =>
        {
            using var pen = new Pen(Border);
            e.Graphics.DrawLine(pen, 0, p.Height - 1, p.Width, p.Height - 1);
        };
        int x = 16;
        var logo = Logo;
        if (logo != null)
        {
            int h = 30, w = (int)Math.Round(logo.Width * (double)h / logo.Height);
            p.Controls.Add(new PictureBox { Image = logo, SizeMode = PictureBoxSizeMode.Zoom, Left = x, Top = 13, Width = w, Height = h, BackColor = Color.Transparent });
            x += w + 16;
        }
        else
        {
            var l = new Label { Text = "NetSynq", AutoSize = true, Left = x, Top = 10, Font = new Font("Segoe UI", 18f, FontStyle.Bold), Tag = "theme:custom", ForeColor = Color.FromArgb(11, 58, 106) };
            p.Controls.Add(l);
            x += 120;
        }
        p.Controls.Add(Bind(Loc.Text(new Label { AutoSize = true, Left = x, Top = 20, Tag = "theme:custom" }, subtitleKey), y => y.ForeColor = BarText));
        // selettore lingua, tutto a destra sulla riga del logo
        var cmb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 64 };
        foreach (var l in NetSynq.Core.I18n.Languages) cmb.Items.Add(l.ToUpperInvariant());
        // selettore del tema (chiaro/scuro), subito a sinistra di quello della lingua
        var thm = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 96 };
        Loc.Combo(thm, "app.theme.light", "app.theme.dark");
        void Place()
        {
            cmb.Left = p.ClientSize.Width - cmb.Width - 16; cmb.Top = (p.ClientSize.Height - cmb.Height) / 2;
            thm.Left = cmb.Left - thm.Width - 8; thm.Top = cmb.Top;
        }
        p.Resize += (_, _) => Place();
        p.Controls.Add(cmb);
        p.Controls.Add(thm);
        Place();
        language = cmb;
        theme = thm;
        return p;
    }

    /// <summary>
    /// Barra delle schede personalizzata: nasconde le linguette standard del TabControl e le sostituisce con pulsanti piatti.
    /// Gli elementi sono TabPage (pulsante) oppure int (spazio vuoto in pixel tra due pulsanti).
    /// </summary>
    public static Control BuildTabStrip(TabControl tabs, params object[] items)
    {
        tabs.Appearance = TabAppearance.Normal;
        tabs.SizeMode = TabSizeMode.Fixed;
        tabs.ItemSize = new Size(0, 1);
        tabs.Padding = new Point(0, 0);
        foreach (TabPage p in tabs.TabPages) p.UseVisualStyleBackColor = false;

        var strip = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, WrapContents = false, FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0), Padding = new Padding(10, 0, 0, 0), Tag = "theme:custom"
        };
        Bind(strip, x => { x.BackColor = Bg; x.Invalidate(); });
        strip.Paint += (_, e) =>
        {
            using var pen = new Pen(Border);
            e.Graphics.DrawLine(pen, 0, strip.Height - 1, strip.Width, strip.Height - 1);
        };
        var buttons = new List<TabButton>();
        foreach (var it in items)
        {
            if (it is TabPage page) { var b = new TabButton(tabs, page); buttons.Add(b); strip.Controls.Add(b); }
            else if (it is int gap) strip.Controls.Add(new Panel { Width = gap, Height = 34, Margin = new Padding(0) });
        }
        tabs.SelectedIndexChanged += (_, _) => { foreach (var b in buttons) b.Invalidate(); };
        return strip;
    }

    private sealed class TabButton : Control
    {
        private readonly TabControl _tabs;
        private readonly TabPage _page;
        private bool _hover;

        public TabButton(TabControl tabs, TabPage page)
        {
            _tabs = tabs; _page = page;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand; TabStop = false; Margin = new Padding(0);
            Bind(this, x => x.Invalidate());
            using var f = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            Width = TextRenderer.MeasureText(page.Text.Trim(), f).Width + 44;
            Height = 34;
            page.TextChanged += (_, _) =>
            {
                using var f2 = new Font("Segoe UI", 9.5f, FontStyle.Bold);
                Width = TextRenderer.MeasureText(_page.Text.Trim(), f2).Width + 44;
                Invalidate();
            };
            Click += (_, _) => _tabs.SelectedTab = _page;
            MouseEnter += (_, _) => { _hover = true; Invalidate(); };
            MouseLeave += (_, _) => { _hover = false; Invalidate(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            bool sel = _tabs.SelectedTab == _page;
            using (var br = new SolidBrush(sel ? Surface : _hover ? AccentSoft : Bg)) e.Graphics.FillRectangle(br, ClientRectangle);
            using var f = new Font("Segoe UI", 9.5f, sel ? FontStyle.Bold : FontStyle.Regular);
            TextRenderer.DrawText(e.Graphics, _page.Text.Trim(), f, ClientRectangle, sel ? Navy : Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (sel) using (var br = new SolidBrush(Accent)) e.Graphics.FillRectangle(br, 0, Height - 3, Width, 3);
        }
    }
}
