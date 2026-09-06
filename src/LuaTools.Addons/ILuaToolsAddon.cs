namespace LuaTools.Addons;

/// <summary>
/// What a code addon implements. The host looks for exactly one public implementation with a public
/// parameterless constructor in the assembly named by the manifest.
/// </summary>
/// <remarks>
/// <para><b>Configure is not a place to do work.</b> It runs on the UI thread during startup, before the
/// window exists, and every addon's runs before the app is usable — an addon that reaches for the
/// network here delays the app for everyone. Register services, add pages, return. Anything that can
/// block belongs in the service the page resolves, on first use.</para>
///
/// <para>An addon that throws from <c>Configure</c> is disabled for the session and reported by name.
/// The rest of the app, and every other addon, carries on.</para>
/// </remarks>
public interface ILuaToolsAddon
{
    /// <summary>Registers the addon's services, pages and sources. See the remarks: do no work here.</summary>
    void Configure(IAddonContext context);
}
