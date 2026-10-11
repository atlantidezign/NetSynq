using System.Text;
using NetSynq.Core;
using static NetSynq.Core.I18n;

namespace NetSynq.UI;

/// <summary>
/// Confronto del contenuto di un singolo file: scarica la versione del master in un file temporaneo
/// e la confronta riga per riga con quella locale (file di testo fino a 8 MB).
/// </summary>
public sealed class DiffViewForm : Form
{
    private const long MaxTextBytes = 8L * 1024 * 1024;
    private const int MaxRenderedLines = 20000;

    private readonly DiffItem _item;
    private readonly string? _localFull;
    private readonly Func<RemoteClient> _rcFactory;
    private readonly RichTextBox _rtb = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, WordWrap = false, Font = new Font("Consolas", 9.5f),
        BackColor = Theme.Surface, ForeColor = Theme.Text, DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Both
    };
    private readonly Label _lblInfo = new() { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(4) };
    private readonly CheckBox _chkAll = new() { Text = Tr("diff.chk.all"), AutoSize = true };
    private readonly CancellationTokenSource _cts = new();
    private List<(DiffOp Op, string Line)>? _diff;

    public DiffViewForm(DiffItem item, string? localFull, Func<RemoteClient> rcFactory)
    {
        _item = item; _localFull = localFull; _rcFactory = rcFactory;
        Text = Tr("diff.title", item.Path);
        Width = 1000; Height = 700; StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        AppIcon.Apply(this);

        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        top.Controls.Add(_lblInfo, 0, 0);
        top.Controls.Add(_chkAll, 0, 1);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(top, 0, 0);
        root.Controls.Add(_rtb, 0, 1);
        Controls.Add(root);

        _chkAll.CheckedChanged += (_, _) => Render();
        Theme.Attach(this);
        Theme.Attach(this);
        Shown += async (_, _) => await LoadAsync();
        FormClosing += (_, _) => _cts.Cancel();
    }

    private static string Meta(Entry? e) =>
        e == null ? "—" : $"{PathUtil.FormatBytes(e.Size)}, {e.MTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";

    private static bool LooksBinary(byte[] b)
    {
        int n = Math.Min(b.Length, 8000);
        for (int i = 0; i < n; i++) if (b[i] == 0) return true;
        return false;
    }

    private static string Decode(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        try { return new UTF8Encoding(false, true).GetString(b); }
        catch { return Encoding.Latin1.GetString(b); }
    }

    private static string[] Lines(string s) => s.Replace("\r\n", "\n").Split('\n');

    private async Task LoadAsync()
    {
        _lblInfo.Text = Tr("diff.info", Meta(_item.Remote), Meta(_item.Local), _item.Status);
        _rtb.Text = Tr("diff.loading");

        string? tmp = null;
        try
        {
            byte[] remote = Array.Empty<byte>(), local = Array.Empty<byte>();
            if (_item.Remote != null)
            {
                if (_item.Remote.Size > MaxTextBytes) { Info(Tr("diff.err.master_big")); return; }
                tmp = Path.Combine(Path.GetTempPath(), "netsynq-view-" + Guid.NewGuid().ToString("N") + ".tmp");
                using var rc = _rcFactory();
                await rc.DownloadAsync(_item.Path, tmp, _item.Remote.Size, null, _cts.Token);
                remote = await File.ReadAllBytesAsync(tmp, _cts.Token);
            }
            if (_localFull != null && File.Exists(_localFull))
            {
                if (new FileInfo(_localFull).Length > MaxTextBytes) { Info(Tr("diff.err.local_big")); return; }
                local = await File.ReadAllBytesAsync(_localFull, _cts.Token);
            }

            if (LooksBinary(remote) || LooksBinary(local))
            {
                bool same = remote.AsSpan().SequenceEqual(local);
                Info(Tr(_item.Remote == null || _localFull == null || !File.Exists(_localFull)
                    ? "diff.bin.one_side"
                    : same ? "diff.bin.same"
                           : "diff.bin.different"));
                return;
            }

            var a = Lines(Decode(local));    // slave
            var b = Lines(Decode(remote));   // master
            _diff = LineDiff.Compute(a, b);
            if (_diff == null) { Info(Tr("diff.err.too_different")); return; }

            bool crlfL = Encoding.UTF8.GetString(local).Contains("\r\n");
            bool crlfR = Encoding.UTF8.GetString(remote).Contains("\r\n");
            if (crlfL != crlfR) _lblInfo.Text += Tr("diff.crlf");
            if (!_diff.Any(d => d.Op != DiffOp.Equal)) _lblInfo.Text += Tr("diff.identical");
            Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Info(Tr("diff.err.compare", e.Message)); }
        finally { if (tmp != null) try { File.Delete(tmp); } catch { } }
    }

    private void Info(string text) { _chkAll.Visible = false; _rtb.Font = new Font("Segoe UI", 10f); _rtb.Text = text; }

    private void Render()
    {
        if (_diff == null) return;
        const int ctx = 3;
        var d = _diff;
        var show = new bool[d.Count];
        if (_chkAll.Checked) Array.Fill(show, true);
        else
            for (int i = 0; i < d.Count; i++)
                if (d[i].Op != DiffOp.Equal)
                    for (int j = Math.Max(0, i - ctx); j <= Math.Min(d.Count - 1, i + ctx); j++) show[j] = true;

        _rtb.SuspendLayout();
        _rtb.Clear();
        AppendLine(Tr("diff.legend"), Theme.Muted);
        int rendered = 0; bool gap = false;
        for (int i = 0; i < d.Count; i++)
        {
            if (!show[i]) { gap = true; continue; }
            if (gap) { AppendLine("   ⋯", Theme.Muted); gap = false; }
            if (++rendered > MaxRenderedLines) { AppendLine(Tr("diff.truncated"), Theme.Muted); break; }
            switch (d[i].Op)
            {
                case DiffOp.Equal: AppendLine("    " + d[i].Line, Theme.Text); break;
                case DiffOp.Delete: AppendLine("−   " + d[i].Line, Theme.DiffDelFore, Theme.DiffDelBack); break;
                default: AppendLine("+   " + d[i].Line, Theme.DiffAddFore, Theme.DiffAddBack); break;
            }
        }
        _rtb.SelectionStart = 0;
        _rtb.ResumeLayout();
    }

    private void AppendLine(string text, Color fore, Color? back = null)
    {
        _rtb.SelectionStart = _rtb.TextLength;
        _rtb.SelectionLength = 0;
        _rtb.SelectionColor = fore;
        _rtb.SelectionBackColor = back ?? Theme.Surface;
        _rtb.AppendText(text + "\n");
    }
}
