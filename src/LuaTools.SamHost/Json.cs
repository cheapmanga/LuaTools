using System.Globalization;
using System.Text;

namespace LuaTools.SamHost
{
    /// <summary>
    /// Minimal JSON writer. .NET Framework 4.8 has no System.Text.Json, and the host is deliberately
    /// dependency-free (see the csproj), so the handful of shapes the protocol emits are built by hand.
    /// Only writing is needed: commands arriving on stdin are plain text lines, not JSON.
    /// </summary>
    internal static class Json
    {
        /// <summary>
        /// Escape a string for a JSON value. Control characters matter here beyond correctness: the
        /// protocol is line-delimited, and achievement descriptions do contain newlines, so an
        /// unescaped one would split a response into two lines and desync the reader.
        /// </summary>
        public static string Escape(string value)
        {
            var sb = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>A quoted, escaped JSON string (or <c>null</c> for a null input).</summary>
        public static string Str(string value) => value == null ? "null" : "\"" + Escape(value) + "\"";

        public static string Bool(bool value) => value ? "true" : "false";

        public static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
