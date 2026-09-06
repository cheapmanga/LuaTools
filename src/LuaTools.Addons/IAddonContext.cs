using Microsoft.Extensions.DependencyInjection;

namespace LuaTools.Addons;

/// <summary>
/// Everything an addon is handed at startup. Passed to <see cref="ILuaToolsAddon.Configure"/> once,
/// while the host is still assembling its service collection — which is why registration happens here
/// and not through a live service provider: by the time one exists, it is too late to add to it.
/// </summary>
/// <remarks>
/// The context is valid only for the duration of the <c>Configure</c> call. Holding on to it and calling
/// it later registers into a collection nobody will read; the host throws in that case rather than let
/// an addon quietly do nothing.
/// </remarks>
public interface IAddonContext
{
    /// <summary>This addon's identity, as read from its manifest.</summary>
    AddonInfo Info { get; }

    /// <summary>
    /// The host's service collection, before the provider is built. An addon registers its own services
    /// here and may resolve host services by constructor injection like any built-in. It should not
    /// replace or remove a host registration: the host snapshots the collection and refuses an addon
    /// that drops someone else's service, because a page silently breaking three tabs away is the
    /// worst possible failure to debug.
    /// </summary>
    IServiceCollection Services { get; }

    /// <summary>Where this addon may keep files, created on demand. Its own folder, per addon id.</summary>
    string DataDirectory { get; }

    /// <summary>Tagged with the addon id, so a misbehaving addon is identifiable from the log alone.</summary>
    IAddonLog Log { get; }

    /// <summary>Adds a page to the nav rail, below the built-in ones.</summary>
    void AddPage(AddonPage page);

    /// <summary>
    /// Adds a manifest source to the Add page. Same call the JSON-only path uses, so a code addon and
    /// a data addon contribute a source through exactly one code path in the host.
    /// </summary>
    void AddManifestSource(ManifestSourceDescriptor source);
}
