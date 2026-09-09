using Boxy.Core;
using Gtk;

namespace Boxy.Ui;

/// Package detail page: metadata, AUR warning, install/remove/launch.
public sealed class DetailPage
{
    private readonly Boxy.BoxyApp _app;
    private PackageInfo _pkg;
    private string? _iconFile;

    private readonly Box _root = Ui.VBox(14);
    private readonly Gtk.Image _icon = new() { PixelSize = 96 };
    private readonly Gtk.Label _name = Ui.Label("");
    private readonly Gtk.Label _versionLine = Ui.Label("");
    private readonly Gtk.Label _desc = Ui.Label("", wrap: true);
    private readonly Gtk.Box _aurWarn = BuildAurWarning();
    private readonly Box _rows = Ui.VBox(0);

    private readonly Gtk.Button _installBtn = new() { Label = "Install" };
    private readonly Gtk.Button _removeBtn = new() { Label = "Remove" };
    private readonly Gtk.Button _launchBtn = new() { Label = "Launch" };
    private readonly Gtk.Button _updateBtn = new() { Label = "Update" };
    private readonly Gtk.Label _installedLine = Ui.Label("");

    private readonly Spinner _busySpinner = new() { Spinning = false };
    private readonly Gtk.Label _busyLabel = Ui.Label("");
    private readonly Gtk.Label _log = Ui.Label("", selectable: true);
    private readonly ScrolledWindow _logScroll = new();
    private bool _busy;

    public Adw.NavigationPage Page { get; }

    public DetailPage(Boxy.BoxyApp app, PackageInfo pkg)
    {
        _app = app;
        _pkg = pkg;
        BuildUi();
        Page = new Adw.NavigationPage { Title = pkg.Name, Child = _scrollWrap() };

        _installBtn.OnClicked += async (_, _) => await RunInstallAsync();
        _removeBtn.OnClicked += async (_, _) => await RunRemoveAsync();
        _updateBtn.OnClicked += async (_, _) => await RunUpdateAsync();
        _launchBtn.OnClicked += (_, _) => _app.Launch(_pkg.Name);

        _ = LoadDetailsAsync();
    }

    private Widget _scrollWrap()
    {
        var scroll = new ScrolledWindow { HasFrame = false, Child = new Adw.Clamp { MaximumSize = 900, TighteningThreshold = 720, Child = _root } };
        scroll.HscrollbarPolicy = Gtk.PolicyType.Never;
        return scroll;
    }

    private void BuildUi()
    {
        _root.MarginTop = 4;

        // header
        var head = Ui.HBox(18);
        Ui.AddClass(head, "boxy-panel");
        head.MarginTop = head.MarginBottom = head.MarginStart = head.MarginEnd = 16;
        _icon.MarginStart = 8;
        var meta = Ui.VBox(6);
        _name.Halign = Gtk.Align.Start;
        Ui.AddClass(_name, "detail-name");
        _name.Wrap = true;
        _versionLine.Halign = Gtk.Align.Start;
        _installedLine.Halign = Gtk.Align.Start;
        meta.Append(_name);
        meta.Append(_versionLine);
        meta.Append(_installedLine);
        meta.Hexpand = true;
        head.Append(_icon);
        head.Append(meta);
        _root.Append(head);

        // AUR warning
        _aurWarn.Visible = false;
        _root.Append(_aurWarn);

        // actions
        var actions = Ui.HBox(10);
        Ui.AddClass(_installBtn, "suggested-action");
        actions.Append(_installBtn);
        actions.Append(_removeBtn);
        actions.Append(_updateBtn);
        actions.Append(_launchBtn);
        var busyBox = Ui.HBox(8);
        _busySpinner.Visible = false;
        _busyLabel.Halign = Gtk.Align.Start;
        busyBox.Append(_busySpinner);
        busyBox.Append(_busyLabel);
        _root.Append(actions);
        _root.Append(busyBox);

        // description
        Ui.AddClass(_desc, "prop-value");
        _desc.Halign = Gtk.Align.Start;
        _root.Append(_desc);

        // metadata rows
        _root.Append(_rows);

        // operation log (revealed while busy)
        _logScroll.HscrollbarPolicy = Gtk.PolicyType.Never;
        _logScroll.HasFrame = false;
        _logScroll.MaxContentHeight = 170;
        _logScroll.Child = new Adw.Clamp { MaximumSize = 900, TighteningThreshold = 720, Child = _log };
        _log.Wrap = true;
        Ui.AddClass(_log, "msg");
        _log.Halign = Gtk.Align.Start;
        _log.Visible = false;
        _root.Append(_logScroll);
        _logScroll.Visible = false;
    }

