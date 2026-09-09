using Boxy.Core;
using Gtk;

namespace Boxy.Ui;

/// Main page: container status, updates, integrated apps and (when searching) results.
public sealed class HomePage
{
    private readonly Boxy.BoxyApp _app;
    private readonly ScrolledWindow _scroll = new();
    private readonly Box _root = Ui.VBox(0);

    // container status panel
    private readonly Gtk.Image _ctIcon = new() { IconName = "system-run-symbolic", PixelSize = 32 };
    private readonly Gtk.Label _ctTitle = Ui.Label("Checking container…");
    private readonly Gtk.Label _ctSub = Ui.Label("", wrap: true);
    private readonly Gtk.Button _ctBtn = new() { Label = "Set up" };
    private readonly Gtk.Label _ctBtnHint = Ui.Label("");

    // updates panel
    private readonly Gtk.Image _upIcon = new() { IconName = "software-update-available-symbolic", PixelSize = 22 };
    private readonly Gtk.Label _upTitle = Ui.Label("Checking for updates…");
    private readonly Gtk.Label _upSub = Ui.Label("", wrap: true);
    private readonly Gtk.Button _upBtn = new() { Label = "Update all" };

    // sections container (hidden while searching)
    private readonly Box _defaultBox = Ui.VBox(16);
    private readonly Box _searchBox = Ui.VBox(12);
    private readonly Gtk.Label _searchHeading = Ui.Label("", wrap: true);
    private readonly Gtk.Label _searchStatus = Ui.Label("");
    private readonly FlowBox _searchGrid = Grids.Make();
    private readonly FlowBox _appsGrid = Grids.Make();
    private readonly Gtk.Label _appsStatus = Ui.Label("");
    private readonly Spinner _searchSpinner = new() { Spinning = true };

    private bool _setupClickable = true;

    public Gtk.Widget Widget => _scroll;

    public HomePage(Boxy.BoxyApp app)
    {
        _app = app;
        BuildUi();

        _ctBtn.OnClicked += async (_, _) => await _app.SetupContainerAsync();
        _upBtn.OnClicked += async (_, _) => await _app.UpdateAllAsync();
        _searchHeading.Halign = Gtk.Align.Start;
        _searchSpinner.Visible = false;
    }

    private void BuildUi()
    {
        _scroll.HscrollbarPolicy = Gtk.PolicyType.Never;
        _scroll.HasFrame = false;

        var clamp = new Adw.Clamp { MaximumSize = 1080, TighteningThreshold = 900, Child = _root };
        _scroll.Child = clamp;
        _root.MarginTop = 8;

        // ---------- default sections ----------
        var ctPanel = BuildContainerPanel();
        var upPanel = BuildUpdatePanel();
        var appsSection = BuildAppsSection();
        var reposNote = Ui.Label(
            "Packages install inside the Arch container \"" + _app.ContainerName +
            "\" (core · extra · multilib · AUR) and are exported to your desktop automatically.");
        Ui.AddClass(reposNote, "boxy-muted");
        reposNote.Wrap = true;

        _defaultBox.Append(ctPanel);
        _defaultBox.Append(upPanel);
        _defaultBox.Append(appsSection);
        _defaultBox.Append(reposNote);

        // ---------- search section ----------
        var searchHead = Ui.HBox(10);
        searchHead.Append(_searchSpinner);
        searchHead.Append(_searchHeading);
        _searchGrid.Halign = Gtk.Align.Fill;
        _searchBox.Append(searchHead);
        _searchBox.Append(_searchStatus);
        _searchBox.Append(_searchGrid);

        _root.Append(_defaultBox);
        _root.Append(_searchBox);
        _searchBox.Visible = false;
    }

    private Gtk.Widget BuildContainerPanel()
    {
        var box = Ui.HBox(16);
        Ui.AddClass(box, "boxy-panel");
        box.MarginStart = box.MarginEnd = 20;
        box.MarginTop = box.MarginBottom = 6;

        _ctIcon.PixelSize = 36;
        _ctIcon.Valign = Gtk.Align.Center;

        var labels = Ui.VBox(4);
        _ctTitle.Halign = Gtk.Align.Start;
        _ctTitle.Wrap = true;
        _ctSub.Halign = Gtk.Align.Start;
        _ctSub.Wrap = true;
        labels.Append(_ctTitle);
        labels.Append(_ctSub);
        labels.Hexpand = true;
        labels.Valign = Gtk.Align.Center;

        var acts = Ui.VBox(6);
        Ui.AddClass(_ctBtn, "suggested-action");
        _ctBtn.Visible = false;
        _ctBtn.Valign = Gtk.Align.Center;
        acts.Append(_ctBtn);
        acts.Append(_ctBtnHint);
        acts.Valign = Gtk.Align.Center;

        box.Append(_ctIcon);
        box.Append(labels);
        box.Append(acts);
        return box;
    }

    private Gtk.Widget BuildUpdatePanel()
    {
        var box = Ui.HBox(16);
        Ui.AddClass(box, "boxy-panel");
        box.MarginStart = box.MarginEnd = 20;
        box.MarginTop = box.MarginBottom = 6;

        _upIcon.PixelSize = 26;
        _upIcon.Valign = Gtk.Align.Center;

        var labels = Ui.VBox(4);
        _upTitle.Halign = Gtk.Align.Start;
        _upTitle.Wrap = true;
        _upSub.Halign = Gtk.Align.Start;
        _upSub.Wrap = true;
        labels.Append(_upTitle);
        labels.Append(_upSub);
        labels.Hexpand = true;
        labels.Valign = Gtk.Align.Center;

        _upBtn.Visible = false;
        _upBtn.Valign = Gtk.Align.Center;
        box.Append(_upIcon);
        box.Append(labels);
        box.Append(_upBtn);
        return box;
    }

