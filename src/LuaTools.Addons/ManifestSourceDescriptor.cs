namespace LuaTools.Addons;

/// <summary>
/// The shapes of manifest source LuaTools already knows how to consume. An addon picks one; it does
/// not get to invent a fetch routine, which is what keeps a data-only addon (a few lines of JSON,
/// nothing executable) able to contribute a working source.
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
    /// the host synthesises the lua (what ManifestHub and its forks publish). One download serves
    /// every lookup for the session, so coverage is broad and per-game cost is nil — but a depot whose
    /// key nobody has dumped is simply absent, and games are silently partial rather than missing.
    /// </summary>
    DepotKeyDatabase,
}

/// <summary>
/// A manifest source contributed by an addon, as data. This is the payload that makes the addon system
/// worth having: free sources rot — the upstream ManifestHub repo has been frozen since January 2026 and
/// the app had to ship a new build just to point at fresher community forks. A descriptor moves that
/// from "cut a release" to "edit a line of JSON".
/// </summary>
public sealed class ManifestSourceDescriptor
{
    /// <summary>
    /// Key used in settings, ordering and telemetry. Lowercase, no spaces. Must not collide with a
    /// built-in ("manifesthub", "sushi") — the host rejects the addon rather than silently shadowing one.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>What the Add page's source row shows. Localised by the addon, not by the host.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Which of the shapes the host knows how to fetch this source is.</summary>
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

    /// <summary>
    /// Sort weight among the Add page's source rows; lower sorts first. Built-in free sources sit at
    /// 100, lua.tools at 500. An addon cannot force itself above a source the user chose to prefer:
    /// the user's own ordering, once set, always wins over this.
    /// </summary>
    public int Order { get; init; } = 200;

    /// <summary>
    /// Short badge on the source row ("No limit", "Free"). Null shows no badge. Purely cosmetic — it
    /// grants nothing, and the host never reads it back as a capability.
    /// </summary>
    public string? Badge { get; init; }

    /// <summary>
    /// True when fetching costs the user nothing and needs no account, which is the only claim the
    /// Add page repeats to the user. Wrong here is a lie to the user, so the host verifies what it
    /// can: a descriptor that requires credentials is rejected outright rather than trusted.
    /// </summary>
    public bool IsFree { get; init; } = true;
}
