namespace LuaTools.Addons;

/// <summary>
/// A page an addon adds to the nav rail.
/// </summary>
/// <remarks>
/// The page is named by TYPE, not handed over as an instance, because that is how every built-in page
/// already works: the host's nav resolves a page from the DI container the first time it is opened. An
/// addon therefore registers its view in <see cref="IAddonContext.Services"/> and names it here, and
/// gets constructor injection, lazy creation and single-instance caching for free — the same deal, and
/// none of it reimplemented for addons.
/// </remarks>
public sealed class AddonPage
{
    /// <summary>Label in the nav rail. Already localised by the addon; the host does not translate it.</summary>
    public required string Title { get; init; }

    /// <summary>
    /// The view. Must derive from <c>System.Windows.Controls.Control</c> and be registered in
    /// <see cref="IAddonContext.Services"/> by the same <c>Configure</c> call, or the host rejects the
    /// page at registration — a nav entry that throws when clicked is a worse outcome than one that
    /// never appears.
    /// </summary>
    public required Type ViewType { get; init; }

    /// <summary>
    /// Fluent System Icons name, as WPF-UI spells it ("Trophy24", "PuzzlePiece24"). An unknown name
    /// falls back to a generic icon rather than throwing — an addon should not be able to take the
    /// window down over a typo in an icon.
    /// </summary>
    public string Icon { get; init; } = "PuzzlePiece24";

    /// <summary>
    /// Sort weight in the nav rail; lower is higher up. Addon pages are placed below every built-in
    /// page regardless, so this only orders addons against each other. An addon cannot promote itself
    /// above Home.
    /// </summary>
    public int Order { get; init; } = 1000;
}
