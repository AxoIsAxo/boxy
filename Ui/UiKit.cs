using Gtk;

namespace Boxy.Ui;

public static class Ui
{
    public static Gtk.Box VBox(int spacing = 8)
    {
        var b = new Gtk.Box { Spacing = spacing };
        ((Gtk.Orientable)b).Orientation = Gtk.Orientation.Vertical;
        return b;
    }

    public static Gtk.Box HBox(int spacing = 8)
    {
        var b = new Gtk.Box { Spacing = spacing };
        ((Gtk.Orientable)b).Orientation = Gtk.Orientation.Horizontal;
        return b;
    }

    public static void AddClass(Gtk.Widget w, string cls)
    {
        var cur = w.CssClasses ?? Array.Empty<string>();
        if (Array.IndexOf(cur, cls) >= 0) return;
        var next = new string[cur.Length + 1];
        Array.Copy(cur, next, cur.Length);
        next[cur.Length] = cls;
        w.CssClasses = next;
    }

    public static Gtk.Label Label(string text = "", bool wrap = false, bool selectable = false)
    {
        var l = Gtk.Label.New(text);
        if (wrap)
        {
            l.Wrap = true;
            l.WrapMode = Pango.WrapMode.WordChar;
        }
        l.Selectable = selectable;
        l.Halign = Gtk.Align.Start;
        return l;
    }

    /// Image from a local file, or a themed icon name resolved via the icon theme,
    /// falling back to <paramref name="fallbackIcon"/> when neither resolves.
    public static Gtk.Image AppImage(string? file, string fallbackIcon, int px, string? themeIcon = null)
    {
        var img = new Gtk.Image { PixelSize = px };
        // 1. Local cached file (e.g. icon fetched out of the container).
        if (!string.IsNullOrEmpty(file) && System.IO.File.Exists(file))
        {
            try
            {
                var tex = Gdk.Texture.NewFromFilename(file);
                img.Paintable = tex;
                return img;
            }
            catch { /* unsupported format -> try the icon theme */ }
        }
        // 2. Themed icon name (e.g. Icon=steam from an exported desktop entry).
        string? name = null;
        if (!string.IsNullOrEmpty(themeIcon) && IconThemeHas(themeIcon!)) name = themeIcon;
        else if (IconThemeHas(fallbackIcon)) name = fallbackIcon;
        img.IconName = name ?? fallbackIcon;
        return img;
    }

    private static bool IconThemeHas(string name)
    {
        try
        {
            var theme = Gtk.IconTheme.GetForDisplay(Gdk.Display.GetDefault());
            return theme.HasIcon(name);
        }
        catch { return false; }
    }

    /// Label styled as a small pill badge.
    public static Gtk.Label Badge(string text, string variant)
    {
        var l = Label(text);
        AddClass(l, "boxy-badge");
        AddClass(l, "badge-" + variant);
        return l;
    }

    /// Installs the stylesheet on the given display (safe to call once).
    public static void InstallCss(Gdk.Display display)
    {
        var provider = Gtk.CssProvider.New();
        provider.LoadFromString(Css);
        Gtk.StyleContext.AddProviderForDisplay(display, provider, 600);
    }

    public const string Css = """
        .boxy-card {
          background-color: @window_bg_color;
          border: 1px solid alpha(@window_fg_color, .10);
          border-radius: 16px;
          padding: 6px;
        }
        .boxy-card:hover { border-color: alpha(@accent_color, .55); }
        .boxy-card:active { background-color: alpha(@accent_color, .12); }

        .card-name { font-weight: 700; font-size: 14px; }
        .card-sub { color: alpha(@window_fg_color, .55); font-size: 11px; }

        .boxy-title { font-weight: 800; font-size: 22px; }
        .boxy-section { font-weight: 700; font-size: 15px; }

        .boxy-badge { border-radius: 999px; padding: 1px 8px; font-size: 10px; font-weight: 700; }
        .badge-repo { background-color: alpha(@accent_color, .16); color: @accent_color; }
        .badge-aur { background-color: alpha(#f66151, .18); color: #f66151; }
        .badge-ok { background-color: alpha(#2ec27e, .18); color: #2ec27e; }
        .badge-muted { background-color: alpha(@window_fg_color, .10); color: alpha(@window_fg_color, .7); }

        .boxy-muted { color: alpha(@window_fg_color, .55); }
        .boxy-panel {
          background-color: alpha(@window_fg_color, .045);
          border: 1px solid alpha(@window_fg_color, .09);
          border-radius: 16px;
        }
        .boxy-aur-warn {
          background-color: alpha(#f66151, .08);
          border: 1px solid alpha(#f66151, .45);
          border-radius: 12px;
        }
        .msg { font-family: monospace; font-size: 11px; }
        .detail-name { font-weight: 800; font-size: 26px; }
        .prop-key { color: alpha(@window_fg_color, .55); font-size: 12px; }
        .prop-value { font-size: 13px; }
        .row-icon { opacity: .85; }
        """;
}
