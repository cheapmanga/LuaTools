using System.Reflection;
using System.Runtime.Loader;

namespace LuaToolsGui.Services.Addons;

/// <summary>
/// The load context one addon assembly and its private dependencies live in.
/// </summary>
/// <remarks>
/// <para>The whole reason this class exists is type identity. If an addon ships its own copy of
/// <c>LuaTools.Addons.dll</c> — and it will, because that is what a plain <c>dotnet publish</c>
/// produces — loading it here would create a SECOND <c>ILuaToolsAddon</c> type that has nothing to do
/// with the host's. The cast then fails with the maddening "unable to cast object of type
/// ILuaToolsAddon to type ILuaToolsAddon". So <see cref="Load"/> hands any assembly the default
/// context already has back to the default context, and only genuinely private dependencies get
/// resolved out of the addon's own folder.</para>
///
/// <para>Collectible is deliberately off. Unloading an ALC requires that nothing anywhere still
/// references a type from it, and an addon that has registered services and built a WPF control has
/// left references all over the visual tree and the DI container. A half-unloaded addon is far worse
/// than one that lives until the process exits, so disabling an addon takes effect on restart.</para>
/// </remarks>
public sealed class AddonLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public AddonLoadContext(string addonAssemblyPath, string name)
        : base(name, isCollectible: false)
        => _resolver = new AssemblyDependencyResolver(addonAssemblyPath);

    protected override Assembly? Load(AssemblyName name)
    {
        // Anything the host already has — the contract, WPF, the BCL, Microsoft.Extensions.* — must
        // resolve to the host's instance. Returning null defers to the default context, which is what
        // keeps a type shared across the boundary actually shared.
        foreach (var loaded in Default.Assemblies)
            if (string.Equals(loaded.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))
                return null;

        string? path = _resolver.ResolveAssemblyToPath(name);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
