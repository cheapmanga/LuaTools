using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace LuaToolsGui.Services;

/// <summary>What the code store answered, and what was done with it.</summary>
/// <param name="AppId">The game the code unlocked, once a redeem succeeded.</param>
/// <param name="Error">Why it didn't, in the server's own words when it gave one.</param>
public record TokeerRedeemResult(bool Ok, long AppId = 0, string? Error = null);

/// <summary>A minted code, or why one could not be minted.</summary>
public record TokeerGenerateResult(bool Ok, string Code = "", string? Error = null);

/// <summary>
/// Redeems and generates Tokeer activation codes: the code store holds the tickets, and Steam's
/// credential store is where they live on this machine.
/// </summary>
/// <remarks>
/// <para>Both halves use the same registry values, because that is where Steam keeps them - the same
/// place their Linux port reads, and the same values a redeem writes. Their Windows app instead
/// drives the live session through a helper published without source; the difference is that a game
/// which has never been launched on this PC has no ticket to read yet, and the page says so.</para>
///
/// <para>What leaves this machine is the code and the Windows MachineGuid on a redeem, and the two
/// tickets plus the account ids on a generate. A code is bound to the machine that opened the
/// ticket, and the store compares that id to refuse a code forwarded to someone else.</para>
/// </remarks>
public class TokeerService(ILogger<TokeerService> log)
{
    /// <remarks>
    /// The User-Agent is not decoration: the store sits behind Cloudflare, .NET sends none by default,
    /// and an absent one is exactly what a WAF rule drops. Their client sends requests' own.
    /// </remarks>
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(25),
        DefaultRequestHeaders = { { "User-Agent", "LuaTools" } },
    };

    /// <summary>The individual/public SteamID64 base (0x0110000100000000).</summary>
    private const ulong SteamId64Base = 76561197960265728UL;

    /// <summary>Redeem a code and write the tickets it returns.</summary>
    public async Task<TokeerRedeemResult> RedeemAsync(string code, CancellationToken ct = default)
    {
        code = NormalizeCode(code);
        if (code.Length == 0) return new TokeerRedeemResult(false);

        var (payload, error) = await PostAsync(AppConfig.TokeerRedeemUrl,
            new { code, hwid = MachineGuid() }, ct);
        if (payload is null) return new TokeerRedeemResult(false, Error: error);

        var body = payload.Value;
        if (!Succeeded(body))
            return new TokeerRedeemResult(false, Error: Reason(body));

        // app_id is read leniently because the store is free to send it as a number or a string, and
        // finding out which the hard way would cost the user their code: it is spent by the time this
        // reply arrives, so a parse that throws here loses it for nothing.
        if (!TryGetAppId(body, out long appId)
            || Text(body, "appticket") is not { Length: > 0 } appTicket
            || Text(body, "eticket") is not { Length: > 0 } eTicket)
            return new TokeerRedeemResult(false, Error: Resources.Strings.Tokeer_Err_Incomplete);

        try
        {
            WriteTickets(appId, appTicket, eTicket);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Writing the tickets for {AppId} failed", appId);
            return new TokeerRedeemResult(false, Error: Resources.Strings.Tokeer_Err_Write);
        }

        return new TokeerRedeemResult(true, appId);
    }

    /// <summary>
    /// Mint a shareable code for a game this account owns.
    /// </summary>
    /// <remarks>
    /// <para>The tickets come from Steam's own store rather than from a live session request. That is
    /// the same data - Steam writes both values when it starts a game that asks for them - and it
    /// costs no interop with a private client interface. The price is the precondition: a game that
    /// has never run on this PC has nothing cached, and the caller is told to launch it once.</para>
    ///
    /// <para>The anti-resell guard is not optional. After redeeming someone else's code the original
    /// owner's ticket sits in this machine's registry; without comparing it against the account
    /// actually signed in, anyone who redeemed a code could mint fresh ones for a game they do not
    /// own - laundering one shared code into many. It refuses only when both ids are known and
    /// disagree, matching their client: an unreadable id is not evidence of anything.</para>
    /// </remarks>
    public async Task<TokeerGenerateResult> GenerateAsync(long appId, CancellationToken ct = default)
    {
        if (appId <= 0) return new TokeerGenerateResult(false);

        var (appTicket, eTicket) = ReadTickets(appId);
        if (appTicket is null || eTicket is null)
            return new TokeerGenerateResult(false, Error: Resources.Strings.Tokeer_Err_NoTicket);

        string owner = OwnerSteamId(appTicket);
        if (owner.Length == 0)
            return new TokeerGenerateResult(false, Error: Resources.Strings.Tokeer_Err_NoTicket);

        string current = CurrentSteamId();
        if (current.Length > 0 && !string.Equals(owner, current, StringComparison.Ordinal))
            return new TokeerGenerateResult(false, Error: Resources.Strings.Tokeer_Err_NotOwner);

        var (payload, error) = await PostAsync(AppConfig.TokeerGenerateUrl, new
        {
            appticket = appTicket,
            eticket = eTicket,
            steam_id = owner,
            app_id = appId.ToString(),
            // Codes are single-use; the store enforces it, and offering a dial here would only
            // promise something the server refuses.
            max_uses = 1,
            created_by_user = owner,
            current_steam_id = current,
        }, ct);
        if (payload is null) return new TokeerGenerateResult(false, Error: error);

        var body = payload.Value;
        if (!Succeeded(body) || Text(body, "code") is not { Length: > 0 } code)
            return new TokeerGenerateResult(false,
                Error: Reason(body) ?? Resources.Strings.Tokeer_Err_NoCode);

        return new TokeerGenerateResult(true, code);
    }

    // ── Transport ────────────────────────────────────────────────────

    /// <summary>
    /// POST a body and hand back the parsed reply, or null and a message to show.
    /// </summary>
    /// <remarks>
    /// <para>Everything is read as a <see cref="JsonElement"/> rather than into a typed class: the
    /// store's exact shapes are not ours to pin, and a strongly-typed read throws on a field whose
    /// type merely differs - after a redeem has already spent the code.</para>
    ///
    /// <para>A non-JSON reply is a case, not an accident. Cloudflare answers 403s, rate limits and
    /// maintenance with HTML, and collapsing those into "couldn't reach the store" would hide the
    /// only clue the user has.</para>
    ///
    /// <para>A timeout must NOT escape: HttpClient reports it as a TaskCanceledException, and since
    /// these run from an async command with no handler above them, rethrowing would take the whole
    /// app down. Only a cancellation the caller actually asked for is passed on.</para>
    /// </remarks>
    private async Task<(JsonElement? Payload, string? Error)> PostAsync(string url, object body, CancellationToken ct)
    {
        try
        {
            using var res = await _http.PostAsJsonAsync(url, body, ct);
            string text = await res.Content.ReadAsStringAsync(ct);

            try
            {
                using var doc = JsonDocument.Parse(text);
                return (doc.RootElement.Clone(), null);
            }
            catch (JsonException)
            {
                log.LogDebug("The code store answered {Status} with a non-JSON body", res.StatusCode);
                string snippet = text.Trim();
                if (snippet.Length > 200) snippet = snippet[..200];
                return (null, snippet.Length > 0 ? $"{(int)res.StatusCode}: {snippet}"
                                                 : Resources.Strings.Tokeer_Err_Unreachable);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "The code store could not be reached");
            return (null, Resources.Strings.Tokeer_Err_Unreachable);
        }
    }

    private static bool Succeeded(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty("success", out var s)
        && (s.ValueKind == JsonValueKind.True
            || (s.ValueKind == JsonValueKind.String && bool.TryParse(s.GetString(), out bool b) && b));

    private static string? Text(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()?.Trim()
            : null;

    /// <summary>The store's own explanation, or null when it gave none worth showing.</summary>
    /// <remarks>
    /// Blank is treated as absent, not as an explanation. A reply of <c>{"success":false,"reason":""}</c>
    /// would otherwise put an empty string on screen and the page would report nothing at all.
    /// </remarks>
    private static string? Reason(JsonElement body)
    {
        foreach (var name in new[] { "reason", "error" })
            if (Text(body, name) is { Length: > 0 } value)
                return value;

        return null;
    }

    /// <summary>The app id, accepted as either a JSON number or a string of digits.</summary>
    private static bool TryGetAppId(JsonElement body, out long appId)
    {
        appId = 0;
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("app_id", out var v)) return false;

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out appId) && appId > 0,
            JsonValueKind.String => long.TryParse(v.GetString(), out appId) && appId > 0,
            _ => false,
        };
    }

    // ── Steam's credential store ─────────────────────────────────────

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
        // Both are decoded BEFORE anything is written. Writing one and throwing on the other would
        // leave a new AppTicket beside a stale ETicket - precisely the mismatch described above, and
        // a retry cannot repair it because the second write never happens.
        byte[] appTicket = FromHex(appTicketHex);
        byte[] eTicket = FromHex(eTicketHex);
        string owner = OwnerSteamId(appTicketHex);

        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Valve\Steam\Apps\{appId}", true);
        if (key is null) throw new InvalidOperationException("Steam's app key could not be opened.");

        key.SetValue("AppTicket", appTicket, RegistryValueKind.Binary);
        key.SetValue("ETicket", eTicket, RegistryValueKind.Binary);

        if (owner.Length > 0)
            key.SetValue("SteamID", owner, RegistryValueKind.String); // decimal digits: the only form it parses
        else
            try { key.DeleteValue("SteamID", false); } catch { /* nothing to clear */ }
    }

    /// <summary>
    /// The ownership and encrypted tickets Steam cached for a game, as hex, or nulls when it has none.
    /// </summary>
    private static (string? AppTicket, string? ETicket) ReadTickets(long appId)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Valve\Steam\Apps\{appId}");
            if (key is null) return (null, null);

            // Both are REG_BINARY. Anything else under these names is not a ticket, and passing it on
            // would have the server reject a request we could have refused here.
            return (key.GetValue("AppTicket") is byte[] { Length: > 0 } app
                        ? Convert.ToHexString(app).ToLowerInvariant() : null,
                    key.GetValue("ETicket") is byte[] { Length: > 0 } e
                        ? Convert.ToHexString(e).ToLowerInvariant() : null);
        }
        catch
        {
            return (null, null);
        }
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
            return sid >= SteamId64Base ? sid.ToString() : "";
        }
        catch
        {
            return "";
        }
    }

    private static byte[] FromHex(string hex) => Convert.FromHexString(hex.Trim());

    /// <summary>
    /// The SteamID64 of the account signed into Steam right now, or "" when Steam isn't running.
    /// </summary>
    /// <remarks>
    /// ActiveUser holds the 32-bit account id of the running session and drops to 0 when Steam exits,
    /// so it is live evidence of who is signed in - independent of any ticket, which is exactly what
    /// makes it worth checking a ticket against.
    /// </remarks>
    private static string CurrentSteamId()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            object? value = key?.GetValue("ActiveUser");

            // A REG_DWORD arrives as a signed int, so an account id past 2^31 would read negative and
            // a "> 0" test would throw away a perfectly good account. Reinterpret instead of compare,
            // and accept the other storage types rather than reporting Steam as closed.
            uint accountId = value switch
            {
                int i => unchecked((uint)i),
                long l => unchecked((uint)l),
                string s when uint.TryParse(s, out uint parsed) => parsed,
                _ => 0,
            };

            return accountId == 0 ? "" : (SteamId64Base + accountId).ToString();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// A pasted code, reduced to what the store accepts: six alphanumerics, upper case.
    /// </summary>
    /// <remarks>
    /// Their client normalises identically before sending, so this is the format the store matches
    /// against - and it means a code pasted with dashes, spaces or trailing text still works instead
    /// of coming back "refused" for a reason the user cannot see.
    /// </remarks>
    private static string NormalizeCode(string? code) =>
        new(( code ?? "" ).Where(char.IsLetterOrDigit).Take(6).Select(char.ToUpperInvariant).ToArray());

    /// <summary>
    /// This machine's Windows MachineGuid, or "" when it cannot be read.
    /// </summary>
    /// <remarks>
    /// Read from the 64-bit view explicitly: this key is WOW64-redirected, so a 32-bit process would
    /// read a different GUID and every redeem would come back bound to the wrong machine. The shipped
    /// build is x64, but nothing in the code says it must stay that way.
    /// </remarks>
    private static string MachineGuid()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid")?.ToString()?.Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }
}
