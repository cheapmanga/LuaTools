using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LuaTools.SamHost
{
    /// <summary>
    /// Headless achievement host for LuaTools. Speaks a tiny line protocol so the UI stays in the
    /// main app: LuaTools spawns one host per opened game, drives it, and kills it on close.
    ///
    /// <para><b>Usage</b></para>
    /// <list type="bullet">
    ///   <item><c>LuaTools.SamHost.exe &lt;appid&gt;</c> — interactive session for one game.</item>
    ///   <item><c>LuaTools.SamHost.exe --schemas</c> — one shot: achievement counts for every schema
    ///   Steam has cached. Touches no Steam connection, so the game grid can use it freely.</item>
    ///   <item><c>LuaTools.SamHost.exe --owned</c> — one shot: reads app ids from stdin, one per line,
    ///   and returns those the signed-in account owns. Needs Steam running.</item>
    /// </list>
    ///
    /// <para><b>Protocol</b></para>
    /// One command per line on stdin, one JSON object per line on stdout (UTF-8, never anything else:
    /// diagnostics go to stderr). A session opens with a single line, either
    /// <c>{"ok":true,"event":"ready",…}</c> or <c>{"ok":false,…}</c>; after that it is strictly
    /// request/response, in order.
    ///
    /// <list type="table">
    ///   <item><term>list</term><description>Ask Steam for this account's stats, then return every
    ///   achievement with its state. Must be run before set/setall.</description></item>
    ///   <item><term>set 0|1 &lt;id&gt;</term><description>Stage one achievement (id may contain
    ///   spaces: it is the rest of the line).</description></item>
    ///   <item><term>setall 0|1</term><description>Stage every non-protected achievement.</description></item>
    ///   <item><term>store</term><description>Commit staged changes to Steam.</description></item>
    ///   <item><term>reset 0|1</term><description>Reset this game's stats; 1 also resets achievements.
    ///   Immediate, no store needed.</description></item>
    ///   <item><term>ping</term><description>Liveness check.</description></item>
    ///   <item><term>quit</term><description>Exit. Closing stdin does the same, which is how the host
    ///   cleans itself up if LuaTools dies.</description></item>
    /// </list>
    ///
    /// <para>
    /// Failures answer <c>{"ok":false,"code":"…","error":"…"}</c>. The code is stable and meant to be
    /// mapped to a localized message by LuaTools; the message is English and only a fallback.
    /// </para>
    /// </summary>
    internal static class Program
    {
        /// <summary>How long to wait for Steam to answer a stats request. It normally takes well under
        /// a second; this only bounds the case where the answer never comes.</summary>
        private const int StatsTimeoutMs = 15000;

        private static StreamWriter _out;

        private static int Main(string[] args)
        {
            // Own the streams rather than going through Console: setting Console.InputEncoding throws
            // when stdin is a pipe, which is exactly how this process is always run.
            var utf8 = new UTF8Encoding(false);
            _out = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
            var input = new StreamReader(Console.OpenStandardInput(), utf8);

            if (args.Length == 1 && args[0] == "--schemas")
            {
                return ScanSchemas();
            }

            if (args.Length == 1 && args[0] == "--owned")
            {
                return FilterOwned(input);
            }

            long appId;
            if (args.Length != 1 ||
                long.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out appId) == false ||
                appId <= 0)
            {
                WriteError("bad_args", "Usage: LuaTools.SamHost.exe <appid> | --schemas");
                return 2;
            }

            using (var session = new SteamSession(appId))
            {
                try
                {
                    session.Initialize();
                }
                catch (SessionException e)
                {
                    WriteError(e.Code, e.Message);
                    return 1;
                }
                catch (Exception e)
                {
                    WriteError("steam_unavailable", e.Message);
                    return 1;
                }

                Write("{\"ok\":true,\"event\":\"ready\",\"appid\":" + Json.Num(appId) +
                      ",\"language\":" + Json.Str(session.Language) + "}");

                string line;
                while ((line = input.ReadLine()) != null)
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    if (line == "quit")
                    {
                        break;
                    }

                    try
                    {
                        Dispatch(session, line);
                    }
                    catch (SessionException e)
                    {
                        WriteError(e.Code, e.Message);
                    }
                    catch (Exception e)
                    {
                        // Never let one bad command take the session down: LuaTools would have to
                        // respawn and the user would lose their staged changes.
                        WriteError("internal_error", e.Message);
                    }
                }
            }

            return 0;
        }

        private static void Dispatch(SteamSession session, string line)
        {
            string command = line;
            string rest = "";
            int space = line.IndexOf(' ');
            if (space >= 0)
            {
                command = line.Substring(0, space);
                rest = line.Substring(space + 1).Trim();
            }

            switch (command)
            {
                case "ping":
                    Write("{\"ok\":true}");
                    break;

                case "list":
                    WriteAchievements(session);
                    break;

                case "set":
                {
                    // "set <0|1> <id>": the flag comes first so the id can be the rest of the line
                    // verbatim (Steam allows spaces in achievement ids).
                    int idSpace = rest.IndexOf(' ');
                    if (idSpace < 0)
                    {
                        throw new SessionException("bad_args", "Usage: set 0|1 <id>");
                    }

                    bool achieved = ParseFlag(rest.Substring(0, idSpace));
                    string id = rest.Substring(idSpace + 1);
                    session.Set(id, achieved);
                    Write("{\"ok\":true}");
                    break;
                }

                case "setall":
                {
                    int staged = session.SetAll(ParseFlag(rest));
                    Write("{\"ok\":true,\"staged\":" + Json.Num(staged) + "}");
                    break;
                }

                case "store":
                    session.Store();
                    Write("{\"ok\":true}");
                    break;

                case "reset":
                    session.Reset(ParseFlag(rest));
                    Write("{\"ok\":true}");
                    break;

                default:
                    throw new SessionException("unknown_command", "Unknown command '" + command + "'.");
            }
        }

        private static bool ParseFlag(string value)
        {
            switch (value)
            {
                case "1": return true;
                case "0": return false;
                default: throw new SessionException("bad_args", "Expected 0 or 1, got '" + value + "'.");
            }
        }

        private static void WriteAchievements(SteamSession session)
        {
            var achievements = session.RequestAchievements(StatsTimeoutMs);

            var sb = new StringBuilder(256 + achievements.Count * 192);
            sb.Append("{\"ok\":true,\"language\":").Append(Json.Str(session.Language));
            sb.Append(",\"achievements\":[");

            for (int i = 0; i < achievements.Count; i++)
            {
                var state = achievements[i];
                var definition = state.Definition;

                // A name still carrying its "#TOKEN" form was never localized by the game; the id is
                // more useful to the user than the token. Same fallback SAM applies.
                string name = definition.Name;
                if (string.IsNullOrEmpty(name) || name[0] == '#')
                {
                    name = definition.Id;
                }

                // Steam only ships a "gray" icon for some games; fall back to the normal one so the
                // list never renders a hole.
                string iconLocked = string.IsNullOrEmpty(definition.IconLocked)
                    ? definition.IconNormal
                    : definition.IconLocked;

                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append("{\"id\":").Append(Json.Str(definition.Id));
                sb.Append(",\"name\":").Append(Json.Str(name));
                sb.Append(",\"desc\":").Append(Json.Str(definition.Description ?? ""));
                sb.Append(",\"hidden\":").Append(Json.Bool(definition.IsHidden));
                sb.Append(",\"protected\":").Append(Json.Bool(definition.IsProtected));
                sb.Append(",\"achieved\":").Append(Json.Bool(state.IsAchieved));
                sb.Append(",\"unlockTime\":").Append(Json.Num(state.UnlockTime));
                sb.Append(",\"icon\":").Append(Json.Str(definition.IconNormal ?? ""));
                sb.Append(",\"iconLocked\":").Append(Json.Str(iconLocked ?? ""));
                sb.Append('}');
            }

            sb.Append("]}");
            Write(sb.ToString());
        }

        /// <summary>
        /// One shot: read app ids from stdin (one per line) and return those the account owns.
        ///
        /// <para>
        /// Opened with app id 0 on purpose: a session bound to a game can only answer for that game,
        /// while an unbound one can be asked about any app id. That is what lets the game grid drop
        /// entries the account no longer owns — Steam's own files can't tell you that.
        /// </para>
        /// </summary>
        private static int FilterOwned(StreamReader input)
        {
            var appIds = new List<long>();
            string line;
            while ((line = input.ReadLine()) != null)
            {
                long appId;
                if (long.TryParse(line.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out appId))
                {
                    appIds.Add(appId);
                }
            }

            using (var session = new SteamSession(0))
            {
                try
                {
                    session.Initialize();
                }
                catch (SessionException e)
                {
                    WriteError(e.Code, e.Message);
                    return 1;
                }
                catch (Exception e)
                {
                    WriteError("steam_unavailable", e.Message);
                    return 1;
                }

                var owned = session.FilterOwned(appIds);

                var sb = new StringBuilder(32 + owned.Count * 10);
                sb.Append("{\"ok\":true,\"owned\":[");
                for (int i = 0; i < owned.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    sb.Append(Json.Num(owned[i]));
                }
                sb.Append("]}");

                Write(sb.ToString());
                return 0;
            }
        }

        /// <summary>
        /// One-shot scan of Steam's schema cache: appid → achievement count, for the game grid. No Steam
        /// connection, no app id binding, so it can cover the whole library in a single process.
        /// </summary>
        private static int ScanSchemas()
        {
            string steamPath = SAM.API.Steam.GetInstallPath();
            if (string.IsNullOrEmpty(steamPath))
            {
                WriteError("steam_not_found", "Steam install path not found in the registry.");
                return 1;
            }

            var counts = new List<KeyValuePair<long, int>>(SchemaReader.ScanCounts(steamPath));

            var sb = new StringBuilder(64 + counts.Count * 32);
            sb.Append("{\"ok\":true,\"schemas\":[");
            for (int i = 0; i < counts.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append("{\"appid\":").Append(Json.Num(counts[i].Key));
                sb.Append(",\"count\":").Append(Json.Num(counts[i].Value)).Append('}');
            }
            sb.Append("]}");

            Write(sb.ToString());
            return 0;
        }

        private static void WriteError(string code, string message)
        {
            Write("{\"ok\":false,\"code\":" + Json.Str(code) + ",\"error\":" + Json.Str(message ?? "") + "}");
        }

        private static void Write(string line)
        {
            _out.Write(line);
            _out.Write('\n'); // '\n' only: the reader is LuaTools, and a stray '\r' is noise
        }
    }
}
