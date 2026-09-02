using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace LuaToolsGui.Services;

/// <summary>What the code store answered, and what was done with it.</summary>
/// <param name="AppId">The game the code unlocked, once a redeem succeeded.</param>
/// <param name="Error">Why it didn't, in the server's own words when it gave one.</param>
public record TokeerRedeemResult(bool Ok, long AppId = 0, string? Error = null);

/// <summary>The code store's reply. Fields it omits simply stay null.</summary>
internal sealed class TokeerRedeemResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("app_id")] public string? AppId { get; set; }
    [JsonPropertyName("appticket")] public string? AppTicket { get; set; }
    [JsonPropertyName("eticket")] public string? ETicket { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>
/// Redeems a Tokeer activation code: asks the code store for the tickets it holds, then writes them
/// into Steam's credential store so the game launches.
/// </summary>
/// <remarks>
/// <para>Only the redeem half is here. Generating a code needs an ownership ticket pulled from the
/// live Steam session, which their app does with a helper binary published without source; that is a
/// reverse-engineering project of its own, so the Downloads page still offers their app for it.</para>
///
/// <para>What leaves this machine is the code and the Windows MachineGuid, nothing else: a code is
/// bound to the machine that opened the ticket, and the store compares that id to refuse a code
/// forwarded to someone else.</para>
/// </remarks>
public class TokeerService(ILogger<TokeerService> log)
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(25) };

    /// <summary>Redeem a code and write the tickets it returns.</summary>
    public async Task<TokeerRedeemResult> RedeemAsync(string code, CancellationToken ct = default)
    {
        code = code.Trim();
        if (code.Length == 0) return new TokeerRedeemResult(false);

        TokeerRedeemResponse? data;
        try
        {
            using var res = await _http.PostAsJsonAsync(
                AppConfig.TokeerRedeemUrl, new { code, hwid = MachineGuid() }, ct);
            data = await res.Content.ReadFromJsonAsync<TokeerRedeemResponse>(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "The code store could not be reached");
            return new TokeerRedeemResult(false, Error: Resources.Strings.Tokeer_Err_Unreachable);
        }

        if (data is null || !data.Success)
            return new TokeerRedeemResult(false, Error: data?.Reason ?? data?.Error);

        if (!long.TryParse(data.AppId, out long appId) || appId <= 0
            || string.IsNullOrWhiteSpace(data.AppTicket) || string.IsNullOrWhiteSpace(data.ETicket))
            return new TokeerRedeemResult(false, Error: Resources.Strings.Tokeer_Err_Incomplete);

        try
        {
            WriteTickets(appId, data.AppTicket!.Trim(), data.ETicket!.Trim());
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Writing the tickets for {AppId} failed", appId);
            return new TokeerRedeemResult(false, Error: Resources.Strings.Tokeer_Err_Write);
        }

        return new TokeerRedeemResult(true, appId);
    }

    /// <summary>
    /// Write the ownership ticket, the encrypted ticket and the owner's SteamID under the game's key.
    /// </summary>
    /// <remarks>
    /// The SteamID matters as much as the tickets. It is read FIRST, and only missing does the engine
    /// fall back to the id inside the ownership ticket - so a value left by an earlier redeem outranks
    /// the tickets just written, the two disagree, and the launch is refused from then on. When the
    /// owner cannot be read out of the ticket the stale value is deleted rather than left standing.
    /// </remarks>
    private static void WriteTickets(long appId, string appTicketHex, string eTicketHex)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Valve\Steam\Apps\{appId}", true);
        if (key is null) throw new InvalidOperationException("Steam's app key could not be opened.");

        key.SetValue("AppTicket", FromHex(appTicketHex), RegistryValueKind.Binary);
        key.SetValue("ETicket", FromHex(eTicketHex), RegistryValueKind.Binary);

        string owner = OwnerSteamId(appTicketHex);
        if (owner.Length > 0)
            key.SetValue("SteamID", owner, RegistryValueKind.String); // decimal digits: the only form it parses
        else
            try { key.DeleteValue("SteamID", false); } catch { /* nothing to clear */ }
    }

    /// <summary>The SteamID64 carried by an ownership ticket: a little-endian uint64 at offset 8.</summary>
    private static string OwnerSteamId(string appTicketHex)
    {
        try
        {
            byte[] data = FromHex(appTicketHex);
            if (data.Length < 16) return "";

            ulong sid = BitConverter.ToUInt64(data, 8);
            // Below the individual-account base the value is not a SteamID at all, which means the
            // ticket isn't shaped as expected - better to write nothing than something wrong.
            return sid >= 76561197960265728UL ? sid.ToString() : "";
        }
        catch
        {
            return "";
        }
    }

    private static byte[] FromHex(string hex) => Convert.FromHexString(hex.Trim());

    /// <summary>
    /// This machine's Windows MachineGuid, or "" when it cannot be read.
    /// </summary>
    /// <remarks>
    /// Read exactly as the code store expects it - unhashed, untrimmed beyond whitespace - because the
    /// store compares it against the machine the ticket was opened on. Anything else turns a legitimate
    /// redeem into a rejection.
    /// </remarks>
    private static string MachineGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid")?.ToString()?.Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }
}
