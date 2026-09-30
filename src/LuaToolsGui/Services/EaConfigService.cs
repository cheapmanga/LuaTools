using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LuaToolsGui.Services;

/// <summary>What happened to one config file during <see cref="EaConfigService.ApplyToken"/>.</summary>
/// <param name="Path">Full path of the file.</param>
/// <param name="Changed">True when the file was rewritten.</param>
/// <param name="Replacements">How many values were replaced (0 when the file had nothing to replace).</param>
public record EaConfigFileResult(string Path, bool Changed, int Replacements);

/// <summary>
/// Writes a Denuvo token into the emulator config files of an installed EA game
/// (<c>anadius.cfg</c> and <c>token.ini</c>, anywhere under the game folder).
///
/// <para>
/// The shipped configs carry the literal <c>PASTE_A_VALID_DENUVO_TOKEN_HERE</c>; every occurrence of it
/// is replaced. A config that was already filled in has no placeholder left, so in that case the
/// quoted value on the token line(s) is overwritten instead: a non-comment line whose text before the
/// value mentions "token", where the last <c>"…"</c> on the line is the value.
/// </para>
/// <para>
/// Everything else in the file is kept byte-for-byte: line endings, encoding (BOM or not), other keys.
/// A file with nothing to replace is not rewritten at all.
/// </para>
/// </summary>
public class EaConfigService
{
    public const string Placeholder = "PASTE_A_VALID_DENUVO_TOKEN_HERE";

    /// <summary>The config file names the token goes into. Matched case-insensitively.</summary>
    public static readonly IReadOnlyList<string> ConfigFileNames = ["anadius.cfg", "token.ini"];

    // A token line's last quoted value: `"DenuvoToken" "abc"`, `Token = "abc"`, ... The key part
    // (everything before the value) must mention "token"; the value is captured for replacement.
    private static readonly Regex TokenLine = new(
        @"^(?<key>[^\r\n]*token[^\r\n]*?"")(?<value>[^""\r\n]*)(?<tail>""[^""\r\n]*)(?=\r?$)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Find the config files under <paramref name="gameFolder"/> (recursive). Never throws.</summary>
    public static IReadOnlyList<string> FindConfigFiles(string gameFolder)
    {
        try
        {
            if (!Directory.Exists(gameFolder)) return [];
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
            };
            return [.. ConfigFileNames
                .SelectMany(name => Directory.EnumerateFiles(gameFolder, name, options))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Put <paramref name="token"/> into every config file under <paramref name="gameFolder"/>.
    /// Returns one entry per file found (empty when the folder has none).
    /// </summary>
    public IReadOnlyList<EaConfigFileResult> ApplyToken(string gameFolder, string token)
    {
        ValidateToken(token);

        var results = new List<EaConfigFileResult>();
        foreach (var path in FindConfigFiles(gameFolder))
        {
            var (text, encoding) = ReadText(path);
            var (updated, count) = ReplaceToken(text, token);

            bool changed = count > 0 && updated != text;
            if (changed) File.WriteAllText(path, updated, encoding);
            results.Add(new EaConfigFileResult(path, changed, count));
        }
        return results;
    }

    /// <summary>
    /// The replacement itself, as a pure string→string so it can be tested without files. Returns the
    /// new text and how many values were replaced.
    /// </summary>
    public static (string Text, int Replacements) ReplaceToken(string text, string token)
    {
        ValidateToken(token);

        int placeholders = CountOccurrences(text, Placeholder);
        if (placeholders > 0)
            return (text.Replace(Placeholder, token, StringComparison.Ordinal), placeholders);

        int replaced = 0;
        string updated = TokenLine.Replace(text, m =>
        {
            if (IsComment(m.Groups["key"].Value)) return m.Value;
            replaced++;
            return m.Groups["key"].Value + token + m.Groups["tail"].Value;
        });
        return (updated, replaced);
    }

    private static void ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("The token is empty.", nameof(token));
        // The value is written between quotes on a single line; either would corrupt the file.
        if (token.IndexOfAny(['"', '\r', '\n']) >= 0)
            throw new ArgumentException("The token can't contain quotes or line breaks.", nameof(token));
    }

    private static bool IsComment(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("//") || t.StartsWith(';') || t.StartsWith('#');
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        for (int i = text.IndexOf(value, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>
    /// Read a file along with the encoding to write it back with. The reader starts from BOM-less UTF-8
    /// and only switches when it sees a BOM, so a file keeps (or keeps lacking) its BOM.
    /// </summary>
    private static (string Text, Encoding Encoding) ReadText(string path)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        string text = reader.ReadToEnd();
        return (text, reader.CurrentEncoding);
    }
}