    private Gtk.Widget BuildAppsSection()
    {
        var box = Ui.VBox(10);
        var head = Ui.HBox(10);
        var title = Ui.Label("Installed & integrated");
        Ui.AddClass(title, "boxy-section");
        var note = Ui.Label("— apps exported from the container");
        Ui.AddClass(note, "boxy-muted");
        head.Append(title);
        head.Append(note);
        box.Append(head);
        _appsStatus.Halign = Gtk.Align.Start;
        box.Append(_appsStatus);
        box.Append(_appsGrid);
        return box;
    }

    // ---------- public state ----------

    public void ShowDefault()
    {
        _searchBox.Visible = false;
        _defaultBox.Visible = true;
    }

    public void ShowSearching(string query)
    {
        _defaultBox.Visible = false;
        _searchBox.Visible = true;
        _searchHeading.Label_ = $"Results for “{query}”";
        _searchStatus.Visible = false;
        _searchSpinner.Visible = true;
        _searchSpinner.Spinning = true;
        _searchGrid.RemoveAll();
    }

    public void RenderSearchResults(IReadOnlyList<PackageInfo> results, string query, string? error)
    {
        _searchSpinner.Visible = false;
        _searchSpinner.Spinning = false;
        _searchHeading.Label_ = $"Results for “{query}”";
        if (error != null)
        {
            _searchStatus.Visible = true;
            _searchStatus.Label_ = error;
            _searchGrid.RemoveAll();
            return;
        }
        _searchStatus.Visible = results.Count == 0;
        _searchStatus.Label_ = "No packages found. Try a different name (AUR included).";
        if (results.Count == 0)
        {
            _searchGrid.RemoveAll();
            return;
        }
        _searchStatus.Visible = false;
        _searchHeading.Label_ = $"{results.Count} result(s) for “{query}”";
        Grids.Fill(_searchGrid, results.Select(p => PackageCard.Create(_app, p)));
    }

    public async Task RenderContainerStatusAsync()
    {
        var (exists, ready) = await _app.ContainerStatusAsync();
        if (exists && ready)
        {
            _ctIcon.IconName = "emblem-ok-symbolic";
            _ctTitle.Label_ = "Arch container \"" + _app.ContainerName + "\" is ready";
            _ctSub.Label_ = "core, extra, multilib and AUR (yay) available — apps are exported to your desktop.";
            _ctBtn.Visible = false;
            _ctBtnHint.Label_ = "";
        }
        else if (exists)
        {
            _ctIcon.IconName = "emblem-warning-symbolic";
            _ctTitle.Label_ = "Container \"" + _app.ContainerName + "\" exists but is not ready";
            _ctSub.Label_ = "Missing yay (AUR helper) or pacman. Re-run the setup to repair.";
            _ctBtn.Label = "Repair";
            _ctBtn.Visible = true;
            _ctBtnHint.Label_ = "";
        }
        else
        {
            _ctIcon.IconName = "edit-clear-symbolic";
            _ctTitle.Label_ = "No Arch container yet";
            _ctSub.Label_ = "Boxy needs an Arch distrobox (\"" + _app.ContainerName +
                "\") with multilib and the AUR helper to install apps from.";
            _ctBtn.Label = "Create container";
            _ctBtn.Visible = true;
            _ctBtnHint.Label_ = "Downloads ~600 MB on first run.";
            _ctBtn.Sensitive = _setupClickable;
        }
    }

    public void SetContainerBusy(string text)
    {
        _ctBtn.Sensitive = false;
        _ctBtn.Visible = true;
        _ctBtn.Label = "Working…";
        _ctTitle.Label_ = text;
        _ctSub.Label_ = "This can take several minutes on the first run.";
    }

    public void SetContainerReady()
    {
        _setupClickable = true;
        _ctBtn.Sensitive = true;
    }

    /// Live progress line while setting up/repairing the container.
    public void SetContainerProgress(string line)
    {
        _ctSub.Label_ = line.Length > 120 ? line[..117] + "…" : line;
    }

    /// Live progress line during "Update all".
    public void SetUpdateProgress(string line)
    {
        _upSub.Label_ = line.Length > 140 ? line[..137] + "…" : line;
    }

    public async Task RenderUpdatesAsync()
    {
        var updates = await _app.GetUpdatesAsync();
        if (updates == null)
        {
            _upTitle.Label_ = "Updates";
            _upSub.Label_ = "Container is not ready.";
            _upBtn.Visible = false;
            return;
        }
        var n = updates.Count;
        if (n == 0)
        {
            _upTitle.Label_ = "All packages are up to date";
            _upSub.Label_ = "Official repositories and AUR are in sync with the container.";
            _upIcon.IconName = "emblem-ok-symbolic";
            _upBtn.Visible = false;
        }
        else
        {
            _upTitle.Label_ = n == 1 ? "1 update available" : n + " updates available";
            _upSub.Label_ = "Updates for: " + string.Join(", ", updates.Take(6)) + (n > 6 ? $" (+{n - 6} more)" : "");
            _upIcon.IconName = "software-update-urgent-symbolic";
            _upBtn.Visible = true;
            _upBtn.Sensitive = !_app.Busy;
        }
    }

    public void RenderHostApps()
    {
        var apps = _app.ListIntegratedApps();
        if (apps.Count == 0)
        {
            _appsStatus.Visible = true;
            _appsStatus.Label_ = "Nothing exported yet — install an app and Boxy puts it on your desktop.";
            Grids.Fill(_appsGrid, Array.Empty<Gtk.FlowBoxChild>());
            return;
        }
        _appsStatus.Visible = false;
        Grids.Fill(_appsGrid, apps.Select(a => HostAppCard.Create(_app, a)));
    }
}
