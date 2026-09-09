using Gtk;

namespace Boxy.Ui;

/// Card for a search-result package (official repo or AUR).
public static class PackageCard
{
    public static Gtk.FlowBoxChild Create(Boxy.BoxyApp app, Core.PackageInfo pkg)
    {
        string? iconFile = app.State.Icons.TryGetValue(pkg.Name, out var ic) ? ic : null;

        var img = Ui.AppImage(iconFile, "application-x-executable", 48);
        var name = Ui.Label(pkg.Name);
        Ui.AddClass(name, "card-name");
        name.Ellipsize = Pango.EllipsizeMode.End;
        name.MaxWidthChars = 16;

        var sub = Ui.Label(string.IsNullOrEmpty(pkg.Description) ? (pkg.FromAur ? "AUR package" : "Package") : pkg.Description);
        Ui.AddClass(sub, "card-sub");
        sub.Ellipsize = Pango.EllipsizeMode.End;
        sub.MaxWidthChars = 18;

        var badge = pkg.FromAur
            ? Ui.Badge("AUR", "aur")
            : Ui.Badge(pkg.Repo, "repo");
        var bottom = Ui.HBox(6);
        bottom.Append(badge);
        if (pkg.Installed)
            bottom.Append(Ui.Badge("installed", "ok"));
        else
            bottom.Append(Ui.Badge(pkg.Version.Length > 12 ? pkg.Version[..12] + "…" : pkg.Version, "muted"));

        var box = Ui.VBox(6);
        box.MarginTop = box.MarginBottom = box.MarginStart = box.MarginEnd = 10;
        img.Halign = Gtk.Align.Start;
        box.Append(img);
        box.Append(name);
        box.Append(sub);
        box.Append(bottom);

        var btn = new Gtk.Button { Child = box, WidthRequest = 190 };
        btn.Halign = Gtk.Align.Fill;
        Ui.AddClass(btn, "boxy-card");
        btn.OnClicked += (_, _) => app.OpenPackage(pkg);

        var child = new Gtk.FlowBoxChild { Child = btn };
        return child;
    }
}

/// Card for an app that is already integrated on the host desktop.
public static class HostAppCard
{
    public static Gtk.FlowBoxChild Create(Boxy.BoxyApp app, Core.HostApp host)
    {
        string? iconFile = null;
        if (host.PackageName != null && app.State.Icons.TryGetValue(host.PackageName, out var ic)) iconFile = ic;

        var img = Ui.AppImage(iconFile, "application-x-executable", 48, themeIcon: host.IconName);
        var name = Ui.Label(host.Name);
        Ui.AddClass(name, "card-name");
        name.Ellipsize = Pango.EllipsizeMode.End;
        name.MaxWidthChars = 16;

        var sub = Ui.Label(host.PackageName ?? "exported to your desktop");
        Ui.AddClass(sub, "card-sub");
        sub.Ellipsize = Pango.EllipsizeMode.End;
        sub.MaxWidthChars = 20;

        var bottom = Ui.HBox(6);
        bottom.Append(Ui.Badge("on desktop", "ok"));

        var box = Ui.VBox(6);
        box.MarginTop = box.MarginBottom = box.MarginStart = box.MarginEnd = 10;
        img.Halign = Gtk.Align.Start;
        box.Append(img);
        box.Append(name);
        box.Append(sub);
        box.Append(bottom);

        var btn = new Gtk.Button { Child = box, WidthRequest = 190 };
        Ui.AddClass(btn, "boxy-card");
        btn.OnClicked += (_, _) =>
        {
            if (host.PackageName != null)
                app.OpenInstalledPackage(host.PackageName);
            else
                Core.Integrator.LaunchDesktopFile(host.DesktopFile);
        };

        var child = new Gtk.FlowBoxChild { Child = btn };
        return child;
    }
}

public static class Grids
{
    public static Gtk.FlowBox Make()
    {
        var g = new Gtk.FlowBox
        {
            Homogeneous = true,
            MinChildrenPerLine = 1,
            MaxChildrenPerLine = 5,
            ColumnSpacing = 16,
            RowSpacing = 16,
            SelectionMode = Gtk.SelectionMode.None,
        };
        g.Halign = Gtk.Align.Start;
        return g;
    }

    public static void Fill(Gtk.FlowBox grid, IEnumerable<Gtk.FlowBoxChild> children)
    {
        grid.RemoveAll();
        foreach (var c in children) grid.Append(c);
    }
}
