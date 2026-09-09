using System.Text.Json;

namespace Boxy.Core;

/// Search/info/install/update operations against the Arch container repositories
/// (official: core/extra/multilib via pacman; AUR via the AUR RPC + yay).
public sealed class PackageService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };
    private readonly ContainerManager _ct;
    private readonly AppState _state;

    public PackageService(ContainerManager ct, AppState state)
    {
        _ct = ct;
        _state = state;
    }

    private async Task<HashSet<string>> InstalledSetAsync(CancellationToken ct)
    {
        var r = await _ct.ExecAsync(new[] { "pacman", "-Qq" }, ct: ct, timeoutSeconds: 120);
        if (!r.Ok) return new HashSet<string>();
        var set = new HashSet<string>();
        foreach (var line in r.Stdout.Split('\n'))
        {
            var name = line.Trim();
            if (name.Length > 0) set.Add(name);
        }
        return set;
    }

    // ---------- Search ----------

    /// Searches official repositories + AUR. Sizes are capped for the UI grid.
    public async Task<List<PackageInfo>> SearchAsync(string query, CancellationToken ct)
    {
        var installed = await InstalledSetAsync(ct);
        var results = new Dictionary<string, PackageInfo>(StringComparer.Ordinal);

        // Official: pacman -Ss. Format:
        //   extra/alsa-utils 1.2.14-1 [installed]
        //       Advanced Linux sound architecture utilities
        var official = await _ct.ExecAsync(
            new[] { "env", "LC_ALL=C", "pacman", "-Ss", "--color", "never", "--", query }, ct: ct, timeoutSeconds: 90);
        if (official.Ok) ParsePacmanSearch(official.Stdout, installed, results);

        // AUR: host-side AUR RPC (no container round trip).
        try
        {
            var url = "https://aur.archlinux.org/rpc/v5/search/" + Uri.EscapeDataString(query) + "?by=name-desc";
            using var resp = await Http.GetAsync(url, ct);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("results", out var arr))
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        var name = el.TryGetProperty("Name", out var n) ? n.GetString() : null;
                        if (string.IsNullOrEmpty(name) || results.ContainsKey(name!)) continue;
                        results[name!] = new PackageInfo
                        {
                            Name = name!,
                            Version = el.TryGetProperty("Version", out var v) ? v.GetString() ?? "" : "",
                            Repo = "aur",
                            Description = el.TryGetProperty("Description", out var d) ? d.GetString() ?? "" : "",
                            Installed = installed.Contains(name!),
                            AurVotes = el.TryGetProperty("NumVotes", out var nv) ? nv.GetInt32() : 0,
                            AurPopularity = el.TryGetProperty("Popularity", out var pop) && pop.ValueKind == JsonValueKind.Number ? pop.GetDouble() : 0,
                        };
                        if (results.Count >= 120) break;
                    }
                }
            }
        }
        catch { /* AUR unreachable — official results still shown */ }

        return results.Values
            .OrderBy(p => p.Repo == "aur")                       // official repos first
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ParsePacmanSearch(string output, HashSet<string> installed, Dictionary<string, PackageInfo> into)
    {
        string? header = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) { header = null; continue; }
            if (!line.StartsWith(' '))
            {
                header = line;                                  // new match block
                continue;
            }
            if (header is null) continue;                       // stray indent
            var desc = line.Trim();
            var (repo, name, version, inst) = ParsePacmanHeader(header);
            header = null;
            if (name is null) continue;
            if (into.TryGetValue(name, out var existing))
            {
                if (existing.Description.Length == 0) existing.Description = desc;
                continue;                                       // official entry already present
            }
            into[name] = new PackageInfo
            {
                Name = name, Version = version, Repo = repo,
                Description = desc, Installed = inst || installed.Contains(name),
            };
        }
    }

    private static (string repo, string name, string version, bool installed) ParsePacmanHeader(string header)
    {
        var h = header.Trim();
        bool inst = h.EndsWith(" [installed]", StringComparison.Ordinal);
        if (inst) h = h[..^"[installed]".Length].TrimEnd();
        var slash = h.IndexOf('/');
        if (slash <= 0) return ("", "", "", inst);
        var repo = h[..slash];
        var rest = h[(slash + 1)..].Trim();
        var sp = rest.IndexOf(' ');
        return (repo, sp < 0 ? rest : rest[..sp], sp < 0 ? "" : rest[(sp + 1)..].Trim(), inst);
    }

    // ---------- Detail ----------

    public async Task<PackageInfo> InfoAsync(PackageInfo baseInfo, CancellationToken ct)
    {
        if (baseInfo.FromAur)
            return await AurInfoAsync(baseInfo, ct);

        var r = await _ct.ExecAsync(new[] { "env", "LC_ALL=C", "pacman", "-Si", baseInfo.Name }, ct: ct, timeoutSeconds: 60);
        var m = r.Ok ? ParseSi(r.Stdout) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var qi = await _ct.ExecAsync(new[] { "env", "LC_ALL=C", "pacman", "-Q", baseInfo.Name }, ct: ct, timeoutSeconds: 30);
        bool isInstalled = qi.Ok;
        string installedVersion = isInstalled && qi.Stdout.Trim().Split(' ').Length > 1
            ? qi.Stdout.Trim().Split(' ')[1] : "";
        return new PackageInfo
        {
            Name = baseInfo.Name,
            Version = m.GetValueOrDefault("Version") ?? baseInfo.Version,
            Repo = m.GetValueOrDefault("Repository") ?? baseInfo.Repo,
            Description = m.GetValueOrDefault("Description") ?? baseInfo.Description,
            Installed = isInstalled || baseInfo.Installed,
            InstalledVersion = installedVersion,
            Url = m.GetValueOrDefault("URL") ?? "",
            License = m.GetValueOrDefault("Licenses") ?? "",
            DownloadSize = m.GetValueOrDefault("Download Size") ?? "",
            InstalledSize = m.GetValueOrDefault("Installed Size") ?? "",
            DependsOn = m.GetValueOrDefault("Depends On") ?? "",
        };
    }

    private async Task<PackageInfo> AurInfoAsync(PackageInfo baseInfo, CancellationToken ct)
    {
        var installed = baseInfo.Installed;
        var installedVersion = baseInfo.InstalledVersion;
        try
        {
            var qi = await _ct.ExecAsync(new[] { "env", "LC_ALL=C", "pacman", "-Q", baseInfo.Name }, ct: ct, timeoutSeconds: 30);
            installed = qi.Ok;
            if (qi.Ok && qi.Stdout.Trim().Split(' ').Length > 1)
                installedVersion = qi.Stdout.Trim().Split(' ')[1];
        }
        catch { }

        try
        {
            var url = "https://aur.archlinux.org/rpc/v5/info?arg[]=" + Uri.EscapeDataString(baseInfo.Name);
            using var resp = await Http.GetAsync(url, ct);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("results", out var arr) && arr.GetArrayLength() > 0)
                {
                    var el = arr[0];
                    return new PackageInfo
                    {
                        Name = baseInfo.Name,
                        Version = el.TryGetProperty("Version", out var v) ? v.GetString() ?? "" : baseInfo.Version,
                        Repo = "aur",
                        Description = el.TryGetProperty("Description", out var d) ? d.GetString() ?? "" : baseInfo.Description,
                        Installed = installed,
                        InstalledVersion = installedVersion,
                        Url = el.TryGetProperty("URL", out var u) ? u.GetString() ?? "" : "",
                        License = el.TryGetProperty("License", out var lic) && lic.ValueKind == JsonValueKind.Array
                            ? string.Join(", ", lic.EnumerateArray().Select(x => x.GetString() ?? "")) : "",
                        AurVotes = el.TryGetProperty("NumVotes", out var nv) ? nv.GetInt32() : 0,
                        AurPopularity = el.TryGetProperty("Popularity", out var pop) && pop.ValueKind == JsonValueKind.Number ? pop.GetDouble() : 0,
                        AurMaintainer = el.TryGetProperty("Maintainer", out var mt) ? mt.GetString() ?? "" : "",
                        AurOutOfDate = el.TryGetProperty("OutOfDate", out var ood) && ood.ValueKind != JsonValueKind.Null,
                        AurPackageBase = el.TryGetProperty("PackageBase", out var pb) ? pb.GetString() ?? "" : "",
                    };
                }
            }
        }
        catch { /* AUR unreachable — keep search metadata */ }
        return ApplyInstalled(baseInfo, installed, installedVersion);
    }

    private static PackageInfo ApplyInstalled(PackageInfo p, bool installed, string version)
        => new PackageInfo
        {
            Name = p.Name, Version = p.Version, Repo = p.Repo, Description = p.Description,
            Installed = installed, InstalledVersion = version,
            Url = p.Url, License = p.License, DownloadSize = p.DownloadSize, InstalledSize = p.InstalledSize,
            DependsOn = p.DependsOn, AurVotes = p.AurVotes, AurPopularity = p.AurPopularity,
            AurMaintainer = p.AurMaintainer, AurOutOfDate = p.AurOutOfDate, AurPackageBase = p.AurPackageBase,
        };

    private static Dictionary<string, string> ParseSi(string output)
    {
        var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? last = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) { last = null; continue; }
            int idx = line.IndexOf(':');
            if (idx > 0)
            {
                last = line[..idx].Trim();
                m[last] = line[(idx + 1)..].Trim();
            }
            else if (last != null && m.TryGetValue(last, out var prev))
            {
                m[last] = prev + " " + line.Trim();             // wrapped list continuation
            }
        }
        return m;
    }

    // ---------- Updates ----------

    /// Names of packages with pending updates (official + AUR via yay -Qu when present).
    public async Task<List<string>> UpdatesAsync(bool hasYay, CancellationToken ct)
    {
        var r = hasYay
            ? await _ct.ExecAsync(new[] { "yay", "-Qu" }, ct: ct, timeoutSeconds: 120)
            : await _ct.ExecAsync(new[] { "env", "LC_ALL=C", "pacman", "-Qu" }, ct: ct, timeoutSeconds: 120);
        if (!r.Ok) return new List<string>();
        var names = new List<string>();
        foreach (var line in r.Stdout.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            int arrow = t.IndexOf("->", StringComparison.Ordinal);   // yay: "name 1.0 -> 1.1"
            if (arrow > 0) t = t[..arrow].Trim();
            var first = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (first is null) continue;
            int slash = first.IndexOf('/');                          // yay aur: "aur/name"
            if (slash > 0) first = first[(slash + 1)..];
            if (first.Length > 0 && !names.Contains(first)) names.Add(first);
        }
        return names;
    }

    // ---------- Install / remove / update all ----------

    public async Task InstallAsync(PackageInfo pkg, Action<string> log, CancellationToken ct)
    {
        if (pkg.FromAur)
        {
            log($"Installing {pkg.Name} from the AUR via yay...");
            log("AUR packages install user-supplied PKGBUILDs. Only install what you trust.");
            var r = await _ct.ExecAsync(new[]
            {
                "yay", "-S", "--noconfirm", "--answerdiff", "None", "--answerclean", "None",
                "--mflags", "--noconfirm", pkg.Name
            }, log, log, ct: ct, timeoutSeconds: 60 * 45);
            if (!r.Ok) throw new InvalidOperationException($"yay failed (exit {r.ExitCode}).\n" + Last(r.Stderr) + Last(r.Stdout));
        }
        else
        {
            log($"Installing {pkg.Name} from the {pkg.Repo} repository...");
            var r = await _ct.ExecAsync(new[] { "sudo", "pacman", "-S", "--noconfirm", "--needed", pkg.Name },
                log, log, ct: ct, timeoutSeconds: 60 * 30);
            if (!r.Ok) throw new InvalidOperationException($"pacman failed (exit {r.ExitCode}).\n" + Last(r.Stderr) + Last(r.Stdout));
        }
        log("Installation finished.");
    }

    public async Task RemoveAsync(PackageInfo pkg, Action<string> log, CancellationToken ct)
    {
        log($"Removing {pkg.Name} and orphaned dependencies...");
        var r = await _ct.ExecAsync(new[] { "sudo", "pacman", "-Rns", "--noconfirm", pkg.Name },
            log, log, ct: ct, timeoutSeconds: 60 * 20);
        if (!r.Ok) throw new InvalidOperationException($"pacman failed (exit {r.ExitCode}).\n" + Last(r.Stderr) + Last(r.Stdout));
        log("Removed.");
    }

    public async Task UpdateAllAsync(bool hasYay, Action<string> log, CancellationToken ct)
    {
        if (hasYay)
        {
            log("Updating everything (official repositories + AUR)...");
            var r = await _ct.ExecAsync(new[]
            {
                "yay", "-Syu", "--noconfirm", "--answerdiff", "None", "--answerclean", "None",
                "--mflags", "--noconfirm"
            }, log, log, ct: ct, timeoutSeconds: 60 * 60);
            if (!r.Ok) throw new InvalidOperationException($"Update failed (exit {r.ExitCode}).\n" + Last(r.Stderr) + Last(r.Stdout));
        }
        else
        {
            log("Updating official repositories...");
            var r = await _ct.ExecAsync(new[] { "sudo", "pacman", "-Syu", "--noconfirm" }, log, log, ct: ct, timeoutSeconds: 60 * 40);
            if (!r.Ok) throw new InvalidOperationException($"Update failed (exit {r.ExitCode}).\n" + Last(r.Stderr) + Last(r.Stdout));
        }
        log("System is up to date.");
    }

    private static string Last(string s)
    {
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 ? string.Join('\n', lines.TakeLast(4)) + "\n" : "";
    }
}
