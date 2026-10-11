using System.Text;
using NetSynq.Core;
using static NetSynq.Core.I18n;

namespace NetSynq.UI;

/// <summary>
/// Mostra il risultato di una simulazione (nessuna modifica è stata fatta).
/// DialogResult.Yes = "Esegui davvero", altrimenti chiuso senza fare nulla.
/// </summary>
public sealed class SimulationForm : Form
{
    private const int MaxLinesShown = 20000;

    public SimulationForm(SimulationReport report) : this(report.Summary, report.Lines, report.ToText()) { }

    public SimulationForm(string summaryText, IReadOnlyList<string> lines, string fullText)
    {
        Text = Tr("sim.title");
        Width = 1000; Height = 700; StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        AppIcon.Apply(this);
        MinimizeBox = false;

        var summary = new Label
        {
            Text = summaryText.TrimEnd(), AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(10, 10, 10, 8),
            Font = new Font("Segoe UI", 9.5f)
        };

        var sb = new StringBuilder();
        foreach (var l in lines.Take(MaxLinesShown)) sb.AppendLine(l);
        if (lines.Count > MaxLinesShown) sb.AppendLine(Tr("sim.more_lines", lines.Count - MaxLinesShown));
        if (lines.Count == 0) sb.AppendLine(Tr("sim.no_ops"));

        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9.5f), Text = sb.ToString()
        };
        box.Select(0, 0);

        var btnSave = new Button { Text = Tr("sim.btn.save"), AutoSize = true, Height = 30, Margin = new Padding(6) };
        btnSave.Click += (_, _) =>
        {
            using var dlg = new SaveFileDialog { Filter = Tr("sim.filter"), FileName = Tr("sim.filename", DateTime.Now) };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try { File.WriteAllText(dlg.FileName, fullText, new UTF8Encoding(true)); }
            catch (Exception e) { MessageBox.Show(e.Message, "NetSynq", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        var btnRun = new Button { Text = Tr("sim.btn.run"), AutoSize = true, Height = 30, Margin = new Padding(6), DialogResult = DialogResult.Yes };
        var btnClose = new Button { Text = Tr("sim.btn.close"), AutoSize = true, Height = 30, Margin = new Padding(6), DialogResult = DialogResult.Cancel };

        Theme.StyleButton(btnSave, ButtonKind.Secondary);
        Theme.StyleButton(btnRun, ButtonKind.Primary);
        Theme.StyleButton(btnClose, ButtonKind.Secondary);
        BackColor = Theme.Bg;
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        bottom.Controls.Add(btnClose); bottom.Controls.Add(btnRun); bottom.Controls.Add(btnSave);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(summary, 0, 0);
        root.Controls.Add(box, 0, 1);
        root.Controls.Add(bottom, 0, 2);
        Controls.Add(root);
        CancelButton = btnClose;
        Theme.Attach(this);
        Theme.Attach(this);
    }
}
