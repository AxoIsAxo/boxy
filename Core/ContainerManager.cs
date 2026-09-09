namespace Boxy.Core;

/// Owns the backing Arch distrobox container: detection, creation and bootstrap
/// (multilib enabled, base-devel, yay for the AUR).
public sealed class ContainerManager
{
    public string Name { get; }
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool? _yayChecked;

    public ContainerManager(string name) => Name = name;

    public string[] Distro(IReadOnlyList<string> inner)
    {
        var l = new List<string> { "distrobox", "enter", Name, "--" };
        l.AddRange(inner);
        return l.ToArray();
    }

    /// Runs a command inside the container (gate keeps podman single-flight).
    public async Task<RunResult> ExecAsync(
        IReadOnlyList<string> inner, Action<string>? onOutput = null, Action<string>? onError = null,
        SynchronizationContext? sync = null, CancellationToken ct = default, int? timeoutSeconds = null)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await Runner.RunAsync(Distro(inner), onOutput, onError, sync, ct, timeoutSeconds);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> ExistsAsync(CancellationToken ct = default)
    {
        var r = await Runner.CapturedAsync(new[] { "distrobox", "list" }, ct, 30);
        if (!r.Ok) return false;
        foreach (var line in r.Stdout.Split('\n'))
        {
            var cols = line.Split('|', StringSplitOptions.TrimEntries);
            if (cols.Length >= 2 && cols[1] == Name) return true;
        }
        return false;
    }

    public async Task<bool> HasYayAsync(CancellationToken ct = default)
    {
        if (_yayChecked is bool b) return b;
        var r = await Runner.CapturedAsync(new[] { "distrobox", "enter", Name, "--", "sh", "-lc", "command -v yay" }, ct, 60);
        _yayChecked = r.Ok;
        return r.Ok;
    }

    /// Detects whether the backing container exists and is ready (has pacman/yay).
    public async Task<(bool Exists, bool Ready)> StatusAsync(CancellationToken ct = default)
    {
        bool exists = await ExistsAsync(ct);
        if (!exists) return (false, false);
        bool yay = await HasYayAsync(ct);
        return (true, yay);
    }

    /// Creates and bootstraps a fresh Arch container with multilib + AUR access.
    public async Task CreateAndBootstrapAsync(Action<string> log, SynchronizationContext? sync, CancellationToken ct)
    {
        log("Creating Arch Linux container '" + Name + "' (first run downloads the base image)...");
        var create = await Runner.RunAsync(
            new[] { "distrobox", "create", "--yes", "--image", "docker.io/library/archlinux:latest", "--name", Name },
            log, null, sync, ct, 60 * 20);
        if (!create.Ok) throw new InvalidOperationException("Failed to create container:\n" + create.Stderr + create.Stdout);

        log("Enabling the multilib repository...");
        // Uncomment the [multilib] section (and its Include line only).
        var sed = await ExecAsync(new[]
        {
            "sh", "-lc",
            "sed -i '/^#[[]multilib[]]/{s/^#//;n;s/^#Include = /Include = /}' /etc/pacman.conf && grep -A1 '^[[]multilib[]]' /etc/pacman.conf"
        }, ct: ct, timeoutSeconds: 60);
        if (!sed.Ok) throw new InvalidOperationException("Could not enable multilib:\n" + sed.Stderr);

        log("Synchronizing repositories (pacman -Syu)...");
        var upd = await ExecAsync(new[] { "sudo", "pacman", "-Syu", "--noconfirm" }, log, null, sync, ct, 60 * 30);
        if (!upd.Ok) throw new InvalidOperationException("System update failed:\n" + upd.Stderr);

        log("Installing base-devel and git (needed to build AUR packages)...");
        var dev = await ExecAsync(new[] { "sudo", "pacman", "-S", "--noconfirm", "--needed", "base-devel", "git" },
            log, null, sync, ct, 60 * 30);
        if (!dev.Ok) throw new InvalidOperationException("base-devel install failed:\n" + dev.Stderr);

        log("Installing yay (AUR helper)...");
        var yay = await ExecAsync(new[]
        {
            "sh", "-lc",
            "rm -rf /tmp/yay-bin && git clone --depth 1 https://aur.archlinux.org/yay-bin.git /tmp/yay-bin " +
            "&& cd /tmp/yay-bin && makepkg -si --noconfirm && rm -rf /tmp/yay-bin"
        }, log, null, sync, ct, 60 * 20);
        if (!yay.Ok) throw new InvalidOperationException("yay install failed:\n" + yay.Stderr);

        _yayChecked = true;
        log("Container '" + Name + "' is ready: core, extra, multilib and AUR available.");
    }
}
