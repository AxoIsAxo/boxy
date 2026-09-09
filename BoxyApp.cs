using Boxy.Core;
using Boxy.Ui;
using Gtk;

namespace Boxy;

/// Application controller: window/navigation shell, search orchestration,
/// single-flight package operations and home/detail coordination.
public sealed class BoxyApp
{
    private readonly Adw.Application _app;
    private Gtk.Window _win = null!;
    private Adw.NavigationView _nav = null!;
    private HomePage _home = null!;

    private readonly Adw.HeaderBar _header = new();
    private readonly Gtk.Button _backBtn = new() { IconName = "go-previous-symbolic" };
    private readonly Box _centerBox = Ui.Ui.HBox(6);
    private readonly Gtk.SearchEntry _search = new() { PlaceholderText = "Search core, extra, multilib and AUR…" };
    private readonly Gtk.Label _centerTitle = Ui.Ui.Label("");

    private CancellationTokenSource? _searchCts;
    private int _stackDepth;      // number of detail pages pushed above the root
    private SynchronizationContext? _ui;

    public Adw.Application App => _app;
    public Gtk.Window Window => _win;

    public AppState State { get; }
    public ContainerManager Ctn { get; }
    public PackageService Svc { get; }
    public Integrator Int { get; }
    public string ContainerName => Ctn.Name;

    public bool Busy { get; private set; }
    public List<string> PendingUpdates { get; } = new();
    public bool PendingUpdatesLoaded { get; private set; }

    public BoxyApp(Adw.Application app, AppState state)
    {
        _app = app;
        State = state;
        Ctn = new ContainerManager(state.Container);
        Svc = new PackageService(Ctn, state);
        Int = new Integrator(Ctn, state);
        _ui = SynchronizationContext.Current;
    }

    public void Activate()
    {
        if (_win != null) { _win.Present(); return; }

        _win = new Gtk.Window
        {
            DefaultWidth = 1180,
            DefaultHeight = 820,
            Title = "Boxy",
        };
        _app.AddWindow(_win); // holds the main loop; quits when last window closes
        Ui.Ui.InstallCss(_win.GetDisplay());

        // header
        Ui.Ui.AddClass(_backBtn, "flat");
        _backBtn.TooltipText = "Back";
        _backBtn.Visible = false;
        _backBtn.OnClicked += (_, _) => NavigateBack();
        _header.PackStart(_backBtn);

        _search.WidthRequest = 420;
        _search.Halign = Gtk.Align.Center;
        _centerBox.Halign = Gtk.Align.Center;
        _centerBox.Append(_search);
        _centerTitle.Visible = false;
        Ui.Ui.AddClass(_centerTitle, "boxy-section");
        _centerBox.Append(_centerTitle);
        _centerBox.Hexpand = true;
        _header.TitleWidget = _centerBox;

        _win.Titlebar = _header;

        // content
        _nav = new Adw.NavigationView();
        _win.Child = _nav;

        _home = new HomePage(this);
        var rootPage = new Adw.NavigationPage { Title = "Boxy", Child = _home.Widget };
        _nav.Add(rootPage);

        _search.OnChanged += (_, _) => _ = OnSearchChangedAsync();
        _nav.OnPopped += (_, _) => { _stackDepth = Math.Max(0, _stackDepth - 1); UpdateChrome(); };

        _win.Present();

        _ = RefreshHomeAsync();
    }

    // ---------- navigation ----------

    private void UpdateChrome()
    {
        bool root = _stackDepth == 0;
        _backBtn.Visible = !root;
        _search.Visible = root;
        _centerTitle.Visible = !root;
        if (root) _search.Text_ = ""; // clear stale query -> default view
    }

    public void NavigateBack()
    {
        if (_stackDepth <= 0) return;
        try { _nav.Pop(); } catch { /* already gone */ }
        _stackDepth--;
        UpdateChrome();
    }

    private void PushDetail(string title, Gtk.Widget page)
    {
        var np = new Adw.NavigationPage { Title = title, Child = page };
        _nav.Push(np);
        _stackDepth++;
        _centerTitle.Label_ = title;
        UpdateChrome();
    }

    public void OpenPackage(PackageInfo pkg)
    {
        if (Busy) return;
        PushDetail(pkg.Name, new DetailPage(this, pkg).Page);
    }

    public void OpenInstalledPackage(string name)
    {
        if (Busy) return;
        var page = new DetailPage(this, new PackageInfo { Name = name, Repo = "", Installed = true });
        PushDetail(name, page.Page);
    }

