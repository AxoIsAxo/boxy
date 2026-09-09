using System.Text.Json;

namespace Boxy.Core;

public sealed class AppStateStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "boxy");
    private static readonly string File = Path.Combine(Dir, "state.json");
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static string ConfigDir => Dir;

    public static AppState Load()
    {
        try
        {
            if (System.IO.File.Exists(File))
                return JsonSerializer.Deserialize<AppState>(System.IO.File.ReadAllText(File)) ?? new AppState();
        }
        catch { /* corrupted state: start fresh */ }
        return new AppState();
    }

    public static void Save(AppState state)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(state, Opts));
        }
        catch { /* non-fatal */ }
    }

    /// Cache dir for icons copied out of the container.
    public static string IconCacheDir
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "boxy", "icons");
            Directory.CreateDirectory(d);
            return d;
        }
    }
}
