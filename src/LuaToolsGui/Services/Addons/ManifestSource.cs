namespace LuaToolsGui.Services.Addons;

/// <summary>
/// The shapes of manifest source the app knows how to fetch. An addon picks one; it cannot supply a
/// fetch routine of its own, which is what lets an addon be nothing but a few lines of JSON.
/// </summary>
public enum ManifestSourceKind
{
    /// <summary>
    /// One <c>&lt;appid&gt;.zip</c> per game holding the lua AND its <c>.manifest</c> files. The shape
    /// SteamTools' public repos use; covers pinned builds, because the manifests travel with the lua.
    /// </summary>
    ManifestZip,

    /// <summary>One <c>&lt;appid&gt;.lua</c> per game, no manifests: entitlements and depot keys only.</summary>
    LuaFile,

    /// <summary>
    /// A single flat JSON map of <c>depot id → decryption key</c> for every game at once, from which
    /// the lua is synthesised locally (what ManifestHub and its forks publish). One download serves
    /// every lookup for the session, so coverage is broad and per-game cost is nil — but a depot whose
    /// key nobody has dumped is simply absent, and games are silently partial rather than missing.
    /// </summary>
    DepotKeyDatabase,
}

/// <summary>
/// A manifest source contributed by an addon. This is the payload that makes addons worth having: free
/// sources rot — the upstream ManifestHub repo has been frozen since January 2026 and the app had to
/// ship a new build just to point at fresher community forks. A descriptor moves that from "cut a
/// release" to "edit a line of JSON".
/// </summary>
public sealed class ManifestSourceDescriptor
{
    /// <summary>
    /// Key used in ordering and the download route. Lowercase, no spaces. Must not collide with a
    /// built-in — the registry refuses the source rather than silently shadowing one.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>What the Add page's source row shows.</summary>
    public required string DisplayName { get; init; }

    public required ManifestSourceKind Kind { get; init; }

    /// <summary>
    /// Where to fetch. For <see cref="ManifestSourceKind.ManifestZip"/> and
    /// <see cref="ManifestSourceKind.LuaFile"/> this contains the literal token <c>{appid}</c>, replaced
    /// per lookup. For <see cref="ManifestSourceKind.DepotKeyDatabase"/> it is a plain URL to the JSON.
    /// </summary>
    public required string UrlTemplate { get; init; }

    /// <summary>
    /// Extra URLs tried in order when <see cref="UrlTemplate"/> is unreachable or returns garbage. The
    /// lesson from the frozen upstream: a source with no fallback is a source with an expiry date.
    /// </summary>
    public IReadOnlyList<string> Mirrors { get; init; } = [];

    /// <summary>Sort weight among addon sources; lower first. They always sit below the built-in ones.</summary>
    public int Order { get; init; } = 200;

    /// <summary>Short badge on the source row. Cosmetic; never read back as a capability.</summary>
    public string? Badge { get; init; }

    /// <summary>True when fetching costs nothing and needs no account — the only claim the row repeats.</summary>
    public bool IsFree { get; init; } = true;
}
