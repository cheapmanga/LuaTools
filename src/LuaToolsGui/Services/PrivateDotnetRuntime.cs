using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using LuaToolsGui.Services.Downloads;
using Microsoft.Extensions.Logging;
using Velopack.Windows;

namespace LuaToolsGui.Services;

/// <summary>
/// A .NET Desktop runtime that lives in our own folder, for third-party tools that need one.
/// </summary>
/// <remarks>
/// <para>Several tools we launch (SteamAutoCrack, LuaToolsValidator) are framework-dependent
/// net10.0-windows builds: without Microsoft.WindowsDesktop.App 10 on the machine they show Windows'
/// own "you must install .NET" dialog instead of starting. The obvious fix is to run Microsoft's
/// installer, which is what we used to do - but that elevates, changes the machine, and asks the user
/// to approve a system-wide install just to open a 500 KB utility.</para>
///
/// <para>This does the same job without touching the machine. The runtime's plain ZIP binaries are
/// extracted into %AppData%\LuaToolsGui\dotnet, and a tool is started with <c>DOTNET_ROOT</c> pointing
/// there: hostfxr honours that variable, so the exe finds a runtime that was never installed. Nothing
/// is registered, nothing needs admin, and deleting the folder undoes it completely.</para>
///
/// <para>It takes two archives, not one. The windowsdesktop ZIP carries only
/// Microsoft.WindowsDesktop.App - no host, no Microsoft.NETCore.App - so the base runtime ZIP is
/// extracted first and the desktop one over it, into a single root.</para>
///
/// <para>Always x64, regardless of this machine's architecture: the tools ship x64 executables, and an
/// x64 apphost needs an x64 runtime even when Windows itself is arm64.</para>
/// </remarks>
public class PrivateDotnetRuntime(GithubProxy gh, ILogger<PrivateDotnetRuntime> log)
{
    // Local, not roaming: this is ~180 MB of extracted runtime, and a roaming profile would try to
    // synchronise it at every sign-in.
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LuaToolsGui", "dotnet");

    private static string DesktopDir => Path.Combine(Root, "shared", "Microsoft.WindowsDesktop.App");
    private static string CoreDir => Path.Combine(Root, "shared", "Microsoft.NETCore.App");
    private static string HostFxrDir => Path.Combine(Root, "host", "fxr");

    private readonly SemaphoreSlim _gate = new(1, 1);

    // Velopack marks Runtimes [Obsolete] ("no longer used by Velopack, and does not represent the
    // current supported runtimes"). It is deprecated because Velopack now bootstraps runtimes through
    // its own installer, which is not what this is: a check for a THIRD-PARTY exe, long after our setup
    // ran. The API still works on 1.2.0 and GetRuntimeByName parses the id generically, so .NET 10
    // resolves even though the static fields stop at 8.
#pragma warning disable CS0618 // deliberate: see above

    /// <summary>
    /// The runtime id these tools need, as Velopack names it. Always x64 - see the class remarks.
    /// </summary>
    private const string RuntimeId = "net10-x64-desktop";

    /// <summary>
    /// Does this machine already have the runtime installed system-wide? Local and cheap - no network.
    /// </summary>
    /// <remarks>
    /// Checked before anything is downloaded: on a machine that has .NET 10 Desktop, a tool starts
    /// exactly as it always did and the 72 MB private copy is never fetched.
    /// </remarks>
    public async Task<bool> MachineHasRuntimeAsync()
    {
        try
        {
            var runtime = Runtimes.GetRuntimeByName(RuntimeId);
            return runtime is not null && await runtime.CheckIsInstalled();
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Checking for an installed .NET runtime failed");
            return false;
        }
    }
#pragma warning restore CS0618

    /// <summary>Is a usable private runtime already extracted? Local and cheap - no network.</summary>
    /// <remarks>
    /// All three parts are checked because a half-extracted root is worse than none: the exe would
    /// start against it and fail deeper, past the point where we could still fall back.
    /// </remarks>
    public bool IsReady =>
        Has(HostFxrDir, "hostfxr.dll")
        && Has(CoreDir, "System.Private.CoreLib.dll")
        && Has(DesktopDir, "PresentationFramework.dll");

    /// <summary>Does some version folder under <paramref name="dir"/> actually contain that file?</summary>
    private static bool Has(string dir, string file)
    {
        try
        {
            return Directory.Exists(dir)
                && Directory.EnumerateDirectories(dir).Any(v => File.Exists(Path.Combine(v, file)));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Extract the private runtime if it isn't there yet. True when a tool can be launched against it.
    /// </summary>
    /// <remarks>
    /// The two archives are ~72 MB together and are fetched once. A failure leaves whatever was already
    /// there untouched and returns false, so the caller can fall back to Microsoft's installer.
    /// </remarks>
    public async Task<bool> EnsureAsync(IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        if (IsReady) return true;

        await _gate.WaitAsync(ct);
        try
        {
            if (IsReady) return true; // won the race elsewhere

            Directory.CreateDirectory(Root);
            var urls = new[] { AppConfig.DotnetRuntimeZipUrl, AppConfig.DotnetDesktopRuntimeZipUrl };
            for (int i = 0; i < urls.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                string url = urls[i];
                string zip = Path.Combine(Root, "runtime.zip");

                // Each archive owns half the bar, so it fills once across the pair rather than
                // filling, resetting and filling again.
                int half = i;
                var sink = progress is null ? null : new ProgressRelay<double?>(f =>
                    progress.Report(new DownloadProgress((long)((half + (f ?? 0)) * 500), 1000)));

                await gh.DownloadAsync(url, zip, sink, ct);

                // Both archives extract into the same root: the base runtime brings host/ and
                // Microsoft.NETCore.App, the desktop one adds Microsoft.WindowsDesktop.App beside it.
                ZipFile.ExtractToDirectory(zip, Root, overwriteFiles: true);
                try { File.Delete(zip); } catch { /* leftover archive is harmless */ }
            }

            if (IsReady) return true;

            log.LogDebug("The private runtime extracted but looks incomplete");
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Includes HttpClient's timeout on a 72 MB download, which arrives as a
            // TaskCanceledException and must not be reported as the user cancelling.
            log.LogDebug(ex, "Preparing the private .NET runtime failed");
            return false;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Point a child process at the private runtime. No-op when there isn't one, so a tool launched on a
    /// machine that has the runtime installed keeps starting exactly as it did before.
    /// </summary>
    /// <remarks>
    /// Environment variables need <c>UseShellExecute = false</c>, which the caller must set. Both names
    /// are written: an x64 host reads DOTNET_ROOT_X64 first when it exists, DOTNET_ROOT otherwise.
    /// </remarks>
    public void Apply(ProcessStartInfo psi)
    {
        if (!IsReady) return;

        // Environment variables are only passed when the shell is out of the picture. Flipped here
        // rather than at every call site, so a launch that needs no private runtime keeps the shell
        // path - which is also the only path that can raise a UAC prompt, should a tool ever want one.
        psi.UseShellExecute = false;
        psi.Environment["DOTNET_ROOT"] = Root;
        psi.Environment["DOTNET_ROOT_X64"] = Root;
    }
}
