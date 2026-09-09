using System.Text.RegularExpressions;

namespace Boxy.Core;

/// "Fully integrate into your system": exports installed container apps to the host
/// via distrobox-export (desktop entries + PATH binaries), caches icons, and tracks
/// everything so uninstall cleans up.
public sealed class Integrator
{
    private readonly ContainerManager _ct;
    private readonly AppState _state;
    private static readonly string HostDesktopDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "applications");

    public Integrator(ContainerManager ct, AppState state)
    {
        _ct = ct;
        _state = state;
    }

    // ---------- Discover what a package ships ----------

    private sealed record Shipment(List<(string BaseName, string AbsPath)> Desktops, List<string> Binaries);

    private async Task<Shipment> ShipAsync(string pkg, CancellationToken ct)
    {
        var r = await _ct.ExecAsync(new[] { "pacman", "-Ql", pkg }, ct: ct, timeoutSeconds: 120);
        var desktops = new List<(string, string)>();
        var binaries = new List<string>();
        if (!r.Ok) return new Shipment(desktops, binaries);
        var seen = new HashSet<string>();
        foreach (var line in r.Stdout.Split('\n'))
        {
            var t = line.Trim();
            var sp = t.IndexOf(' ');
            var path = sp > 0 ? t[(sp + 1)..].Trim() : t;
            if (path.EndsWith(".desktop", StringComparison.Ordinal)
                && (path.StartsWith("/usr/share/applications/", StringComparison.Ordinal)
                    || path.StartsWith("/usr/local/share/applications/", StringComparison.Ordinal)))
            {
                var baseName = Path.GetFileNameWithoutExtension(path);
                if (seen.Add(baseName)) desktops.Add((baseName, path));
            }
            else if (path.StartsWith("/usr/bin/", StringComparison.Ordinal))
            {
                binaries.Add(path["/usr/bin/".Length..]);
            }
        }
        return new Shipment(desktops, binaries);
    }

    // ---------- Export after install ----------

    /// Exports GUI apps (desktop entries) and falls back to a PATH binary for
    /// CLI-only packages. Returns the main icon path cached locally (if any).
    public async Task<string?> ExportPackageAsync(string pkg, Action<string> log, CancellationToken ct)
    {
        var ship = await ShipAsync(pkg, ct);
        var exportedDesktops = new List<string>();
        var exportedBins = new List<string>();

        if (ship.Desktops.Count > 0)
        {
            foreach (var (baseName, absPath) in ship.Desktops.Take(5))
            {
                log($"Exporting {baseName} to your desktop...");
                // distrobox-export (>=1.x) takes --app <name|absolute .desktop path>.
                // App names may not match package names, so pass the absolute desktop
                // file path, which the exporter resolves and copies.
                var e = await _ct.ExecAsync(
                    new[] { "distrobox-export", "--app", absPath }, ct: ct, timeoutSeconds: 120);
                var hostFile = Path.Combine(HostDesktopDir, _ct.Name + "-" + baseName + ".desktop");
                if (File.Exists(hostFile) && e.Ok)
                {
                    exportedDesktops.Add(hostFile);
                    log($"  -> {hostFile}");
                }
                else
                {
                    log("  (no desktop entry was created: " + LastErr(e) + ")");
                }
            }
        }
        else if (ship.Binaries.Count > 0)
        {
            var bin = ship.Binaries.Contains(pkg) ? pkg : ship.Binaries[0];
            log($"No GUI entry found; exposing '{bin}' on your PATH...");
            var e = await _ct.ExecAsync(
                new[] { "distrobox-export", "--bin", "/usr/bin/" + bin }, ct: ct, timeoutSeconds: 120);
            if (e.Ok && File.Exists(Path.Combine(BinDir, bin))) exportedBins.Add(bin);
            else log("  (PATH export failed: " + LastErr(e) + ")");
        }
        else
        {
            log("Package ships neither a desktop entry nor a /usr/bin binary — nothing to export.");
        }

        // icon for the UI (first desktop entry's icon resolved + copied)
        string? iconFile = null;
        if (ship.Desktops.Count > 0)
        {
            var iconName = await DesktopIconAsync(ship.Desktops[0].AbsPath, ct);
            iconFile = await FetchIconAsync(pkg, iconName, ct);
        }

        if (exportedDesktops.Count > 0)
            _state.Exports[pkg] = exportedDesktops.Distinct().ToList();
        else
            _state.Exports.Remove(pkg);
        if (exportedBins.Count > 0)
            _state.BinExports[pkg] = exportedBins.Distinct().ToList();
        else
            _state.BinExports.Remove(pkg);
        if (ship.Binaries.Count > 0)
            _state.LaunchBin[pkg] = "/usr/bin/" + (ship.Binaries.Contains(pkg) ? pkg : ship.Binaries[0]);
        if (iconFile != null)
            _state.Icons[pkg] = iconFile;
        AppStateStore.Save(_state);
        return iconFile;
    }

    private static string BinDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");

    private static string LastErr(RunResult r)
        => (r.Stderr + r.Stdout).Trim().Split('\n').Where(l => l.Trim().Length > 0).TakeLast(1).FirstOrDefault() ?? $"exit {r.ExitCode}";

    // ---------- Uninstall cleanup ----------

    public async Task CleanupExportsAsync(string pkg, Action<string> log, CancellationToken ct)
    {
        if (_state.Exports.TryGetValue(pkg, out var desktops))
        {
            foreach (var hostFile in desktops)
            {
                log("Removing desktop integration: " + Path.GetFileName(hostFile));
                try
                {
                    var baseName = Path.GetFileNameWithoutExtension(hostFile);
                    // distrobox-export names host files "<container>-<app>"
                    if (baseName.StartsWith(_ct.Name + "-", StringComparison.Ordinal))
                        baseName = baseName[(_ct.Name.Length + 1)..];
                    await _ct.ExecAsync(new[] { "distrobox-export", "--app", baseName, "--delete" }, ct: ct, timeoutSeconds: 60);
                }
                catch { }
                try { File.Delete(hostFile); } catch { }
            }
            _state.Exports.Remove(pkg);
        }
        if (_state.BinExports.TryGetValue(pkg, out var bins))
        {
            foreach (var b in bins)
            {
                try
                {
                    await _ct.ExecAsync(new[] { "distrobox-export", "--bin", "/usr/bin/" + b, "--delete" }, ct: ct, timeoutSeconds: 60);
                    File.Delete(Path.Combine(BinDir, b));
                }
                catch { }
            }
            _state.BinExports.Remove(pkg);
        }
        _state.LaunchBin.Remove(pkg);
        if (_state.Icons.TryGetValue(pkg, out var icon) && icon != null)
        {
            try { File.Delete(icon); } catch { }
            _state.Icons.Remove(pkg);
        }
        AppStateStore.Save(_state);
    }

    // ---------- Icon handling ----------

    private async Task<string?> DesktopIconAsync(string desktopPathInContainer, CancellationToken ct)
    {
        var r = await _ct.ExecAsync(new[] { "cat", desktopPathInContainer }, ct: ct, timeoutSeconds: 30);
        if (!r.Ok) return null;
        string? icon = null;
        foreach (var raw in r.Stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("Icon=", StringComparison.Ordinal))
            {
                icon = line["Icon=".Length..].Trim();
                break;
            }
        }
        return string.IsNullOrEmpty(icon) ? null : icon;
    }

    /// Resolves an icon (theme name or path) inside the container and copies it to
    /// the local cache. Returns the local file path.
    public async Task<string?> FetchIconAsync(string pkg, string? icon, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(icon)) return null;
        if (icon.StartsWith('/'))
        {
            var p = await PodmanCpAsync(pkg, icon, ct);
            if (p != null) return p;
            return null;
        }
        // Bare theme icon name -> probe standard theme paths inside the container.
        var candidates = new List<string>();
        var sizes = new[] { "256x256", "scalable", "128x128", "64x64", "48x48", "32x32" };
        var exts = new[] { "png", "svg", "xpm" };
        foreach (var size in sizes)
            foreach (var ext in exts)
                candidates.Add($"/usr/share/icons/hicolor/{size}/apps/{icon}.{ext}");
        foreach (var ext in exts)
            candidates.Add($"/usr/share/pixmaps/{icon}.{ext}");
        // de-duplicate while preserving preference order
        candidates = candidates.Distinct().ToList();

        var script = "for f in \"$@\"; do [ -f \"$f\" ] && { echo \"$f\"; exit 0; }; done; exit 1";
        var args = new List<string> { "sh", "-c", script, "sh" };
        args.AddRange(candidates);
        var r = await _ct.ExecAsync(args, ct: ct, timeoutSeconds: 60);
        if (!r.Ok) return null;
        var found = r.Stdout.Trim().Split('\n').FirstOrDefault();
        if (string.IsNullOrEmpty(found)) return null;
        return await PodmanCpAsync(pkg, found, ct);
    }

    private async Task<string?> PodmanCpAsync(string pkg, string containerPath, CancellationToken ct)
    {
        var ext = Path.GetExtension(containerPath).ToLowerInvariant();
        if (ext is not (".png" or ".svg" or ".jpg" or ".jpeg" or ".xpm")) ext = ".png";
        var dest = Path.Combine(AppStateStore.IconCacheDir, Sanitize(pkg) + ext);
        var r = await Runner.CapturedAsync(new[] { "podman", "cp", _ct.Name + ":" + containerPath, dest }, ct, 60);
        return r.Ok && File.Exists(dest) ? dest : null;
    }

    private static string Sanitize(string s)
        => Regex.Replace(s, @"[^A-Za-z0-9._-]", "_");

    // ---------- Host app inventory (what is integrated on the desktop) ----------

    /// Lists host .desktop entries that launch into the backing container.
    public List<HostApp> ListHostApps()
    {
        var apps = new List<HostApp>();
        if (!Directory.Exists(HostDesktopDir)) return apps;
        foreach (var f in Directory.GetFiles(HostDesktopDir, _ct.Name + "-*.desktop"))
        {
            var ini = ParseDesktop(File.ReadAllText(f));
            if (ini == null) continue;
            var name = ini.GetValueOrDefault("Name") ?? Path.GetFileNameWithoutExtension(f);
            // distrobox appends " (on <container>)" to exported app names — hide it.
            name = Regex.Replace(name, @"\s*\(on\s+.+\)\s*$", "").Trim();
            string? pkg = null;
            foreach (var (p, files) in _state.Exports)
                if (files.Contains(f)) pkg = p;
            apps.Add(new HostApp
            {
                Name = name,
                DesktopFile = f,
                IconName = ini.GetValueOrDefault("Icon") ?? "",
                Exec = ini.GetValueOrDefault("Exec") ?? "",
                PackageName = pkg,
            });
        }
        return apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static Dictionary<string, string>? ParseDesktop(string text)
    {
        var m = new Dictionary<string, string>();
        bool inEntry = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('['))
            {
                inEntry = line == "[Desktop Entry]";
                continue;
            }
            if (!inEntry) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq];
            if (key.Contains('[')) continue; // localized Name[de]
            m[key] = line[(eq + 1)..].Trim();
        }
        return m.Count == 0 ? null : m;
    }

    public static void LaunchDesktopFile(string desktopFile)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo { FileName = "gio", UseShellExecute = false };
            psi.ArgumentList.Add("launch");
            psi.ArgumentList.Add(desktopFile);
            System.Diagnostics.Process.Start(psi);
        }
        catch { /* fire-and-forget */ }
    }
}
