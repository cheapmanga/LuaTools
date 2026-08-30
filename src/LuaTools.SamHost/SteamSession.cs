using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using SAM.API;
using APITypes = SAM.API.Types;

namespace LuaTools.SamHost
{
    /// <summary>An achievement plus its current state for this account.</summary>
    internal sealed class AchievementState
    {
        public AchievementDefinition Definition;
        public bool IsAchieved;
        public uint UnlockTime;
    }

    /// <summary>Why an operation failed, in a form LuaTools can localize (see Program's protocol doc).</summary>
    internal sealed class SessionException : Exception
    {
        public readonly string Code;

        public SessionException(string code, string message)
            : base(message)
        {
            Code = code;
        }
    }

    /// <summary>
    /// One Steam connection, bound to one app id for its whole life (the app id is baked into the pipe
    /// at load time, which is exactly why LuaTools runs one host process per game).
    ///
    /// <para>
    /// Everything here must stay on a single thread: the Steam client interfaces are called through raw
    /// vtable pointers with no synchronization of their own. The host's main loop is that thread, and it
    /// blocks on stdin between commands, so there is nothing to guard against.
    /// </para>
    /// </summary>
    internal sealed class SteamSession : IDisposable
    {
        private readonly long _appId;
        private readonly Client _client = new Client();

        private bool _initialized;
        private int? _lastStatsResult;
        private List<AchievementDefinition> _definitions;

        public SteamSession(long appId)
        {
            _appId = appId;
        }

        /// <summary>Steam's install folder, from the registry. Also where the stats schema cache lives.</summary>
        public string SteamPath { get; private set; }

        /// <summary>
        /// Connect to the running Steam client. Throws <see cref="SessionException"/> with a stable code
        /// on every failure the user can actually do something about (Steam closed, wrong account, …).
        /// </summary>
        public void Initialize()
        {
            SteamPath = Steam.GetInstallPath();

            try
            {
                _client.Initialize(_appId);
            }
            catch (ClientInitializeException e)
            {
                throw new SessionException(CodeFor(e.Failure), e.Message);
            }

            if (_client.SteamUser.IsLoggedIn() == false)
            {
                throw new SessionException("not_logged_in", "Steam is running but no user is logged in.");
            }

            // Registered once, for the lifetime of the process: RequestUserStats answers through it.
            var callback = _client.CreateAndRegisterCallback<SAM.API.Callbacks.UserStatsReceived>();
            callback.OnRun += OnUserStatsReceived;

            _initialized = true;
        }

        /// <summary>
        /// Which of these app ids the signed-in account actually owns.
        ///
        /// <para>
        /// This is the only reliable answer to "is this game still in the library?". Files on disk are
        /// not: Steam keeps a game's cached stats schema long after the account stops owning it, so a
        /// game that was added and then removed still looks present on disk. Asking Steam removes those
        /// ghosts, and keeps games that are owned but not installed.
        /// </para>
        ///
        /// <para>Requires a session opened with app id 0 — a connection bound to one game answers for
        /// that game only.</para>
        /// </summary>
        public List<long> FilterOwned(IEnumerable<long> appIds)
        {
            EnsureInitialized();

            var owned = new List<long>();
            foreach (long appId in appIds)
            {
                if (appId <= 0 || appId > uint.MaxValue)
                {
                    continue;
                }

                if (_client.SteamApps008.IsSubscribedApp((uint)appId))
                {
                    owned.Add(appId);
                }
            }

            return owned;
        }

        /// <summary>
        /// The signed-in account's 64-bit Steam id. LuaTools needs it to find this user's folder under
        /// <c>userdata\</c>, which is where Steam records how long each game has been played.
        /// </summary>
        public ulong SteamId
        {
            get
            {
                EnsureInitialized();
                return _client.SteamUser.GetSteamId();
            }
        }

        /// <summary>Steam's current language for this game, used to pick localized achievement names.</summary>
        public string Language
        {
            get
            {
                try { return _client.SteamApps008.GetCurrentGameLanguage() ?? "english"; }
                catch (Exception) { return "english"; }
            }
        }

        /// <summary>
        /// Ask Steam for this account's stats and wait for the answer, then read the schema. This is the
        /// one blocking step: nothing else works until the stats have landed, because achievement state
        /// is only meaningful once Steam has filled it in.
        /// </summary>
        /// <param name="timeoutMs">
        /// How long to wait for the UserStatsReceived callback. Steam answers in well under a second when
        /// it is healthy; the timeout is there for the case where it never answers at all.
        /// </param>
        public List<AchievementState> RequestAchievements(int timeoutMs)
        {
            EnsureInitialized();

            _lastStatsResult = null;
            var handle = _client.SteamUserStats.RequestUserStats(_client.SteamUser.GetSteamId());
            if (handle == CallHandle.Invalid)
            {
                throw new SessionException("stats_request_failed", "Steam refused the stats request.");
            }

            var clock = Stopwatch.StartNew();
            while (_lastStatsResult.HasValue == false && clock.ElapsedMilliseconds < timeoutMs)
            {
                _client.RunCallbacks(false);
                Thread.Sleep(25);
            }

            if (_lastStatsResult.HasValue == false)
            {
                throw new SessionException("stats_timeout", "Steam did not answer the stats request in time.");
            }

            // 1 == k_EResultOK. Anything else is a Steam-side refusal (not owned, offline, …).
            if (_lastStatsResult.Value != 1)
            {
                throw new SessionException(
                    "stats_error",
                    "Steam returned error " + _lastStatsResult.Value + " while retrieving stats.");
            }

            // Read the schema only now: Steam writes/refreshes the cache file as part of answering, so
            // reading it earlier can miss a game whose stats were never fetched on this machine.
            _definitions = SchemaReader.Read(SteamPath, _appId, Language);
            if (_definitions == null)
            {
                throw new SessionException(
                    "no_schema",
                    "Steam has no achievement schema cached for this game.");
            }

            var result = new List<AchievementState>(_definitions.Count);
            foreach (var definition in _definitions)
            {
                bool isAchieved;
                uint unlockTime;
                if (_client.SteamUserStats.GetAchievementAndUnlockTime(
                        definition.Id, out isAchieved, out unlockTime) == false)
                {
                    // The schema lists it but this account's stats don't carry it (leftover from an
                    // older build of the game). Skipping matches SAM's behaviour.
                    continue;
                }

                result.Add(new AchievementState
                {
                    Definition = definition,
                    IsAchieved = isAchieved,
                    UnlockTime = isAchieved ? unlockTime : 0,
                });
            }

            return result;
        }

