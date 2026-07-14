using System.Globalization;
using System.Text;
using Glow.Shared;

namespace Glow.Client;

// REPL tokenizer + PropertyValue parser. Rules:
//   * whitespace separates tokens
//   * "..." keeps spaces, supports \" and \\ escapes
//   * quoted -> String; null -> Null; true/false -> Bool
//   * unquoted digits -> Int (or Long if too big); dot/e -> Double
//   * otherwise -> String (unquoted)
//   * 0x-prefix hex -> Bytes
public static class ValueParser
{
    public static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var quotedFlags = new List<bool>();
        var i = 0;
        while (i < line.Length)
        {
            if (char.IsWhiteSpace(line[i])) { i++; continue; }
            if (line[i] == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < line.Length && line[i] != '"')
                {
                    if (line[i] == '\\' && i + 1 < line.Length) { sb.Append(line[i + 1]); i += 2; }
                    else { sb.Append(line[i]); i++; }
                }
                if (i < line.Length) i++;
                tokens.Add(sb.ToString());
            }
            else
            {
                var start = i;
                while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
                tokens.Add(line[start..i]);
            }
        }
        return tokens;
    }

    public static PropertyValue ParseValue(string token)
    {
        if (token.Length >= 2 && token[0] == '"' && token[^1] == '"')
            return PropertyValue.From(token[1..^1]);
        if (token.Equals("null", StringComparison.OrdinalIgnoreCase)) return PropertyValue.Null;
        if (token.Equals("true", StringComparison.OrdinalIgnoreCase)) return PropertyValue.From(true);
        if (token.Equals("false", StringComparison.OrdinalIgnoreCase)) return PropertyValue.From(false);
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            try { return PropertyValue.From(Convert.FromHexString(token[2..])); }
            catch { return PropertyValue.From(token); }
        }
        if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            return PropertyValue.From(i);
        if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            return PropertyValue.From(l);
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) &&
            (token.Contains('.') || token.Contains('e', StringComparison.OrdinalIgnoreCase)))
            return PropertyValue.From(d);
        return PropertyValue.From(token);
    }
}
