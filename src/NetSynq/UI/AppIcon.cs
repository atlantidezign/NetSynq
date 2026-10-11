using System.Drawing;

namespace NetSynq.UI;

/// <summary>Applica alle finestre l'icona dell'applicazione (quella incorporata nell'exe).</summary>
internal static class AppIcon
{
    private static Icon? _icon;
    private static bool _loaded;

    public static void Apply(Form form)
    {
        try
        {
            if (!_loaded)
            {
                _loaded = true;
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) _icon = Icon.ExtractAssociatedIcon(exe);
            }
            if (_icon != null) form.Icon = _icon;
        }
        catch { /* icona non disponibile: resta quella predefinita */ }
    }
}