    private static Gtk.Box BuildAurWarning()
    {
        var box = Ui.HBox(10);
        Ui.AddClass(box, "boxy-aur-warn");
        box.MarginTop = box.MarginBottom = box.MarginStart = box.MarginEnd = 4;
        var icon = new Gtk.Image { IconName = "dialog-warning-symbolic", PixelSize = 22 };
        icon.Valign = Gtk.Align.Start;
        var text = Ui.Label(
            "This package comes from the Arch User Repository (AUR). AUR packages are " +
            "community-supplied PKGBUILDs that run on your machine during build — Boxy does " +
            "not audit them. Review the PKGBUILD and the package's comments before installing.", wrap: true);
        text.Halign = Gtk.Align.Start;
        box.Append(icon);
        box.Append(text);
        return box;
    }

    private void Row(string key, string value)
    {
        var row = Ui.HBox(14);
        row.MarginBottom = 6;
        var k = Ui.Label(key);
        Ui.AddClass(k, "prop-key");
        k.WidthRequest = 170;
        var v = Ui.Label(value, wrap: true, selectable: true);
        Ui.AddClass(v, "prop-value");
        v.Hexpand = true;
        row.Append(k);
        row.Append(v);
        _rows.Append(row);
    }

    private static string FormatBytes(string pacmanHuman)
        => pacmanHuman.Length > 0 ? pacmanHuman : "—";

    private async Task LoadDetailsAsync()
    {
        try
        {
            _pkg = await _app.FetchInfoAsync(_pkg);
        }
        catch { /* keep the search snapshot */ }

        _name.Label_ = _pkg.Name;
        _iconFile = _app.State.Icons.TryGetValue(_pkg.Name, out var ic) ? ic : null;
        _icon.Paintable = null;
        _icon.IconName = null;
        if (_iconFile != null)
        {
            try { _icon.Paintable = Gdk.Texture.NewFromFilename(_iconFile); }
            catch { _icon.IconName = "application-x-executable"; }
        }
        else _icon.IconName = "application-x-executable";

        _versionLine.Label_ = _pkg.FromAur
            ? "AUR · " + _pkg.Version + (_pkg.AurVotes > 0 ? $" · ▲ {_pkg.AurVotes} votes" : "")
            : _pkg.Repo + " repository · version " + _pkg.Version;
        _desc.Label_ = string.IsNullOrEmpty(_pkg.Description) ? "No description provided." : _pkg.Description;
        _aurWarn.Visible = _pkg.FromAur;
        _installedLine.Label_ = _pkg.Installed
            ? "Installed: " + (string.IsNullOrEmpty(_pkg.InstalledVersion) ? _pkg.Version : _pkg.InstalledVersion)
            : "";

        // metadata
        foreach (var c in _rows.GetFirstChild() is { } first ? EnumerateChildren(first).ToArray() : Array.Empty<Widget>())
            _rows.Remove(c);
        if (!_pkg.FromAur)
        {
            Row("Repository", _pkg.Repo);
            if (_pkg.DownloadSize.Length > 0) Row("Download size", FormatBytes(_pkg.DownloadSize));
            if (_pkg.InstalledSize.Length > 0) Row("Installed size", FormatBytes(_pkg.InstalledSize));
        }
        else
        {
            Row("Repository", "AUR (unsandboxed builds)");
            Row("Votes / popularity", _pkg.AurVotes + " ▲ · " + _pkg.AurPopularity.ToString("0.00"));
            if (_pkg.AurMaintainer.Length > 0) Row("Maintainer", _pkg.AurMaintainer);
            if (_pkg.AurOutOfDate) Row("Out of date", "yes — flagged by users");
        }
        if (_pkg.License.Length > 0) Row("License", _pkg.License);
        if (_pkg.Url.Length > 0) Row("Website", _pkg.Url);
        if (!_pkg.FromAur && _pkg.DependsOn.Length > 0) Row("Depends on", _pkg.DependsOn);
        if (_pkg.FromAur) Row("Package base", _pkg.AurPackageBase);

        UpdateActionState();
    }

