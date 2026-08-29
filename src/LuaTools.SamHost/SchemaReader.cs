using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SAM.Game;
using APITypes = SAM.API.Types;

namespace LuaTools.SamHost
{
    /// <summary>One achievement as declared by the game's stats schema.</summary>
    internal sealed class AchievementDefinition
    {
        public string Id;
        public string Name;
        public string Description;
        public string IconNormal;
        public string IconLocked;
        public bool IsHidden;
        public int Permission;

        /// <summary>
        /// True when Steam refuses to let a client change this achievement (server-awarded). Setting
        /// one silently does nothing, so LuaTools shows it read-only. Mirrors SAM's own check.
        /// </summary>
        public bool IsProtected => (Permission & 3) != 0;
    }

    /// <summary>
    /// Reads Steam's cached achievement schema, <c>appcache\stats\UserGameStatsSchema_&lt;appid&gt;.bin</c>
    /// (Valve binary KeyValues). This is the same source SAM parses, and it is what supplies the names,
    /// descriptions and icon filenames the Steam API itself won't hand back in bulk.
    ///
    /// <para>
    /// The file is written by the Steam client when it fetches a game's stats, so it exists only for
    /// games the account has actually loaded stats for. A missing file is normal, not an error: it just
    /// means "ask Steam for the stats first".
    /// </para>
    /// </summary>
    internal static class SchemaReader
    {
        public static string StatsDirectory(string steamPath) =>
            Path.Combine(steamPath, "appcache", "stats");

        public static string SchemaPath(string steamPath, long appId) =>
            Path.Combine(StatsDirectory(steamPath), "UserGameStatsSchema_" +
                appId.ToString(CultureInfo.InvariantCulture) + ".bin");

        /// <summary>
        /// Parse the achievement definitions for one app. Returns null when the schema file is absent
        /// or unreadable; an empty list means the game genuinely declares no achievements.
        /// </summary>
        /// <param name="language">
        /// Steam language name ("english", "french"…) used to pick the localized display strings.
        /// Falls back to English, then to the raw value, then to the achievement id.
        /// </param>
        public static List<AchievementDefinition> Read(string steamPath, long appId, string language)
        {
            string path = SchemaPath(steamPath, appId);
            if (File.Exists(path) == false)
            {
                return null;
            }

            KeyValue kv;
            try
            {
                kv = KeyValue.LoadAsBinary(path);
            }
            catch (Exception)
            {
                return null; // truncated / mid-write / not the format we expect
            }

            if (kv == null)
            {
                return null;
            }

            var stats = kv[appId.ToString(CultureInfo.InvariantCulture)]["stats"];
            if (stats.Valid == false || stats.Children == null)
            {
                return null;
            }

            var definitions = new List<AchievementDefinition>();
            foreach (var stat in stats.Children)
            {
                if (stat.Valid == false || stat.Children == null)
                {
                    continue;
                }

                var type = TypeOf(stat);
                if (type != APITypes.UserStatType.Achievements &&
                    type != APITypes.UserStatType.GroupAchievements)
                {
                    continue; // integer/float stats: not our business here
                }

                // Achievements hang off a "bits" node (a group can carry several).
                foreach (var bits in stat.Children.Where(b =>
                    string.Compare(b.Name, "bits", StringComparison.InvariantCultureIgnoreCase) == 0))
                {
                    if (bits.Valid == false || bits.Children == null)
                    {
                        continue;
                    }

                    foreach (var bit in bits.Children)
                    {
                        string id = bit["name"].AsString("");
                        if (string.IsNullOrEmpty(id))
                        {
                            continue;
                        }

                        definitions.Add(new AchievementDefinition
                        {
                            Id = id,
                            Name = Localized(bit["display"]["name"], language, id),
                            Description = Localized(bit["display"]["desc"], language, ""),
                            IconNormal = bit["display"]["icon"].AsString(""),
                            IconLocked = bit["display"]["icon_gray"].AsString(""),
                            IsHidden = bit["display"]["hidden"].AsBoolean(false),
                            Permission = bit["permission"].AsInteger(0),
                        });
                    }
                }
            }

            return definitions;
        }

        /// <summary>
        /// Achievement count per app for every schema Steam has cached, for the game grid. One pass over
        /// appcache\stats, no Steam connection needed — which is the whole point: the grid can show real
        /// counts without spawning a host (and therefore a Steam pipe) per game.
        /// </summary>
        public static IEnumerable<KeyValuePair<long, int>> ScanCounts(string steamPath)
        {
            string dir = StatsDirectory(steamPath);
            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "UserGameStatsSchema_*.bin");
            }
            catch (Exception)
            {
                yield break;
            }

            foreach (string file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file) ?? "";
                string idPart = name.Substring(name.LastIndexOf('_') + 1);
                if (long.TryParse(idPart, NumberStyles.None, CultureInfo.InvariantCulture, out long appId) == false)
                {
                    continue;
                }

                // English is fine here: only the count is read, and it doesn't vary by language.
                var definitions = Read(steamPath, appId, "english");
                if (definitions != null && definitions.Count > 0)
                {
                    yield return new KeyValuePair<long, int>(appId, definitions.Count);
                }
            }
        }

        /// <summary>
        /// The stat's type, tolerating both schema formats: newer files carry a string "type"
        /// ("achievements"), older ones an integer "type_int" / "type". Ported from SAM.
        /// </summary>
        private static APITypes.UserStatType TypeOf(KeyValue stat)
        {
            var typeNode = stat["type"];
            if (typeNode.Valid == true && typeNode.Type == KeyValueType.String)
            {
                APITypes.UserStatType parsed;
                if (Enum.TryParse((string)typeNode.Value, true, out parsed) == true)
                {
                    return parsed;
                }
            }

            var typeIntNode = stat["type_int"];
            int raw = typeIntNode.Valid == true ? typeIntNode.AsInteger(0) : typeNode.AsInteger(0);
            return (APITypes.UserStatType)raw;
        }

        /// <summary>
        /// Pick a display string for the current language, falling back to English, then to the node's
        /// own value, then to <paramref name="defaultValue"/>. Ported from SAM's GetLocalizedString.
        /// </summary>
        private static string Localized(KeyValue kv, string language, string defaultValue)
        {
            string name = kv[language].AsString("");
            if (string.IsNullOrEmpty(name) == false)
            {
                return name;
            }

            if (language != "english")
            {
                name = kv["english"].AsString("");
                if (string.IsNullOrEmpty(name) == false)
                {
                    return name;
                }
            }

            name = kv.AsString("");
            return string.IsNullOrEmpty(name) == false ? name : defaultValue;
        }
    }
}
