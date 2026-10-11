using System.Runtime.InteropServices;

namespace NetSynq.UI;

/// <summary>Piccoli ritocchi nativi per il tema scuro (barra del titolo e barre di scorrimento); se Windows non li supporta non succede nulla.</summary>
internal static class NativeTheme
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    /// <summary>Barra del titolo scura (Windows 10 1809+ / 11).</summary>
    public static void TitleBar(Form f, bool dark)
    {
        if (!f.IsHandleCreated) return;
        try
        {
            int v = dark ? 1 : 0;
            if (DwmSetWindowAttribute(f.Handle, 20, ref v, sizeof(int)) != 0) DwmSetWindowAttribute(f.Handle, 19, ref v, sizeof(int));
        }
        catch { /* sistema senza DWM o librerie diverse */ }
    }

    /// <summary>Barre di scorrimento scure per griglie, caselle di testo multilinea e pannelli con scorrimento.</summary>
    public static void Scrollbars(Control c, bool dark)
    {
        if (!c.IsHandleCreated) return;
        try { SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : null, null); }
        catch { }
    }
}