    private static IEnumerable<Widget> EnumerateChildren(Widget first)
    {
        var w = first;
        while (w != null)
        {
            yield return w;
            w = w.GetNextSibling();
        }
    }

    private void UpdateActionState()
    {
        bool installed = _pkg.Installed;
        var exported = _app.State.Exports.TryGetValue(_pkg.Name, out var d) && d.Count > 0;
        var hasBin = _app.State.BinExports.TryGetValue(_pkg.Name, out var b) && b.Count > 0;
        bool integrated = exported || hasBin;

        _installBtn.Visible = !installed;
        _removeBtn.Visible = installed;
        _updateBtn.Visible = installed;
        _launchBtn.Visible = integrated;
        _launchBtn.Label = _app.State.BinExports.ContainsKey(_pkg.Name) && !exported ? "Launch (container)" : "Launch";

        _installBtn.Sensitive = !_busy;
        _removeBtn.Sensitive = !_busy;
        _updateBtn.Sensitive = !_busy && (_app.PendingUpdates.Contains(_pkg.Name) || !_app.PendingUpdatesLoaded);
        _launchBtn.Sensitive = !_busy;
    }

    private void SetBusy(bool busy, string text)
    {
        _busy = busy;
        _busySpinner.Spinning = busy;
        _busySpinner.Visible = busy;
        _busyLabel.Label_ = busy ? text : "";
        _busyLabel.Visible = busy;
        _log.Visible = busy || _log.Label_?.Length > 0;
        _logScroll.Visible = _log.Visible;
        _log.Label_ = "";
        UpdateActionState();
    }

    private void Log(string line)
    {
        var prev = _log.Label_ ?? "";
        var max = 8000;
        if (prev.Length > max) prev = "…" + prev[^max..];
        _log.Label_ = prev.Length == 0 ? line : prev + "\n" + line;
        var adj = _logScroll.Vadjustment;
        adj.Value = adj.Upper;
    }

    private async Task RunInstallAsync()
    {
        if (_busy) return;
        if (_pkg.FromAur)
        {
            // AUR confirmation
            var dlg = new Adw.MessageDialog
            {
                Heading = "Install from the AUR?",
                Body = _pkg.Name + " is a community-maintained PKGBUILD. Building it runs arbitrary " +
                       "code as your user inside the container. Only continue if you trust the package and its maintainer.",
                TransientFor = _app.Window,
            };
            dlg.AddResponse("cancel", "Cancel");
            dlg.AddResponse("proceed", "Install from AUR");
            dlg.DefaultResponse = "cancel";
            dlg.OnResponse += (s, e) =>
            {
                if (e.Response == "proceed") _ = DoInstallAsync();
            };
            dlg.Present();
        }
        else
        {
            await DoInstallAsync();
        }
    }

    private async Task DoInstallAsync()
    {
        SetBusy(true, "Installing…");
        var ok = await _app.RunOpAsync(
            async log =>
            {
                await _app.Svc.InstallAsync(_pkg, log, CancellationToken.None);
                await _app.Int.ExportPackageAsync(_pkg.Name, log, CancellationToken.None);
            },
            Log,
            success: () => _app.StateExportsChanged());
        SetBusy(false, "");
        if (ok) await LoadDetailsAsync();
    }

    private async Task RunRemoveAsync()
    {
        SetBusy(true, "Removing…");
        var ok = await _app.RunOpAsync(
            async log =>
            {
                await _app.Svc.RemoveAsync(_pkg, log, CancellationToken.None);
                await _app.Int.CleanupExportsAsync(_pkg.Name, log, CancellationToken.None);
            },
            Log,
            success: () => _app.StateExportsChanged());
        SetBusy(false, "");
        if (ok)
        {
            _app.NavigateBack();
            await _app.RefreshHomeAsync();
        }
        else
        {
            await LoadDetailsAsync();
        }
    }

    private async Task RunUpdateAsync()
    {
        if (_busy) return;
        SetBusy(true, "Updating…");
        var ok = await _app.RunOpAsync(
            log => _app.Svc.InstallAsync(_pkg, log, CancellationToken.None),
            Log,
            success: () => _app.StateExportsChanged());
        SetBusy(false, "");
        if (ok) await LoadDetailsAsync();
    }

    public void NotifyIntegrationsChanged()
    {
        UpdateActionState();
    }
}
