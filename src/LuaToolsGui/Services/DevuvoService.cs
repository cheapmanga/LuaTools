using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui.Services;

/// <summary>How a validation run ended.</summary>
public enum DevuvoRunResult
{
    Finished,
    /// <summary>The user dismissed the elevation prompt. Not an error.</summary>
    ElevationDeclined,
    /// <summary>Neither source served the script.</summary>
    ScriptUnavailable,
    Failed,
}

/// <summary>
/// Runs Devuvo.ps1 - the script that checks and repairs a broken Denuvo activation - and streams its
/// output back line by line.
/// </summary>
/// <remarks>
/// <para>The script is FETCHED, never bundled: LuaTools' own author publishes it at
/// <c>luatools.vercel.app</c> with a copy in <c>madoiscool/lt_api_links</c>, and it changes as games and
/// checks change. Pinning a copy inside the app would freeze the repairs at build time, so this reads
/// whatever is current and treats the script as a black box - the only contract relied on is its input
/// variables and the markers it prints.</para>
///
/// <para>It runs ELEVATED, because it touches the Steam folder and the registry. Elevation means
/// ShellExecute, and ShellExecute cannot redirect stdout - so the script writes a PowerShell transcript
/// and this tails that file instead. The console window is deliberately left visible: it is the user's
/// only way to stop a process this one is not allowed to kill.</para>
/// </remarks>
public class DevuvoService(ILogger<DevuvoService> log)
{
    private static readonly string RunDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LuaToolsGui", "devuvo");

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// Fetch the current script. Null when neither source answered.
    /// </summary>
    /// <remarks>
    /// <para>The Vercel copy is authoritative and comes first, with the raw GitHub copy behind it.</para>
    ///
    /// <para>Deliberately NOT through <see cref="GithubProxy"/>, which is used everywhere else in this
    /// app: its fallback mirrors are third parties, and this script is run with administrator rights.
    /// Accepting relayed code to run elevated would hand any of those mirrors the machine. A user
    /// behind a GitHub block loses the fallback and keeps the primary source; that is the right way
    /// round.</para>
    /// </remarks>
    public async Task<string?> FetchScriptAsync(CancellationToken ct = default)
    {
        foreach (var url in AppConfig.DevuvoScriptUrls)
        {
            try
            {
                string body = await _http.GetStringAsync(url, ct);

                // A source that answers with an error page is worse than one that doesn't answer: it
                // would be written to disk and run. Require something that looks like the script.
                if (!string.IsNullOrWhiteSpace(body) && body.Contains("$AppID", StringComparison.Ordinal))
                    return body;

                log.LogDebug("{Url} did not return the validation script", url);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Fetching the validation script from {Url} failed", url);
            }
        }

        return null;
    }

    /// <summary>
    /// Run the script for one game and report every line it prints.
    /// </summary>
    /// <param name="onLine">Called on a background thread for each new transcript line.</param>
    public async Task<DevuvoRunResult> RunAsync(
        long appId, bool lockVersion, Action<string> onLine, CancellationToken ct = default)
    {
        string? script = await FetchScriptAsync(ct);
        if (script is null) return DevuvoRunResult.ScriptUnavailable;

        try
        {
            Directory.CreateDirectory(RunDir);
            string stamp = $"{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            string scriptPath = Path.Combine(RunDir, $"Devuvo-{stamp}.ps1");
            string wrapperPath = Path.Combine(RunDir, $"Run-{stamp}.ps1");
            string logPath = Path.Combine(RunDir, $"Run-{stamp}.log");

            // The script prompts for an AppID when it isn't given one, which would hang forever behind
            // -NonInteractive. Fail loudly instead - the wrapper always sets it.
            script = script.Replace(
                "$AppID = Read-Host \"Enter Steam AppID\"",
                "throw 'No Steam AppID was passed by LuaTools.'",
                StringComparison.Ordinal);

            await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(true), ct);
            await File.WriteAllTextAsync(wrapperPath, string.Join("\r\n",
            [
                "$ErrorActionPreference = 'Continue'",
                // Tells the script it is driven by a UI: it then prints progress markers instead of
                // waiting on keypresses.
                "$env:LUATOOLS_APP_MODE = '1'",
                "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12",
                $"Start-Transcript -Path '{Escape(logPath)}' -Force | Out-Null",
                $"$AppID = '{appId}'",
                // Section 6 of the script: pins the game to its installed build so a Steam update
                // cannot wipe the activation.
                $"$LockVersion = ${(lockVersion ? "true" : "false")}",
                $"try {{ . '{Escape(scriptPath)}' }} catch {{ Write-Host \"[!] $_\" }}",
                "Stop-Transcript | Out-Null",
            ]), new UTF8Encoding(true), ct);

            var psi = new ProcessStartInfo(PowerShellPath())
            {
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -NonInteractive -File \"{wrapperPath}\"",
                // Elevation and redirection are mutually exclusive; this is why the transcript exists.
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = RunDir,
            };

            using var process = Process.Start(psi);
            if (process is null) return DevuvoRunResult.Failed;

            await TailAsync(logPath, process, onLine, ct);
            return DevuvoRunResult.Finished;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the UAC prompt was dismissed. The user's choice, not a failure.
            return DevuvoRunResult.ElevationDeclined;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Running the validation script failed");
            return DevuvoRunResult.Failed;
        }
    }

    /// <summary>
    /// Follow the transcript while the script runs, then read what it wrote as it exited.
    /// </summary>
    /// <remarks>
    /// Opened with the widest possible sharing because PowerShell holds the file open for writing the
    /// whole time. The final read after exit is not belt-and-braces: the last poll and the process
    /// ending race, and the report code the user came for is on the very last lines.
    /// </remarks>
    private static async Task TailAsync(string logPath, Process process, Action<string> onLine, CancellationToken ct)
    {
        long offset = 0;
        var pending = new StringBuilder();

        while (true)
        {
            bool exited = process.HasExited;
            offset = Drain(logPath, offset, pending, onLine);
            if (exited)
            {
                // One last pass: the transcript is flushed and closed as PowerShell tears down.
                await Task.Delay(300, ct);
                Drain(logPath, offset, pending, onLine);
                if (pending.Length > 0) onLine(pending.ToString());
                return;
            }

            await Task.Delay(250, ct);
        }
    }

    private static long Drain(string logPath, long offset, StringBuilder pending, Action<string> onLine)
    {
        try
        {
            if (!File.Exists(logPath)) return offset;

            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length <= offset) return offset;

            fs.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string chunk = reader.ReadToEnd();
            offset = fs.Position;

            pending.Append(chunk);
            string text = pending.ToString();
            int last = text.LastIndexOf('\n');
            if (last < 0) return offset;

            foreach (var line in text[..last].Split('\n'))
                onLine(line.TrimEnd('\r'));

            pending.Clear();
            pending.Append(text[(last + 1)..]);
            return offset;
        }
        catch (IOException)
        {
            return offset; // being written to right now; the next poll gets it
        }
    }

    private static string Escape(string path) => path.Replace("'", "''");

    /// <summary>
    /// Windows PowerShell, by full path first. Some machines don't have it on PATH, and a bare
    /// "powershell.exe" would then fail for a reason the user cannot act on.
    /// </summary>
    private static string PowerShellPath()
    {
        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.System),
                     Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
                 })
        {
            if (string.IsNullOrEmpty(folder)) continue;
            string candidate = Path.Combine(folder, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(candidate)) return candidate;
        }

        return "powershell.exe";
    }
}
