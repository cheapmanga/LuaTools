using System.IO;
using System.IO.Compression;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services.Downloads;

/// <summary>
/// Builds the <see cref="DownloadJob"/>s for every in-scope download: game manifests, DLC generation
/// and Denuvo fixes.
/// </summary>
/// <remarks>
/// This is the convergence point for what used to be three separate download+install implementations:
/// <c>DownloadViewModel.DownloadFromSourceAsync</c>, <c>PluginAddService.DownloadAsync</c> and
/// <c>HttpServerService.DownloadAndInstallAsync</c>. The Hubcap-vs-lua.tools branch, the ZIP byte
/// sniff (previously copy-pasted into three files) and the staged-file cleanup now exist exactly once.
/// </remarks>
public class ManifestJobFactory(
    LuaToolsApiClient api,
    HubcapService hubcap,
    SettingsService settings,
    LuaInstaller installer,
    SteamLibraryService library,
    CoverCache covers,
    ToastService toast)
{
    // ── Job builders ─────────────────────────────────────────────────

    /// <summary>A base-game manifest from a named source (Hubcap uses the user's own key).</summary>
    public DownloadJob CreateManifestJob(
        long appId, string? gameName, string sourceName, bool needsKey,
        Func<DownloadedFile, DownloadItem, CancellationToken, Task<bool>>? confirm = null,
        Action<DownloadItem, JobResult?>? onFinished = null,
        Action? onReveal = null)
    {
        string title = gameName ?? appId.ToString();
        return new DownloadJob(
            DownloadKind.Manifest,
            $"manifest:{appId}",
            appId,
            title,
            SourceMeta.Get(sourceName).DisplayName ?? sourceName,
            covers.GetLocalPath(appId),
            (progress, ct) => needsKey
                ? hubcap.DownloadManifestAsync(appId.ToString(), settings.HubcapApiKey ?? "", progress, ct)
                : api.DownloadManifestAsync(appId.ToString(), sourceName, gameName, progress, ct),
            (file, _, _) => Task.FromResult(InstallManifest(file, appId, title)),
            confirm,
            onFinished,
            onReveal);
    }

    /// <summary>DLC unlock lua. Installed silently: it's an unlock, so there's nothing to confirm.</summary>
    public DownloadJob CreateDlcJob(
        long appId, string baseAppId, string? gameName,
        Action<DownloadItem, JobResult?>? onFinished = null,
        Action? onReveal = null)
    {
        string title = gameName ?? appId.ToString();
        return new DownloadJob(
            DownloadKind.Dlc,
            $"dlc:{appId}",
            appId,
            title,
            Resources.Strings.Downloads_Kind_Dlc,
            covers.GetLocalPath(appId),
            (progress, ct) => api.GenerateDlcAsync(appId.ToString(), baseAppId, gameName, progress, ct),
            (file, _, _) => Task.FromResult(InstallManifest(file, appId, title)),
            ConfirmAsync: null,
            OnFinished: onFinished,
            OnReveal: onReveal);
    }

    /// <summary>
    /// A Denuvo fix slot. "manifest" installs force-locked into Steam (fixes must stay version-pinned);
    /// "fix" extracts the zip into the game's install folder. Neither restarts Steam.
    /// </summary>
    public DownloadJob CreateDenuvoJob(
        string fixId, string slot, string fallbackName,
        long appId, string gameName, string fixTitle,
        Action<DownloadItem, JobResult?>? onFinished = null)
    {
        bool isManifestSlot = slot == "manifest";
        return new DownloadJob(
            isManifestSlot ? DownloadKind.DenuvoManifest : DownloadKind.DenuvoFix,
            $"denuvo:{fixId}:{slot}",
            appId,
            gameName,
            fixTitle,
            covers.GetLocalPath(appId),
            (progress, ct) =>
            {
                // Verify the game is on disk BEFORE the request. /api/denuvo/download spends a slot of
                // the server-side daily limit and the fix zip is game binaries, so discovering "not
                // installed" in the install phase (where ApplyDenuvoFix still checks, as a backstop)
                // costs a slot and a full download for nothing. The Fixes page disables the button for
                // uninstalled games, but a queued fix can outlive that check if the user uninstalls
                // while it waits its turn.
                if (!isManifestSlot && library.GetInstallDir(appId) is null)
                    throw new DownloadAbortedException(
                        string.Format(Resources.Strings.Fixes_Toast_GameNotFound_Body, gameName));
                return api.DownloadDenuvoAsync(fixId, slot, fallbackName, progress, ct);
            },
            (file, _, _) => Task.FromResult(isManifestSlot
                ? InstallDenuvoManifest(file, appId, gameName)
                : ApplyDenuvoFix(file, appId, gameName)),
            ConfirmAsync: null,
            OnFinished: onFinished);
    }

    // ── Install phases ───────────────────────────────────────────────

    /// <summary>
    /// Install a downloaded manifest/lua into Steam and turn the result into a user-facing message.
    /// </summary>
    /// <remarks>
    /// The file is always staged as "&lt;appid&gt;.zip", but some sources return a BARE .lua with no zip
    /// wrapper. Unzipping that throws "End of Central Directory record could not be found", so the
    /// bytes are sniffed rather than the extension trusted.
    /// </remarks>
    private JobResult InstallManifest(DownloadedFile file, long appId, string gameName)
    {
        try
        {
            var result = IsZip(file.FilePath)
                ? installer.InstallZip(file.FilePath, appId)
                : installer.InstallLua(file.FilePath, appId);

            if (result.Error is not null) return new JobResult(false, result.Error);
            if (result.AnyFailed)
                return new JobResult(false,
                    string.Format(Resources.Strings.Add_Status_InstallFailed, result.Failed.Count));

            string message = result.ManifestCount > 0
                ? string.Format(Resources.Strings.Add_Status_AddedManifests, gameName, result.ManifestCount)
                : string.Format(Resources.Strings.Add_Status_AddedFetch, gameName);
            return new JobResult(true, message, file.FilePath);
        }
        finally
        {
            DeleteStaged(file.FilePath); // consumed by the install
        }
    }

    /// <summary>
    /// Denuvo manifest slot: force-locked install (version-pinned so an auto-update can't break the fix).
    /// </summary>
    /// <remarks>
    /// This used to call <c>SteamService.RestartSteam()</c> unconditionally and without asking, which
    /// killed Steam and every running game on each fix install. OpenSteamTools/BetterSteamTools watch
    /// the lua directories listed in <c>opensteamtool.toml</c>'s <c>[lua] paths</c> — which includes the
    /// <c>config/stplug-in</c> we just wrote to — so the write itself applies the change live.
    /// </remarks>
    private JobResult InstallDenuvoManifest(DownloadedFile file, long appId, string gameName)
    {
        try
        {
            bool isZip = file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            var result = isZip
                ? installer.InstallZip(file.FilePath, appId, forceLocked: true)
                : installer.InstallLuaFile(file.FilePath, appId, forceLocked: true);

            if (result.AnyFailed)
            {
                string err = result.Error ?? Resources.Strings.Fixes_Toast_InstallFailed_Body;
                toast.Show(Resources.Strings.Fixes_Toast_InstallFailed, err, error: true);
                return new JobResult(false, err);
            }

            string message = string.Format(Resources.Strings.Fixes_Toast_FixInstalled_Body, gameName);
            toast.Show(Resources.Strings.Fixes_Toast_FixInstalled, message);
            return new JobResult(true, message, file.FilePath);
        }
        finally
        {
            DeleteStaged(file.FilePath);
        }
    }

    /// <summary>Denuvo fix slot: extract into the game folder. Only possible if the game is installed.</summary>
    private JobResult ApplyDenuvoFix(DownloadedFile file, long appId, string gameName)
    {
        try
        {
            string? installDir = library.GetInstallDir(appId);
            if (installDir is null)
            {
                string err = string.Format(Resources.Strings.Fixes_Toast_GameNotFound_Body, gameName);
                toast.Show(Resources.Strings.Fixes_Toast_GameNotFound, err, error: true);
                return new JobResult(false, err);
            }

            // Extract into the game folder, overwriting. Best-effort per entry so one locked file
            // doesn't abandon the rest of the fix.
            using var archive = ZipFile.OpenRead(file.FilePath);
            int failed = 0;
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
                string dest = Path.Combine(installDir, entry.FullName);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);
                }
                catch { failed++; }
            }

            if (failed > 0)
            {
                string err = string.Format(Resources.Strings.Fixes_Toast_PartiallyApplied_Body, failed);
                toast.Show(Resources.Strings.Fixes_Toast_PartiallyApplied, err, error: true);
                return new JobResult(false, err);
            }

            string message = string.Format(Resources.Strings.Fixes_Toast_FixApplied_Body, gameName);
            toast.Show(Resources.Strings.Fixes_Toast_FixApplied, message);
            return new JobResult(true, message, installDir);
        }
        catch (Exception ex)
        {
            toast.Show(Resources.Strings.Fixes_Toast_CouldntApply, ex.Message, error: true);
            return new JobResult(false, ex.Message);
        }
        finally
        {
            DeleteStaged(file.FilePath); // archive is disposed by now
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// True if the file begins with the ZIP local-file-header magic (PK\x03\x04). A bare .lua (or any
    /// non-zip a source returned under a .zip name) returns false, so it installs as a loose lua.
    /// </summary>
    public static bool IsZip(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> sig = stackalloc byte[4];
            return fs.Read(sig) == 4 && sig[0] == 0x50 && sig[1] == 0x4B && sig[2] == 0x03 && sig[3] == 0x04;
        }
        catch { return false; }
    }

    /// <summary>Best-effort delete of a staged download once it has been consumed.</summary>
    public static void DeleteStaged(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
