using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using LuaToolsGui.Models;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui.Services;

/// <summary>
/// Self-update for the fork's STANDALONE zip build (the one shipped outside Velopack). The official
/// <see cref="UpdateService"/> only fires for a Velopack install, which the standalone is not, so this
/// fills that gap.
///
/// <para>The fork ships as ONE moving release (<see cref="AppConfig.ForkReleaseTag"/>, asset
/// <see cref="AppConfig.ForkAssetName"/>): the tag is re-pointed at each build's commit and the asset
/// replaced in place, so the tag NAME never changes between builds. Detection therefore compares the
/// running build's stamped commit (<see cref="BuildCommit"/>, from the csproj's SourceRevisionId) to the
/// commit the tag currently points at — they differ exactly when a newer build has been published. This
/// needs nothing persisted locally: the freshly applied build carries the new commit and stops matching
/// "newer".</para>
///
/// <para>Applying replaces a running exe, which Windows will not let a process do to itself: the new zip
/// is downloaded and digest-verified BEFORE anything installed is touched, extracted to a temp folder,
/// then a tiny .cmd takes over — it waits for THIS process to exit, copies the new files over the install
/// folder, and relaunches.</para>
/// </summary>
public sealed class StandaloneUpdateService(GithubProxy gh, ILogger<StandaloneUpdateService> log)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>An available update: the asset to fetch and the commit it belongs to.</summary>
    public sealed record Available(GithubAsset Asset, string Commit);

    /// <summary>The short commit this build was published from, or null for a dev/unpacked build.</summary>
    public static string? BuildCommit
    {
        get
        {
            string? info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            // "1.1.3+<sha>" when SourceRevisionId was stamped; no '+' on a plain dev build.
            int plus = info?.IndexOf('+') ?? -1;
            return plus >= 0 ? info![(plus + 1)..] : null;
        }
    }

    /// <summary>The folder the running exe lives in — the update target. Null if it can't be determined.</summary>
    private static string? InstallDir
    {
        get
        {
            // Environment.ProcessPath is the real on-disk exe even for a single-file build (AppContext
            // .BaseDirectory would be the self-extract temp instead).
            string? exe = Environment.ProcessPath;
            return string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe);
        }
    }

    /// <summary>A Velopack install (the official channel) sits next to its Update.exe stub. This updater
    /// is only for the plain-zip standalone, so it must stay dormant there even if such a build ever ends
    /// up with a stamped commit.</summary>
    private static bool IsVelopackInstall
    {
        get
        {
            string? dir = InstallDir;
            if (dir is null) return false;
            try { return File.Exists(Path.Combine(dir, "..", "Update.exe")); }
            catch { return false; }
        }
    }

    /// <summary>Is a newer build published? Null when up to date, a dev build, or the check fails.</summary>
    public async Task<Available?> CheckAsync(CancellationToken ct = default)
    {
        string? local = BuildCommit;
        if (string.IsNullOrEmpty(local) || InstallDir is null) return null; // dev / unpacked: nothing to do
        if (IsVelopackInstall) return null;                                 // official channel owns updates

        try
        {
            // 1) Which commit does the release tag point at now?
            string? remote = await ResolveTagCommitAsync(ct);
            // remote begins with our short sha ⇒ same build ⇒ up to date.
            if (string.IsNullOrEmpty(remote) || remote.StartsWith(local, StringComparison.OrdinalIgnoreCase))
                return null;

            // 2) Newer commit: take the release's zip asset.
            string relUrl = $"https://api.github.com/repos/{AppConfig.ForkRepo}/releases/tags/{AppConfig.ForkReleaseTag}";
            using var relRes = await gh.SendAsync(relUrl, ct);
            if (relRes is null || !relRes.IsSuccessStatusCode) return null;

            var release = JsonSerializer.Deserialize<GithubRelease>(await relRes.Content.ReadAsStringAsync(ct), JsonOpts);
            var asset = release?.Assets.FirstOrDefault(
                a => string.Equals(a.Name, AppConfig.ForkAssetName, StringComparison.OrdinalIgnoreCase));
            if (asset is null || string.IsNullOrEmpty(asset.DownloadUrl)) return null;

            log.LogDebug("Standalone update available: {Local} → {Remote}", local, remote);
            return new Available(asset, remote);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Standalone update check failed");
            return null;
        }
    }

    /// <summary>
    /// The commit the release tag currently points at, or null if it can't be resolved. Uses the SINGULAR
    /// git/ref endpoint (exact match, always one object — the plural one prefix-matches and can return an
    /// array), and dereferences an annotated tag to its commit so the compare is always commit-vs-commit.
    /// </summary>
    private async Task<string?> ResolveTagCommitAsync(CancellationToken ct)
    {
        string refUrl = $"https://api.github.com/repos/{AppConfig.ForkRepo}/git/ref/tags/{AppConfig.ForkReleaseTag}";
        using var refRes = await gh.SendAsync(refUrl, ct);
        if (refRes is null || !refRes.IsSuccessStatusCode) return null;

        using var refDoc = JsonDocument.Parse(await refRes.Content.ReadAsStringAsync(ct));
        if (refDoc.RootElement.ValueKind != JsonValueKind.Object
            || !refDoc.RootElement.TryGetProperty("object", out var obj)
            || !obj.TryGetProperty("sha", out var shaEl))
            return null;

        string? sha = shaEl.GetString();
        string? type = obj.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type != "tag" || string.IsNullOrEmpty(sha))
            return sha; // lightweight tag (our PATCH-refs flow): object IS the commit

        // Annotated tag: object.sha is the tag object; follow it to the commit it wraps.
        string tagUrl = $"https://api.github.com/repos/{AppConfig.ForkRepo}/git/tags/{sha}";
        using var tagRes = await gh.SendAsync(tagUrl, ct);
        if (tagRes is null || !tagRes.IsSuccessStatusCode) return null;
        using var tagDoc = JsonDocument.Parse(await tagRes.Content.ReadAsStringAsync(ct));
        return tagDoc.RootElement.TryGetProperty("object", out var tobj)
               && tobj.TryGetProperty("sha", out var csha) ? csha.GetString() : null;
    }

    /// <summary>
    /// Download + verify + stage the update, then launch the swap helper and ask the app to exit. Returns
    /// false (and leaves everything as-is) if anything fails before the handoff; on success it does not
    /// return in any useful sense — <paramref name="requestExit"/> ends the process.
    /// </summary>
    public async Task<bool> DownloadAndApplyAsync(
        Available update, IProgress<double?>? progress, Action requestExit, CancellationToken ct = default)
    {
        string? installDir = InstallDir;
        if (installDir is null) return false;

        // Replacing-and-running the whole app is the one place where a missing digest must NOT pass: the
        // zip is fetched through third-party download mirrors, so with nothing to verify against a bad
        // mirror could swap in an arbitrary app. Fail closed. (GitHub does supply sha256 digests here.)
        if (string.IsNullOrEmpty(update.Asset.Digest))
        {
            log.LogDebug("Update asset has no digest; refusing to self-update");
            return false;
        }

        string work = Path.Combine(Path.GetTempPath(), "LuaToolsUpdate_" + Guid.NewGuid().ToString("N"));
        string zipPath = work + ".zip";
        try
        {
            Directory.CreateDirectory(work);

            // Download and verify BEFORE touching anything installed. A tampered/half-written zip must
            // never reach the swap step. ConfigureAwait(false) keeps the CPU-bound hash + extract that
            // follow off the UI thread (this is invoked from a toast action).
            await gh.DownloadAsync(update.Asset.DownloadUrl, zipPath, progress, ct).ConfigureAwait(false);
            if (!AssetHash.Matches(zipPath, update.Asset.Digest))
            {
                log.LogDebug("Update asset digest mismatch; aborting");
                Cleanup(zipPath, work);
                return false;
            }

            ZipFile.ExtractToDirectory(zipPath, work, overwriteFiles: true);

            // The zip holds a top-level LuaTools/ folder (LuaTools.exe + LuaTools.SamHost.exe); fall back to
            // the extraction root if the layout is ever flattened.
            string src = Path.Combine(work, "LuaTools");
            if (!File.Exists(Path.Combine(src, "LuaTools.exe")) && File.Exists(Path.Combine(work, "LuaTools.exe")))
                src = work;
            if (!File.Exists(Path.Combine(src, "LuaTools.exe")))
            {
                log.LogDebug("Update zip has no LuaTools.exe; aborting");
                Cleanup(zipPath, work);
                return false;
            }

            LaunchSwap(WriteSwapScript(src, installDir, zipPath, work));
            requestExit(); // graceful process exit; the helper is waiting for us to actually be gone
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Applying standalone update failed");
            Cleanup(zipPath, work);
            return false;
        }
    }

    private static void Cleanup(string zipPath, string work)
    {
        try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { /* best effort */ }
        try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Write the swap helper. It waits for this process to exit, stops the helper exe that may hold a file
    /// lock, then swaps the files RECOVERABLY: the old exes are renamed aside (an atomic move, so they're
    /// never simply gone), the new files are copied in, and if the copy fails or the new exe is missing the
    /// old ones are moved back — the user is never left without a working install. On success the backups
    /// are removed. Finally it relaunches, cleans up staging, and deletes itself. Paths are quoted for spaces.
    /// </summary>
    private static string WriteSwapScript(string src, string dst, string zipPath, string work)
    {
        int pid = Environment.ProcessId;
        string cmdPath = Path.Combine(Path.GetTempPath(), "LuaToolsUpdate_" + Guid.NewGuid().ToString("N") + ".cmd");
        string script =
            "@echo off\r\n" +
            "setlocal\r\n" +
            ":waitloop\r\n" +
            $"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul\r\n" +
            "if not errorlevel 1 (\r\n" +
            "  >nul ping -n 2 127.0.0.1\r\n" +
            "  goto waitloop\r\n" +
            ")\r\n" +
            "taskkill /F /IM LuaTools.SamHost.exe >nul 2>&1\r\n" +
            // Rename the current exes aside first (renaming is atomic and never leaves a truncated file).
            $"move /Y \"{dst}\\LuaTools.exe\" \"{dst}\\LuaTools.exe.old\" >nul 2>&1\r\n" +
            $"move /Y \"{dst}\\LuaTools.SamHost.exe\" \"{dst}\\LuaTools.SamHost.exe.old\" >nul 2>&1\r\n" +
            $"robocopy \"{src}\" \"{dst}\" /E /R:10 /W:1 >nul\r\n" +
            // robocopy exit codes < 8 are success; >= 8 is a real failure. Also guard against a missing exe.
            "if errorlevel 8 goto restore\r\n" +
            $"if not exist \"{dst}\\LuaTools.exe\" goto restore\r\n" +
            $"del /F /Q \"{dst}\\LuaTools.exe.old\" >nul 2>&1\r\n" +
            $"del /F /Q \"{dst}\\LuaTools.SamHost.exe.old\" >nul 2>&1\r\n" +
            "goto relaunch\r\n" +
            ":restore\r\n" +
            $"if exist \"{dst}\\LuaTools.exe.old\" move /Y \"{dst}\\LuaTools.exe.old\" \"{dst}\\LuaTools.exe\" >nul 2>&1\r\n" +
            $"if exist \"{dst}\\LuaTools.SamHost.exe.old\" move /Y \"{dst}\\LuaTools.SamHost.exe.old\" \"{dst}\\LuaTools.SamHost.exe\" >nul 2>&1\r\n" +
            ":relaunch\r\n" +
            $"start \"\" /D \"{dst}\" \"{dst}\\LuaTools.exe\"\r\n" +
            ">nul ping -n 2 127.0.0.1\r\n" +
            $"del /F /Q \"{zipPath}\" >nul 2>&1\r\n" +
            $"rmdir /S /Q \"{work}\" >nul 2>&1\r\n" +
            "del /F /Q \"%~f0\" >nul 2>&1\r\n";
        File.WriteAllText(cmdPath, script);
        return cmdPath;
    }

    private static void LaunchSwap(string cmdPath) =>
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{cmdPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        })?.Dispose();
}
