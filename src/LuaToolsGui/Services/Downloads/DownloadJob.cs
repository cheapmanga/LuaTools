namespace LuaToolsGui.Services.Downloads;

/// <summary>What a queued download is for. Drives the row icon and the history label.</summary>
public enum DownloadKind { Manifest, Dlc, DenuvoManifest, DenuvoFix }

/// <summary>
/// A job refused to start for a reason the user can act on — e.g. the game a Denuvo fix targets isn't
/// installed. Carries a ready-to-display, already-localized message.
/// </summary>
/// <remarks>
/// Distinct from <see cref="ApiException"/> on purpose: this is thrown *before* any request is made, so
/// nothing was spent (no bandwidth, and no slot of the server-side daily limit). Reusing ApiException
/// would surface the message correctly but would name the failure after a call that never happened.
/// </remarks>
public sealed class DownloadAbortedException(string message) : Exception(message);

/// <summary>Outcome of a job's install phase.</summary>
/// <param name="Message">User-facing result text, already localized by the factory.</param>
public sealed record JobResult(bool Ok, string? Message, string? InstalledPath = null);

/// <summary>
/// One unit of work handed to <see cref="DownloadQueue"/>.
/// </summary>
/// <remarks>
/// The queue is a pure scheduler. It knows nothing about Hubcap vs lua.tools vs signed R2 URLs, and
/// nothing about <c>LuaInstaller</c> or <c>SteamService</c>; all of that lives in the delegates, which
/// in practice are always built by <see cref="ManifestJobFactory"/>. That keeps the three formerly
/// duplicated download+install implementations (the Add page, the plugin add service and the HTTP
/// server) sharing exactly one code path.
///
/// Deliberate consequence of holding delegates: a job is NOT serializable, so nothing resumes across an
/// app restart. Only the completed-history <see cref="DownloadHistoryRecord"/> persists.
/// </remarks>
public sealed record DownloadJob(
    DownloadKind Kind,

    /// <summary>
    /// Identity for duplicate suppression: "manifest:730", "dlc:12345", "denuvo:{fixId}:{slot}".
    /// Enqueuing a key that is already active returns the existing item instead of a second download.
    /// This is what replaces the old per-page <c>if (IsBusy) return;</c> gates.
    /// </summary>
    string DedupeKey,

    long AppId,
    string Title,
    string SubTitle,
    string? CoverPath,

    /// <summary>Fetch the bytes to a staged file. Runs on a background thread.</summary>
    Func<IProgress<DownloadProgress>, CancellationToken, Task<DownloadedFile>> DownloadAsync,

    /// <summary>Consume the staged file (install into Steam). Runs serialized against other installs.</summary>
    Func<DownloadedFile, DownloadItem, CancellationToken, Task<JobResult>> InstallAsync,

    /// <summary>
    /// Optional gate between download and install; true proceeds, false discards the staged file.
    /// While this is awaited the item is <see cref="DownloadStatus.AwaitingConfirmation"/> and has
    /// RELEASED its concurrency slot, so an unanswered dialog can never wedge the queue.
    /// </summary>
    Func<DownloadedFile, DownloadItem, CancellationToken, Task<bool>>? ConfirmAsync = null,

    /// <summary>
    /// Fired on the dispatcher once the item reaches a terminal state: usage-badge refresh, install
    /// banner, plugin AddState mutation. Exceptions are swallowed so a bad continuation cannot kill
    /// the pump.
    /// </summary>
    Action<DownloadItem, JobResult?>? OnFinished = null,

    /// <summary>Target of the Downloads tab's "Review" / "Reveal" button (e.g. navigate to Add).</summary>
    Action? OnReveal = null);