        /// <summary>
        /// Stage one achievement's state. Nothing reaches Steam's servers until <see cref="Store"/>,
        /// which is what makes "toggle a few, then save" possible.
        /// </summary>
        public void Set(string id, bool achieved)
        {
            EnsureInitialized();

            var definition = FindDefinition(id);
            if (definition == null)
            {
                throw new SessionException("unknown_achievement", "No achievement with id '" + id + "'.");
            }

            if (definition.IsProtected)
            {
                throw new SessionException(
                    "protected_achievement",
                    "'" + id + "' is server-awarded and cannot be changed.");
            }

            if (_client.SteamUserStats.SetAchievement(id, achieved) == false)
            {
                throw new SessionException("set_failed", "Steam rejected the change to '" + id + "'.");
            }
        }

        /// <summary>
        /// Stage every non-protected achievement at once (unlock all / lock all). Returns how many were
        /// staged. Doing this host-side keeps it one command instead of hundreds of round trips.
        /// </summary>
        public int SetAll(bool achieved)
        {
            EnsureInitialized();

            if (_definitions == null)
            {
                throw new SessionException("not_loaded", "Load the achievement list first.");
            }

            int staged = 0;
            foreach (var definition in _definitions)
            {
                if (definition.IsProtected)
                {
                    continue; // silently skipped: the UI already shows these as read-only
                }

                if (_client.SteamUserStats.SetAchievement(definition.Id, achieved) == false)
                {
                    throw new SessionException(
                        "set_failed", "Steam rejected the change to '" + definition.Id + "'.");
                }

                staged++;
            }

            return staged;
        }

        /// <summary>Commit every staged change to Steam. This is the point of no return.</summary>
        public void Store()
        {
            EnsureInitialized();

            if (_client.SteamUserStats.StoreStats() == false)
            {
                throw new SessionException("store_failed", "Steam rejected the store request.");
            }

            // Let the store callback come back so Steam has actually processed it before the caller
            // reloads the list (otherwise a reload can still show the pre-store state).
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 500)
            {
                _client.RunCallbacks(false);
                Thread.Sleep(25);
            }
        }

        /// <summary>
        /// Wipe this game's progress for the account. Steam's only reset call always clears the
        /// integer/float stats; <paramref name="achievementsToo"/> extends it to achievements. There is
        /// no achievements-only reset, which is why LuaTools spells that out before asking.
        ///
        /// <para>
        /// Destructive and immediate: unlike Set, this does not wait for a Store. Steam commits it.
        /// </para>
        /// </summary>
        public void Reset(bool achievementsToo)
        {
            EnsureInitialized();

            if (_client.SteamUserStats.ResetAllStats(achievementsToo) == false)
            {
                throw new SessionException("reset_failed", "Steam rejected the reset request.");
            }
        }

        private AchievementDefinition FindDefinition(string id)
        {
            if (_definitions == null)
            {
                return null;
            }

            foreach (var definition in _definitions)
            {
                if (string.Equals(definition.Id, id, StringComparison.Ordinal))
                {
                    return definition;
                }
            }

            return null;
        }

        private void OnUserStatsReceived(APITypes.UserStatsReceived param)
        {
            _lastStatsResult = param.Result;
        }

        private void EnsureInitialized()
        {
            if (_initialized == false)
            {
                throw new SessionException("not_initialized", "Not connected to Steam.");
            }
        }

        /// <summary>Map SAM's init failure to a code LuaTools can turn into a localized message.</summary>
        private static string CodeFor(ClientInitializeFailure failure)
        {
            switch (failure)
            {
                case ClientInitializeFailure.GetInstallPath:
                    return "steam_not_found";
                case ClientInitializeFailure.AppIdMismatch:
                    return "appid_mismatch";
                case ClientInitializeFailure.Load:
                case ClientInitializeFailure.CreateSteamClient:
                case ClientInitializeFailure.CreateSteamPipe:
                case ClientInitializeFailure.ConnectToGlobalUser:
                    // All four mean the same thing in practice: no usable Steam client to talk to.
                    return "steam_not_running";
                default:
                    return "steam_unavailable";
            }
        }

        public void Dispose()
        {
            _client.Dispose();
        }
    }
}
