namespace LuaTools.Addons;

/// <summary>
/// Who an addon is, as declared in its <c>addon.json</c>. The host fills this in and hands it to the
/// addon; an addon never invents its own identity, so what the Addons page shows and what the addon
/// believes about itself can never drift apart.
/// </summary>
/// <param name="Id">
/// Stable, lowercase, dot-separated ("cheapmanga.achievements"). This is the folder name on disk and
/// the key everything else joins on, so it is the one field an addon may never change between
/// versions without becoming a different addon.
/// </param>
/// <param name="Name">Display name, shown in the Addons list and (for page addons) the nav rail.</param>
/// <param name="Version">Semver of the addon itself, unrelated to the host version.</param>
/// <param name="Author">Freeform; shown as-is, never used to grant trust.</param>
/// <param name="Description">One line. The Addons list has room for one line.</param>
public sealed record AddonInfo(
    string Id,
    string Name,
    string Version,
    string? Author = null,
    string? Description = null);
