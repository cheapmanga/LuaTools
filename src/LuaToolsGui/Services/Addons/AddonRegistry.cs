using System.IO;
using System.Text.Json;

namespace LuaToolsGui.Services.Addons;

/// <summary>Why an addon is, or isn't, contributing anything.</summary>
public enum AddonState
{
    /// <summary>Read and accepted; its sources are live.</summary>
    Loaded,

    /// <summary>Present on disk, switched off by the user.</summary>
    Disabled,

    /// <summary>Refused. <see cref="LoadedAddon.Error"/> says why, in words for the user.</summary>
    Failed,
}

/// <summary>One addon folder, after the registry has had its way with it.</summary>
public sealed class LoadedAddon
{
    public required AddonManifest Manifest { get; init; }
    public required string Directory { get; init; }
    public required AddonState State { get; set; }

    /// <summary>User-facing reason when <see cref="State"/> is <see cref="AddonState.Failed"/>.</summary>
    public string? Error { get; set; }

    public List<ManifestSourceDescriptor> Sources { get; } = [];

    /// <summary>
    /// The folder name, which is the addon's identity of record everywhere it matters: diagnostics, the
    /// enabled/disabled list, the Addons row. The manifest's own id is only ever CHECKED against this,
    /// never trusted over it — a message about a manifest that lies about its id must not be filed under
    /// the id it lied about, or it names an addon that exists nowhere on disk and the user cannot find
    /// the folder to fix.
    /// </summary>
    public string FolderName => Path.GetFileName(Directory.TrimEnd(Path.DirectorySeparatorChar));
}

/// <summary>
/// Finds addons and collects the manifest sources they declare.
/// </summary>
/// <remarks>
/// <para>An addon is <b>data only</b>. There is deliberately no way for one to supply code, a binary or
/// a fetch routine of its own: it names one of the shapes the app already knows how to consume, and the
/// app does the fetching. Installing an addon from a stranger cannot execute anything — which is what
/// makes it reasonable to install one at all.</para>
///
/// <para>The rule this class is built around: <b>a broken addon must never be able to stop the app from
/// starting.</b> Every stage is wrapped, every failure is recorded against the folder it came from and
/// the loop moves on. The user finds out on the Addons page instead of by the app not opening.</para>
/// </remarks>
public sealed class AddonRegistry
{
    /// <summary>Highest <c>addon.json</c> schema this build understands.</summary>
    private const int SupportedSchema = 1;

    /// <summary>Source names the host owns. An addon claiming one is refused, never silently ignored.</summary>
    private static readonly HashSet<string> ReservedSourceNames =
        new(StringComparer.OrdinalIgnoreCase)
        { "manifesthub", "sushi", "luatools", "hubcap", "sadie", "ryuu", "manifestcache" };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaToolsGui", "addons");

    private readonly List<LoadedAddon> _addons = [];
    private readonly List<string> _diagnostics = [];

    /// <summary>Every addon folder found, whatever became of it. Ordered by display name.</summary>
    public IReadOnlyList<LoadedAddon> Addons => _addons;

    /// <summary>Sources contributed by the addons that loaded, sorted the way the Add page wants them.</summary>
    public IReadOnlyList<ManifestSourceDescriptor> Sources =>
        _addons.SelectMany(a => a.Sources).OrderBy(s => s.Order).ThenBy(s => s.DisplayName).ToList();

