using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Controls;
using LuaTools.Addons;
using Microsoft.Extensions.DependencyInjection;

namespace LuaToolsGui.Services.Addons;

/// <summary>Why an addon is, or isn't, contributing anything this session.</summary>
public enum AddonState
{
    /// <summary>Manifest read, assembly loaded, <c>Configure</c> returned.</summary>
    Loaded,

    /// <summary>No assembly named: its sources are live, nothing executed.</summary>
    DataOnly,

    /// <summary>Present on disk, switched off by the user. Takes effect on restart.</summary>
    Disabled,

    /// <summary>Rejected or blew up. <see cref="LoadedAddon.Error"/> says why, in words for the user.</summary>
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
    public List<AddonPage> Pages { get; } = [];

    /// <summary>True when the addon names an assembly - the one thing that makes a restart unavoidable.</summary>
    public bool HasAssembly => !string.IsNullOrWhiteSpace(Manifest.Assembly);

    /// <summary>
    /// The folder name, which is the addon's identity of record everywhere it matters: diagnostics, the
    /// enabled/disabled list, the Addons row. The manifest's own id is only ever CHECKED against this,
    /// never trusted over it - a message about a manifest that lies about its id must not be filed under
    /// the id it lied about, or it names an addon that exists nowhere on disk and the user cannot find
    /// the folder to fix.
    /// </summary>
    public string FolderName => Path.GetFileName(Directory.TrimEnd(Path.DirectorySeparatorChar));
}

/// <summary>
/// Finds addons, decides which may run, and lets them register into the host's service collection.
/// </summary>
/// <remarks>
/// <para>Runs once, from the App constructor, before the service provider is built — that is the only
/// window in which anything can still be added to the collection.</para>
///
/// <para>The rule this class is built around: <b>a broken addon must never be able to stop the app from
/// starting.</b> Every stage is wrapped, every failure is recorded against the addon by name and the
/// loop moves on. An addon that throws is disabled for the session; the user finds out on the Addons
/// page instead of by the app not opening.</para>
/// </remarks>
public sealed class AddonRegistry
{
    /// <summary>Highest <c>addon.json</c> schema this build understands.</summary>
    private const int SupportedSchema = 1;

    /// <summary>Source names the host owns. An addon claiming one is rejected, never silently ignored.</summary>
    private static readonly HashSet<string> ReservedSourceNames =
        new(StringComparer.OrdinalIgnoreCase) { "manifesthub", "sushi", "luatools", "hubcap", "sadie" };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaToolsGui", "addons");

    private readonly List<LoadedAddon> _addons = [];

    /// <summary>
    /// Source names already taken this session, across every addon. Two addons contributing the same
    /// name would each get a row, and a download would resolve to whichever the lookup hit first - so
    /// the second one is refused and said so, rather than producing a source whose behaviour depends on
    /// folder ordering.
    /// </summary>
    private readonly HashSet<string> _claimedSourceNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every addon folder found, whatever became of it. Ordered by display name.</summary>
    public IReadOnlyList<LoadedAddon> Addons => _addons;

    /// <summary>Pages contributed this session, already sorted for the nav rail.</summary>
    public IReadOnlyList<AddonPage> Pages =>
        _addons.SelectMany(a => a.Pages).OrderBy(p => p.Order).ThenBy(p => p.Title).ToList();

    /// <summary>Sources contributed this session, sorted the way the Add page wants them.</summary>
    public IReadOnlyList<ManifestSourceDescriptor> Sources =>
        _addons.SelectMany(a => a.Sources).OrderBy(s => s.Order).ThenBy(s => s.DisplayName).ToList();

    /// <summary>Lines worth showing the user, addon id first. Not a debug log; this is the Addons page's feed.</summary>
    public IReadOnlyList<string> Diagnostics => [.._startupDiagnostics, .._diagnostics];

    /// <summary>
    /// What startup had to say. Kept apart from <see cref="_diagnostics"/> and never cleared, because a
    /// refresh rebuilds only the data addons: clearing everything would erase the reason a CODE addon
    /// failed to load, and the page auto-refreshes when opened - so the user would never get to read it.
    /// </summary>
    private readonly List<string> _startupDiagnostics = [];

    private readonly List<string> _diagnostics = [];

