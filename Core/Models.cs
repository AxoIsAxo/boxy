namespace Boxy.Core;

/// A package discoverable/installable through the backing Arch container.
public sealed class PackageInfo
{
    public required string Name { get; set; }
    public string Version { get; set; } = "";
    /// Repository tag: "core", "extra", "multilib" or "aur".
    public required string Repo { get; set; }
    public string Description { get; set; } = "";
    public bool Installed { get; set; }
    public string InstalledVersion { get; set; } = "";

    // Official-repo detail fields (pacman -Si).
    public string Url { get; set; } = "";
    public string License { get; set; } = "";
    public string DownloadSize { get; set; } = "";
    public string InstalledSize { get; set; } = "";
    public string DependsOn { get; set; } = "";

    // AUR detail fields (AUR RPC).
    public int AurVotes { get; set; }
    public double AurPopularity { get; set; }
    public string AurMaintainer { get; set; } = "";
    public bool AurOutOfDate { get; set; }
    public string AurPackageBase { get; set; } = "";

    public bool FromAur => Repo == "aur";
    public string RepoLabel => FromAur ? "AUR" : Repo;
}

/// A host-side integration created by distrobox-export (or pre-existing).
public sealed class HostApp
{
    public required string Name { get; set; }
    /// Absolute path of the .desktop file on the host.
    public required string DesktopFile { get; set; }
    public string IconName { get; set; } = "";
    public string Exec { get; set; } = "";
    /// Backing package inside the container, when tracked by Boxy.
    public string? PackageName { get; set; }
    public bool IsGui => true;
}

/// Persisted state in ~/.config/boxy/state.json.
public sealed class AppState
{
    public string Container { get; set; } = "arch-box";
    /// package name -> host desktop file paths exported for it.
    public Dictionary<string, List<string>> Exports { get; set; } = new();
    /// package name -> exported CLI binary names (distrobox-export --bin).
    public Dictionary<string, List<string>> BinExports { get; set; } = new();
    /// package name -> cached icon file path on host.
    public Dictionary<string, string> Icons { get; set; } = new();
    /// package name -> preferred launch binary inside the container.
    public Dictionary<string, string> LaunchBin { get; set; } = new();
}