    // ---------- search ----------

    private async Task OnSearchChangedAsync()
    {
        var q = (_search.Text_ ?? "").Trim();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
        if (q.Length == 0)
        {
            _home.ShowDefault();
            return;
        }
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        try
        {
            await Task.Delay(450, cts.Token);
            _home.ShowSearching(q);
            List<PackageInfo> results;
            try
            {
                results = await Svc.SearchAsync(q, cts.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _home.RenderSearchResults(Array.Empty<PackageInfo>(), q, "Search failed: " + ex.Message);
                return;
            }
            if (!cts.IsCancellationRequested)
                _home.RenderSearchResults(results, q, null);
        }
        catch (OperationCanceledException) { /* superseded by newer keystroke */ }
        finally
        {
            if (ReferenceEquals(_searchCts, cts)) _searchCts = null;
        }
    }

    // ---------- home data ----------

    public async Task<(bool Exists, bool Ready)> ContainerStatusAsync()
    {
        try { return await Ctn.StatusAsync(); }
        catch { return (false, false); }
    }

    public async Task<List<string>?> GetUpdatesAsync()
    {
        var (_, ready) = await ContainerStatusAsync();
        if (!ready) return null;
        try
        {
            var list = await Svc.UpdatesAsync(true, CancellationToken.None);
            PendingUpdates.Clear();
            PendingUpdates.AddRange(list);
            PendingUpdatesLoaded = true;
            return list;
        }
        catch { return null; }
    }

    public List<HostApp> ListIntegratedApps()
    {
        try
        {
            return Int.ListHostApps()
                .Where(a => File.Exists(a.DesktopFile))
                .ToList();
        }
        catch { return new List<HostApp>(); }
    }

    public async Task<PackageInfo> FetchInfoAsync(PackageInfo pkg)
        => await Svc.InfoAsync(pkg, CancellationToken.None);

    public async Task RefreshHomeAsync()
    {
        try
        {
            await _home.RenderContainerStatusAsync();
            await _home.RenderUpdatesAsync();
        }
        catch { /* container not reachable — status panel explains */ }
        _home.RenderHostApps();
    }

    // ---------- operations ----------

    /// Runs one package/container operation single-flight, streaming log lines to
    /// <paramref name="logUi"/> (marshaled to the UI thread), then <paramref name="success"/>.
    public async Task<bool> RunOpAsync(
        Func<Action<string>, Task> op,
        Action<string> logUi,
        Action? success = null)
    {
        if (Busy) return false;
        Busy = true;
        _search.Sensitive = false;
        try
        {
            await op(line =>
            {
                if (_ui is { } u) u.Post(_ => logUi(line), null);
                else logUi(line);
            });
            success?.Invoke();
            return true;
        }
        catch (OperationCanceledException)
        {
            _ui?.Post(_ => logUi("Cancelled."), null);
            return false;
        }
        catch (Exception ex)
        {
            _ui?.Post(_ => logUi("Error: " + ex.Message), null);
            return false;
        }
        finally
        {
            Busy = false;
            _search.Sensitive = _stackDepth == 0;
        }
    }

    public async Task SetupContainerAsync()
    {
        _home.SetContainerBusy("Setting up Arch container \"" + ContainerName + "\"…");
        await RunOpAsync(
            log => Ctn.CreateAndBootstrapAsync(line => _home.SetContainerProgress(line), _ui, CancellationToken.None),
            _ => { },
            success: () => { });
        await RefreshHomeAsync();
    }

    public async Task UpdateAllAsync()
    {
        await RunOpAsync(
            log => Svc.UpdateAllAsync(true, line => _home.SetUpdateProgress(line), CancellationToken.None),
            _ => { },
            success: () => { });
        await RefreshHomeAsync();
    }

    public void StateExportsChanged()
    {
        _home.RenderHostApps();
    }

    // ---------- launch ----------

    public void Launch(string pkgName)
    {
        if (State.Exports.TryGetValue(pkgName, out var desktops) && desktops.Count > 0 && File.Exists(desktops[0]))
        {
            Integrator.LaunchDesktopFile(desktops[0]);
            return;
        }
        if (State.LaunchBin.TryGetValue(pkgName, out var bin))
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "distrobox",
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("enter");
                psi.ArgumentList.Add(ContainerName);
                psi.ArgumentList.Add("--");
                psi.ArgumentList.Add(bin);
                System.Diagnostics.Process.Start(psi);
            }
            catch { /* launch is best-effort */ }
        }
    }
}