    /// <summary>
    /// Reads every addon folder and lets the enabled ones register. Safe to call exactly once, from the
    /// App constructor. <paramref name="disabledIds"/> comes from settings.
    /// </summary>
    public void LoadAll(IServiceCollection services, IReadOnlyCollection<string> disabledIds)
    {
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
                CollectSources(addon);
                if (string.IsNullOrWhiteSpace(addon.Manifest.Assembly))
                {
                    addon.State = AddonState.DataOnly;
                }
                else
                {
                    RunAddonAssembly(addon, services);
                    addon.State = AddonState.Loaded;
                }
            }
            catch (Exception ex)
            {
                Fail(addon, ex.Message);
            }
        }

        Sort();

        // From here on these lines belong to startup and survive every later refresh.
        _startupDiagnostics.AddRange(_diagnostics);
        _diagnostics.Clear();
    }

    /// <summary>
    /// Re-read the addon folder, applying everything that does not need the app restarted. Returns true
    /// when something changed that this cannot apply, so the caller can ask for a restart only when a
    /// restart is genuinely the answer.
    /// </summary>
    /// <remarks>
    /// <para>The restart requirement is not a property of addons, it is a property of CODE addons. One
    /// that names an assembly registers services into a collection that stops existing the moment the
    /// provider is built, and its page has to be resolvable by the nav that was populated at startup —
    /// neither can be redone live, and its types can never be unloaded (see
    /// <see cref="AddonLoadContext"/>). A data addon does none of that: its sources are a list this
    /// registry holds and the Add page re-reads on every fetch, so adding, removing, enabling and
    /// disabling one can all take effect immediately.</para>
    ///
    /// <para>Already-running code addons are left strictly alone here — not re-read, not re-configured,
    /// not dropped. Their <c>Configure</c> ran once against a service collection that is gone; running
    /// it again would register into nothing, and dropping them would leave their page in the nav rail
    /// pointing at a service that no longer exists.</para>
    /// </remarks>
    public bool RefreshDataAddons(IReadOnlyCollection<string> disabledIds)
    {
        var code = _addons.Where(a => a.HasAssembly).ToList();

        // Names are re-claimed from scratch each pass, seeded with the code addons that keep theirs, so
        // a source whose addon was deleted stops holding its name hostage for the rest of the session.
        _claimedSourceNames.Clear();
        foreach (var a in code)
            foreach (var src in a.Sources) _claimedSourceNames.Add(src.Name);

        _addons.RemoveAll(a => !a.HasAssembly);
        _diagnostics.Clear();

        bool needsRestart = false;

        try
        {
            if (!System.IO.Directory.Exists(Root)) { Sort(); return false; }
        }
        catch (Exception ex) { Note($"addons: cannot reach {Root}: {ex.Message}"); Sort(); return false; }

        foreach (string dir in SafeEnumerate(Root))
        {
            // A code addon already running is reported as it was, and skipped entirely.
            var existing = code.FirstOrDefault(a =>
                string.Equals(a.Directory, dir, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                bool nowDisabled = disabledIds.Contains(existing.FolderName, StringComparer.OrdinalIgnoreCase);
                if (nowDisabled != (existing.State == AddonState.Disabled)) needsRestart = true;
                continue;
            }

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
                if (!addon.HasAssembly)
                {
                    CollectSources(addon);
                    addon.State = AddonState.DataOnly;
                }
                else
                {
                    // New on disk since startup, and it carries code: nothing here can load it safely.
                    addon.State = AddonState.Failed;
                    addon.Error = Resources.Strings.Addons_State_NeedsRestart;
                    needsRestart = true;
                }
            }
            catch (Exception ex)
            {
                Fail(addon, ex.Message);
            }
        }

        Sort();
        return needsRestart;
    }

    private void Sort() =>
        _addons.Sort((a, b) => string.Compare(a.Manifest.Name, b.Manifest.Name, StringComparison.CurrentCultureIgnoreCase));

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
        string folder = Path.GetFileName(dir);
        if (string.IsNullOrWhiteSpace(m.Id) || !string.Equals(m.Id, folder, StringComparison.OrdinalIgnoreCase))
        {
            Fail(addon, $"its addon.json claims the id \"{m.Id}\", which is not this folder's name");
            _addons.Add(addon);
            return null;
        }

        if (!IsHostNewEnough(m.MinHostVersion, out string? need))
        {
            Fail(addon, $"needs LuaTools {need} or newer");
            _addons.Add(addon);
            return null;
        }

        return addon;
    }

    private void CollectSources(LoadedAddon addon)
    {
        foreach (var e in addon.Manifest.Sources)
        {
            if (string.IsNullOrWhiteSpace(e.Name) || string.IsNullOrWhiteSpace(e.Url))
            { Note($"{addon.FolderName}: a source is missing its name or url, skipped"); continue; }

            if (ReservedSourceNames.Contains(e.Name))
            { Note($"{addon.FolderName}: source \"{e.Name}\" is a built-in name, skipped"); continue; }

            if (!_claimedSourceNames.Add(e.Name))
            { Note($"{addon.FolderName}: source \"{e.Name}\" is already provided by another addon, skipped"); continue; }

            if (!Enum.TryParse<ManifestSourceKind>(e.Kind, ignoreCase: true, out var kind))
            { Note($"{addon.FolderName}: source \"{e.Name}\" has unknown kind \"{e.Kind}\", skipped"); continue; }

            // Everything a community addon fetches goes over TLS. The app already has enough cleartext
            // in it; a source anyone can add is not the place to add more.
            if (!IsHttps(e.Url) || e.Mirrors.Any(u => !IsHttps(u)))
            { Note($"{addon.FolderName}: source \"{e.Name}\" must use https, skipped"); continue; }

            if (kind is not ManifestSourceKind.DepotKeyDatabase && !e.Url.Contains("{appid}", StringComparison.OrdinalIgnoreCase))
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

    private void RunAddonAssembly(LoadedAddon addon, IServiceCollection services)
    {
        string file = addon.Manifest.Assembly!;
        if (file.Contains('/') || file.Contains('\\') || Path.IsPathRooted(file))
            throw new InvalidOperationException("assembly must be a file name inside the addon folder");

        string path = Path.Combine(addon.Directory, file);
        if (!File.Exists(path)) throw new FileNotFoundException($"{file} is missing");

        if (string.IsNullOrWhiteSpace(addon.Manifest.AssemblySha256))
            throw new InvalidOperationException("assemblySha256 is required when an assembly is declared");

        string actual = Sha256(path);
        if (!string.Equals(actual, addon.Manifest.AssemblySha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{file} does not match its declared sha256");

        var alc = new AddonLoadContext(path, addon.Manifest.Id);
        Assembly asm = alc.LoadFromAssemblyPath(path);

        // GetTypes throws wholesale on a half-resolvable assembly; the loader exception carries the
        // types that DID load, which is usually enough to find the entry point and always enough to
        // report something better than "an error occurred".
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }

        var entry = types.FirstOrDefault(t =>
            typeof(ILuaToolsAddon).IsAssignableFrom(t) && t is { IsAbstract: false, IsPublic: true })
            ?? throw new InvalidOperationException($"{file} has no public {nameof(ILuaToolsAddon)} implementation");

        if (Activator.CreateInstance(entry) is not ILuaToolsAddon instance)
            throw new InvalidOperationException($"{entry.Name} could not be constructed");

        var ctx = new AddonContext(addon, services, this);
        instance.Configure(ctx);
        ctx.Seal();
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Compares against this build's version. Unparseable on either side means "allow".</summary>
    private static bool IsHostNewEnough(string? min, out string? needed)
    {
        needed = min;
        if (string.IsNullOrWhiteSpace(min)) return true;
        if (!Version.TryParse(min.Split('+', '-')[0], out var want)) return true;

        string? host = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
        if (!Version.TryParse(host?.Split('+', '-')[0], out var have)) return true;
        return have >= want;
    }

    private static bool IsHttps(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    private static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

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

    internal void Note(string line) => _diagnostics.Add(line);

    // ── the object handed to an addon ───────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="IAddonContext"/> for exactly one <c>Configure</c> call. Sealed afterwards, so an addon
    /// that stashes the context and registers later gets a clear throw instead of a silent no-op into a
    /// service collection the host has already finished with.
    /// </summary>
    private sealed class AddonContext(LoadedAddon addon, IServiceCollection services, AddonRegistry registry)
        : IAddonContext, IAddonLog
    {
        private bool _sealed;

        public AddonInfo Info { get; } = new(
            addon.Manifest.Id, addon.Manifest.Name, addon.Manifest.Version,
            addon.Manifest.Author, addon.Manifest.Description);

        public IServiceCollection Services => Live(services);
        public IAddonLog Log => this;

        public string DataDirectory
        {
            get
            {
                string p = Path.Combine(Root, addon.FolderName, "data");
                System.IO.Directory.CreateDirectory(p);
                return p;
            }
        }

        public void AddPage(AddonPage page)
        {
            Live(page);
            ArgumentException.ThrowIfNullOrWhiteSpace(page.Title);

            if (!typeof(Control).IsAssignableFrom(page.ViewType))
                throw new InvalidOperationException($"page \"{page.Title}\": {page.ViewType.Name} is not a WPF Control");

            // Checked now, while Configure is still running and the addon can be blamed by name. Left to
            // navigation time it would surface as an unhandled exception on the UI thread, in a stack
            // that says nothing about which addon caused it.
            if (services.All(d => d.ServiceType != page.ViewType))
                throw new InvalidOperationException(
                    $"page \"{page.Title}\": {page.ViewType.Name} must be registered in Services by this addon");

            addon.Pages.Add(page);
        }

        public void AddManifestSource(ManifestSourceDescriptor source)
        {
            Live(source);
            if (ReservedSourceNames.Contains(source.Name))
                throw new InvalidOperationException($"source name \"{source.Name}\" is reserved");
            if (!registry._claimedSourceNames.Add(source.Name))
                throw new InvalidOperationException($"source name \"{source.Name}\" is already taken by another addon");
            addon.Sources.Add(source);
        }

        void IAddonLog.Info(string message) => registry.Note($"{addon.FolderName}: {message}");
        void IAddonLog.Warn(string message) => registry.Note($"{addon.FolderName}: {message}");
        void IAddonLog.Error(string message, Exception? ex) =>
            registry.Note($"{addon.FolderName}: {message}{(ex is null ? "" : $" ({ex.Message})")}");

        internal void Seal() => _sealed = true;

        private T Live<T>(T value)
        {
            ObjectDisposedException.ThrowIf(_sealed, nameof(IAddonContext));
            return value;
        }

    }
}