    /// <summary>Lines worth showing the user, folder first. Not a debug log; this is the Addons page's feed.</summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    /// <summary>
    /// Read the addon folder. Cheap — a handful of small json files — and safe to call whenever the list
    /// might have changed, which is what lets an addon be added, removed or toggled without a restart.
    /// Nothing an addon declares outlives this call, so there is no stale state to reconcile.
    /// </summary>
    public void Reload(IReadOnlyCollection<string> disabledIds)
    {
        _addons.Clear();
        _diagnostics.Clear();

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!System.IO.Directory.Exists(Root)) return;
        }
        catch (Exception ex) { Note($"addons: cannot reach {Root}: {ex.Message}"); return; }

        foreach (string dir in SafeEnumerate(Root))
        {
            LoadedAddon? addon = ReadManifest(dir);
            if (addon is null) continue;
            _addons.Add(addon);

            if (disabledIds.Contains(addon.FolderName, StringComparer.OrdinalIgnoreCase))
            {
                addon.State = AddonState.Disabled;
                continue;
            }

            try
            {
                CollectSources(addon, claimed);
                addon.State = AddonState.Loaded;
            }
            catch (Exception ex)
            {
                Fail(addon, ex.Message);
            }
        }

        _addons.Sort((a, b) =>
            string.Compare(a.Manifest.Name, b.Manifest.Name, StringComparison.CurrentCultureIgnoreCase));
    }

    // ── stages ──────────────────────────────────────────────────────────────────────────────────

    private LoadedAddon? ReadManifest(string dir)
    {
        string path = Path.Combine(dir, "addon.json");
        if (!File.Exists(path)) return null; // a stray folder is not an error

        AddonManifest? m;
        try { m = JsonSerializer.Deserialize<AddonManifest>(File.ReadAllText(path), JsonOpts); }
        catch (Exception ex) { Note($"{Path.GetFileName(dir)}: unreadable addon.json ({ex.Message})"); return null; }

        if (m is null) { Note($"{Path.GetFileName(dir)}: empty addon.json"); return null; }

        var addon = new LoadedAddon { Manifest = m, Directory = dir, State = AddonState.Failed };

        if (m.Schema > SupportedSchema)
        {
            Fail(addon, $"needs a newer LuaTools (manifest schema {m.Schema}, this build reads {SupportedSchema})");
            _addons.Add(addon);
            return null;
        }

        // The folder name is the identity of record. A manifest is a file anyone can edit, so an id that
        // disagrees with the folder it sits in is how one addon would masquerade as another.
        if (string.IsNullOrWhiteSpace(m.Id) || !string.Equals(m.Id, addon.FolderName, StringComparison.OrdinalIgnoreCase))
        {
            Fail(addon, $"its addon.json claims the id \"{m.Id}\", which is not this folder's name");
            _addons.Add(addon);
            return null;
        }

        return addon;
    }

    private void CollectSources(LoadedAddon addon, HashSet<string> claimed)
    {
        foreach (var e in addon.Manifest.Sources)
        {
            if (string.IsNullOrWhiteSpace(e.Name) || string.IsNullOrWhiteSpace(e.Url))
            { Note($"{addon.FolderName}: a source is missing its name or url, skipped"); continue; }

            if (ReservedSourceNames.Contains(e.Name))
            { Note($"{addon.FolderName}: source \"{e.Name}\" is a built-in name, skipped"); continue; }

            // Unique across every addon: two rows under one name would make a download resolve by
            // whichever the lookup hit first, which is to say by folder ordering.
            if (!claimed.Add(e.Name))
            { Note($"{addon.FolderName}: source \"{e.Name}\" is already provided by another addon, skipped"); continue; }

            if (!Enum.TryParse<ManifestSourceKind>(e.Kind, ignoreCase: true, out var kind))
            { Note($"{addon.FolderName}: source \"{e.Name}\" has unknown kind \"{e.Kind}\", skipped"); continue; }

            // Everything an addon fetches goes over TLS. The app already has enough cleartext in it; a
            // source anyone can add is not the place to add more.
            if (!IsHttps(e.Url) || e.Mirrors.Any(u => !IsHttps(u)))
            { Note($"{addon.FolderName}: source \"{e.Name}\" must use https, skipped"); continue; }

            if (kind is not ManifestSourceKind.DepotKeyDatabase
                && !e.Url.Contains("{appid}", StringComparison.OrdinalIgnoreCase))
            { Note($"{addon.FolderName}: source \"{e.Name}\" url has no {{appid}} placeholder, skipped"); continue; }

            addon.Sources.Add(new ManifestSourceDescriptor
            {
                Name = e.Name,
                DisplayName = string.IsNullOrWhiteSpace(e.DisplayName) ? e.Name : e.DisplayName,
                Kind = kind,
                UrlTemplate = e.Url,
                Mirrors = e.Mirrors.ToArray(),
                Order = e.Order,
                Badge = e.Badge,
                IsFree = e.Free,
            });
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static bool IsHttps(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    private static IEnumerable<string> SafeEnumerate(string root)
    {
        try { return System.IO.Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch { return []; }
    }

    private void Fail(LoadedAddon addon, string why)
    {
        addon.State = AddonState.Failed;
        addon.Error = why;
        Note($"{addon.FolderName}: {why}");
    }

    private void Note(string line) => _diagnostics.Add(line);
}
