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
    ToastService toast,
    DepotDownloaderService depotTool,
    SteamDepotInfo depotInfo)
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
            (_, progress, ct) => needsKey
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
            (_, progress, ct) => api.GenerateDlcAsync(appId.ToString(), baseAppId, gameName, progress, ct),
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
            (_, progress, ct) =>
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

    /// <summary>
    /// Raw depot content for a game. ONE queue item covers the whole selection; internally it runs the
    /// downloader once per depot, in list order.
    /// </summary>
    /// <remarks>
    /// Sequential by necessity, not preference: the tool's <c>-manifestfile</c> is a single value applied
    /// to every depot in its own loop, so a batched call would feed them all the same manifest.
    /// </remarks>
    public DownloadJob CreateDepotJob(
        long appId, string gameName, IReadOnlyList<DepotSelection> selections, string outDir,
        Action<DownloadItem, JobResult?>? onFinished = null)
    {
        long totalSize = selections.Sum(s => s.Size);
        return new DownloadJob(
            DownloadKind.Depot,
            $"depot:{appId}",
            appId,
            gameName,
            Resources.Strings.Downloads_Kind_Depot,
            covers.GetLocalPath(appId),
            (item, progress, ct) => RunDepotsAsync(item, appId, gameName, selections, outDir, totalSize, progress, ct),
            // Nothing to install: the depots were written straight to outDir.
            (_, _, _) => Task.FromResult(new JobResult(true,
                string.Format(Resources.Strings.Depot_Status_Done, selections.Count, outDir), outDir)),
            ConfirmAsync: null,
            OnFinished: onFinished);
    }

    private async Task<DownloadedFile> RunDepotsAsync(
        DownloadItem item, long appId, string gameName, IReadOnlyList<DepotSelection> selections,
        string outDir, long totalSize, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        var keys = depotTool.ResolveKeys(appId);
        if (keys.Count == 0) throw new DownloadAbortedException(Resources.Strings.Depot_Err_NoKeys);

        // Refuse up front rather than part-way through. The downloader pre-allocates every file at its
        // full size BEFORE fetching a byte, so a short disk fails almost immediately — but only after it
        // has already created multi-GB of zero-filled files. Checking here also gives a message that says
        // what's actually wrong instead of a raw allocation error.
        long needed = selections.Where(s => !item.CompletedDepots.Contains(s.DepotId)).Sum(s => s.Size);
        if (needed > 0 && DepotDownloaderService.FreeSpaceFor(outDir) is { } free && free < needed)
            throw new DownloadAbortedException(string.Format(
                Resources.Strings.Depot_Err_NoSpace, ByteFormat.Size(needed), ByteFormat.Size(free)));

        // Sampled ONCE, before anything runs. Checking it inside the loop would be self-fulfilling:
        // the first depot creates outDir, so every later depot would see it and think a previous session
        // had written there. (Harmless in cost — a depot whose files don't exist yet validates nothing —
        // but the intent is "did an earlier run leave partial files here", which is only true up front.)
        bool outDirExisted = Directory.Exists(outDir);

        string keysFile = DepotDownloaderService.WriteKeysFile(keys);
        try
        {
            long done = 0;
            for (int i = 0; i < selections.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var sel = selections[i];

                // Resume skips what's already finished rather than re-hashing tens of GB.
                if (item.CompletedDepots.Contains(sel.DepotId)) { done += sel.Size; continue; }

                string step = string.Format(Resources.Strings.Downloads_Depots_Progress, i + 1, selections.Count);
                OnUi(() => item.Detail = step);

                // A shared redistributable carries no gid or size in the game's own app-info (it's a
                // three-field stub pointing at the owning app), so both are resolved here rather than at
                // pick time. Cached per app by SteamDepotInfo, and the owner is app 228980 for nearly
                // every game, so this costs one lookup per session across all downloads.
                var sized = await ResolveSharedAsync(sel, ct);
                // The up-front total assumed 0 bytes for a shared depot. Correct it now we know better,
                // so the overall bar stays honest instead of finishing early.
                totalSize += sized.Size - sel.Size;

                // Resolve the manifest, fetching it into depotcache if Steam doesn't already have it.
                // This is what lets a depot be downloaded at all when the game was added with
                // "Auto Update Apps" on, which comments out the pins and skips the manifest files.
                var ready = sized with { ManifestPath = await EnsureManifestAsync(item, sized, step, ct) };

                // Only the FIRST depot after a resume is the partially-written one, so only it needs the
                // (expensive) re-hash. Consume the flag so later depots download at full speed.
                //
                // An existing output folder forces the same treatment even on a fresh item: it means a
                // previous session already wrote here, and CompletedDepots does not survive an app
                // restart. Skipping validation there would hand back a half-written file reported as
                // complete, which is this tool's worst failure mode.
                bool validate = item.NeedsValidate || outDirExisted;
                item.NeedsValidate = false;
                if (validate) OnUi(() => item.Status = DownloadStatus.Verifying);

                long baseBytes = done;
                bool sawBytes = false;
                var relay = new ProgressRelay<double>(f =>
                {
                    // First real progress means hashing is over and bytes are moving again.
                    if (validate && !sawBytes)
                    {
                        sawBytes = true;
                        OnUi(() => item.Status = DownloadStatus.Downloading);
                    }
                    progress.Report(new DownloadProgress(baseBytes + (long)(f * ready.Size), totalSize));
                });

                var res = await depotTool.RunAsync(appId, ready, keysFile, outDir, validate, relay, ct);
                if (!res.Ok)
                    throw new DownloadAbortedException(res.Error == "tool"
                        ? Resources.Strings.Depot_Err_Tool
                        : string.Format(Resources.Strings.Depot_Err_Failed, sel.DepotId, res.Error ?? ""));

                item.CompletedDepots.Add(sel.DepotId);
                done += ready.Size;
                progress.Report(new DownloadProgress(done, totalSize));
            }

            OnUi(() => item.Detail = null);
            // Sentinel for the queue's file plumbing: a directory, so the staged-file cleanup no-ops on it.
            return new DownloadedFile(outDir, gameName);
        }
        finally
        {
            DeleteStaged(keysFile); // holds decryption keys; never leave it lying around
        }
    }

    /// <summary>
    /// Fill in a shared depot's manifest id and size from the app that actually owns its content.
    /// Returns the selection unchanged for an ordinary depot (one that already declares its own gid).
    /// </summary>
    private async Task<DepotSelection> ResolveSharedAsync(DepotSelection sel, CancellationToken ct)
    {
        if (sel.ManifestId is not null || sel.FromAppId is not { } owner) return sel;

        var info = await depotInfo.GetAsync(owner, ct);
        if (info?.Depots.FirstOrDefault(d => d.Id == sel.DepotId) is not { PublicManifestId: not null } owned)
            throw new DownloadAbortedException(Resources.Strings.Depot_Err_NoManifest);

        return sel with { ManifestId = owned.PublicManifestId, Size = owned.Size };
    }

    /// <summary>
    /// The depotcache path for a depot's manifest, fetching it from the API and installing it there if
    /// it's missing. Never returns null — it throws with a user-facing reason instead.
    /// </summary>
    private async Task<string> EnsureManifestAsync(
        DownloadItem item, DepotSelection sel, string step, CancellationToken ct)
    {
        // Already on disk (a previous run, a pinned install, or Steam's own copy): no request at all.
        if (depotTool.ResolveManifestPath(sel.DepotId, sel.ManifestId!) is { } have) return have;

        if (!depotTool.CanFetchManifests)
            throw new DownloadAbortedException(Resources.Strings.Depot_Err_SignIn);

        OnUi(() => item.Detail = $"{step} · {Resources.Strings.Downloads_Depot_FetchingManifest}");

        DownloadedFile staged;
        try
        {
            staged = await api.DownloadDepotManifestAsync(sel.DepotId, sel.ManifestId!, null, ct);
        }
        catch (AuthException)
        {
            // Signed out between opening the picker and the download starting.
            throw new DownloadAbortedException(Resources.Strings.Depot_Err_SignIn);
        }

        // The API is expected to serve raw manifest bytes, but has been observed returning them inside
        // a ZIP (a single entry named "z"). Sniff rather than assume, exactly as InstallManifest does for
        // lua/zip: writing the wrapper into depotcache yields a file SteamKit rejects with
        // "Unrecognized magic value 4034B50" (0x04034B50 being the PK header), and it is sticky
        // once written because InstallManifestFile skips an existing destination.
        string unzipDir = Path.Combine(Path.GetDirectoryName(staged.FilePath)!, "mf_" + Guid.NewGuid().ToString("N"));
        try
        {
            string manifestFile = staged.FilePath;
            if (IsZip(staged.FilePath))
            {
                Directory.CreateDirectory(unzipDir);
                using var archive = ZipFile.OpenRead(staged.FilePath);
                var entry = archive.Entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.Name))
                    ?? throw new DownloadAbortedException(Resources.Strings.Depot_Err_NoManifest);

                // InstallManifestFile names the destination after this file, so it must already carry
                // the <depot>_<manifest>.manifest name; the entry inside the zip is just called "z".
                manifestFile = Path.Combine(unzipDir, $"{sel.DepotId}_{sel.ManifestId}.manifest");
                entry.ExtractToFile(manifestFile, overwrite: true);
            }

            // Check BEFORE writing. A bad file that reaches depotcache is sticky, so every later run
            // resolves it locally and fails identically with no way back short of deleting it by hand.
            if (!IsSteamManifest(manifestFile))
                throw new DownloadAbortedException(Resources.Strings.Depot_Err_NoManifest);

            // Reuse the same depotcache write that manifest installs already use: it keeps the
            // <depot>_<manifest>.manifest name, skips an identical existing file and stamps the mtime.
            var result = installer.InstallManifestFile(manifestFile);
            if (result.AnyFailed)
                throw new DownloadAbortedException(result.Error ?? Resources.Strings.Depot_Err_NoManifest);
        }
        finally
        {
            DeleteStaged(staged.FilePath);
            try { if (Directory.Exists(unzipDir)) Directory.Delete(unzipDir, recursive: true); } catch { }
        }

        OnUi(() => item.Detail = step);

        return depotTool.ResolveManifestPath(sel.DepotId, sel.ManifestId!)
               ?? throw new DownloadAbortedException(Resources.Strings.Depot_Err_NoManifest);
    }

    /// <summary>Marshal an observable-property write onto the dispatcher (this runs on a worker).</summary>
    private static void OnUi(Action a) =>
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(a);

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

    /// <summary>
    /// True if the file starts with Steam's depot-manifest magic (0x71F617D0, little-endian on disk).
    /// A cheap guard against storing something that merely arrived under a .manifest name.
    /// </summary>
    private static bool IsSteamManifest(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> sig = stackalloc byte[4];
            return fs.Read(sig) == 4 &&
                   sig[0] == 0xD0 && sig[1] == 0x17 && sig[2] == 0xF6 && sig[3] == 0x71;
        }
        catch { return false; }
    }

    /// <summary>Best-effort delete of a staged download once it has been consumed.</summary>
    public static void DeleteStaged(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
