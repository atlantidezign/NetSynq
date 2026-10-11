using NetSynq.Core;
using static NetSynq.Core.I18n;

namespace NetSynq.UI;

/// <summary>
/// Collega i testi dell'interfaccia alle chiavi del dizionario: ogni testo registrato viene riscritto
/// quando cambia la lingua (<see cref="Refresh"/>). Vale solo per la finestra principale (vive quanto l'applicazione);
/// le finestre di dialogo leggono i testi alla creazione con <see cref="I18n.Tr(string)"/>.
/// </summary>
internal static class Loc
{
    private static readonly List<Action> Binds = new();

    /// <summary>Registra un'azione che applica i testi; viene eseguita subito e a ogni cambio di lingua.</summary>
    public static void Add(Action apply) { Binds.Add(apply); apply(); }

    public static T Text<T>(T c, string key, params object?[] args) where T : Control
    {
        Add(() => c.Text = Tr(key, args));
        return c;
    }

    public static TabPage Page(string key) => Text(new TabPage(), key);

    public static void Tip(ToolTip tip, Control c, string key) => Add(() => tip.SetToolTip(c, Tr(key)));

    /// <summary>Riempie un elenco a discesa con i testi delle chiavi, mantenendo la voce selezionata al cambio lingua.</summary>
    public static void Combo(ComboBox cb, params string[] keys) => Add(() =>
    {
        int i = cb.SelectedIndex;
        cb.BeginUpdate();
        cb.Items.Clear();
        foreach (var k in keys) cb.Items.Add(Tr(k));
        if (i >= 0 && i < cb.Items.Count) cb.SelectedIndex = i;
        cb.EndUpdate();
    });

    public static void Header(DataGridViewColumn col, string key) => Add(() => col.HeaderText = Tr(key));

    public static void Refresh()
    {
        foreach (var a in Binds.ToArray()) { try { a(); } catch { /* un controllo già eliminato */ } }
    }
}
