using System.IO;
using System.Text.RegularExpressions;

namespace LuaToolsGui.Services;

/// <summary>
/// Resolves where a Steam game is installed on disk by walking Valve's KeyValues files:
/// registry Steam root → steamapps\libraryfolders.vdf (every library, possibly across drives) →
/// per-library steamapps\appmanifest_&lt;appid&gt;.acf (the game's installdir) → common\&lt;installdir&gt;.
/// Best-effort: returns null if Steam/the game can't be located. Used to apply Denuvo "fix" zips.
/// </summary>
public partial class SteamLibraryService(SteamService steam)
{
    // "path"        "D:\\SteamLibrary"     → the library root (libraryfolders.vdf)
    // "installdir"  "Elden Ring"           → folder under steamapps\common (appmanifest_*.acf)
    // Values are quoted; backslashes are escaped (\\). One key per line.
    [GeneratedRegex(@"""path""\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"""installdir""\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex InstallDirRegex();

    /// <summary>
    /// Full path to the game's install folder (…\steamapps\common\&lt;installdir&gt;) if it exists on
    /// disk, else null (game not installed / Steam not found / unreadable).
    /// </summary>
    public string? GetInstallDir(long appId)
    {
        try
        {
            string? steamRoot = steam.EffectivePath;
            if (steamRoot is null) return null;

            foreach (string library in GetLibraryRoots(steamRoot))
            {
                string acf = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
                if (!File.Exists(acf)) continue;

                var m = InstallDirRegex().Match(File.ReadAllText(acf));
                if (!m.Success) continue;

                string installDir = Unescape(m.Groups[1].Value);
                string full = Path.Combine(library, "steamapps", "common", installDir);
                if (Directory.Exists(full)) return full;
            }
        }
        catch { /* unreadable VDF/ACF or odd path. Treat as not found */ }
        return null;
    }

    // "appid" "480" / "name" "Spacewar" — the two keys we need out of an appmanifest, same quoted
    // one-per-line KeyValues shape as the rest of this file.
    [GeneratedRegex(@"""appid""\s*""(\d+)""", RegexOptions.IgnoreCase)]
    private static partial Regex AppIdRegex();

    [GeneratedRegex(@"""name""\s*""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex NameRegex();

    // "LastPlayed" "1788083097" — unix seconds, and "0" when the game has never been launched.
    [GeneratedRegex(@"""LastPlayed""\s*""(\d+)""", RegexOptions.IgnoreCase)]
    private static partial Regex LastPlayedRegex();

    /// <summary>
    /// When Steam last launched this game, or null if it can't be established. Zero means never — which
    /// is the only value this is really asked for.
    /// </summary>
    /// <remarks>
    /// Steam stopped recording per-game playtime in localconfig.vdf, so the manifest's LastPlayed is
    /// what is left to tell a game that has been played from one that has only been installed.
    /// </remarks>
    public DateTimeOffset? GetLastPlayed(long appId)
    {
        string? steamRoot = steam.EffectivePath;
        if (steamRoot is null) return null;

        foreach (string library in GetLibraryRoots(steamRoot))
        {
            string acf = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
            try
            {
                if (!File.Exists(acf)) continue;
                if (ReadLastPlayed(File.ReadAllText(acf)) is { } seconds)
                    return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
            catch { /* unreadable manifest: treat as unknown */ }
        }

        return null;
    }

    /// <summary>The LastPlayed stamp out of an appmanifest, or null when the key is absent.</summary>
    internal static long? ReadLastPlayed(string manifest)
    {
        var match = LastPlayedRegex().Match(manifest);
        return match.Success && long.TryParse(match.Groups[1].Value, out long seconds) ? seconds : null;
    }

    /// <summary>
    /// Enumerate the installed games across every library by reading each appmanifest_&lt;appid&gt;.acf.
    /// Best-effort per file: an unreadable or half-written manifest is skipped, never fatal. The name is
    /// the one Steam itself stores, so it's correct even for apps missing from the public app list.
    /// </summary>
    public IEnumerable<(long AppId, string Name)> EnumerateInstalled()
    {
        string? steamRoot = steam.EffectivePath;
        if (steamRoot is null) yield break;

        var seen = new HashSet<long>();
        foreach (string library in GetLibraryRoots(steamRoot))
        {
            string[] manifests;
            try { manifests = Directory.GetFiles(Path.Combine(library, "steamapps"), "appmanifest_*.acf"); }
            catch { continue; } // library on a disconnected drive

            foreach (string manifest in manifests)
            {
                string text;
                try { text = File.ReadAllText(manifest); } catch { continue; }

                var idMatch = AppIdRegex().Match(text);
                if (!idMatch.Success || !long.TryParse(idMatch.Groups[1].Value, out long appId)) continue;
                if (!seen.Add(appId)) continue; // same game listed in two libraries

                var nameMatch = NameRegex().Match(text);
                yield return (appId, nameMatch.Success ? nameMatch.Groups[1].Value : appId.ToString());
            }
        }
    }

    /// <summary>Every Steam library root (the main install plus any added libraries).</summary>
    private static IEnumerable<string> GetLibraryRoots(string steamRoot)
    {
        // The main install is always a library.
        yield return steamRoot;

        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string text;
        try { text = File.ReadAllText(vdf); } catch { yield break; }

        foreach (Match m in PathRegex().Matches(text))
        {
            string path = Unescape(m.Groups[1].Value);
            // The main root often appears here too; harmless duplicate (we just probe each).
            if (!string.Equals(path, steamRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(path))
                yield return path;
        }
    }

    /// <summary>VDF strings escape backslashes as "\\"; collapse to a real path.</summary>
    private static string Unescape(string s) => s.Replace(@"\\", @"\");
}
