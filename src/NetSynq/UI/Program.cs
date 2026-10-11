namespace NetSynq.UI;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var cfg = NetSynq.Core.AppSettings.Load();
        NetSynq.Core.I18n.SetLanguage(cfg.Language); // lingua e tema vanno impostati prima di creare la finestra
        Theme.SetDark(cfg.Dark);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.ToString(), "NetSynq – " + NetSynq.Core.I18n.Tr("app.unexpected"));
        Application.Run(new MainForm());
    }
}
