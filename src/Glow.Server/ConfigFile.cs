using System.Text;

namespace Glow.Server;

// Minimal INI reader / writer for the boot config file. The file is a
// flat list of `key = value` pairs; `#` or `;` start a comment; blank
// lines are ignored. Keys are case-sensitive and match the CLI long
// names (kebab-case). CLI flags override values read from here.
public static class ConfigFile
{
    public static Dictionary<string, string> Read(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return map;
    }

    // Writes each entry as `# <comment>\n<key> = <value>\n\n`. Comments
    // may be multi-line; every line is prefixed with `# `.
    public static void Write(string path, IReadOnlyList<(string Key, string Value, string Comment)> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Glow server config -- created on first run.");
        sb.AppendLine("# CLI flags always override values here.");
        sb.AppendLine("# Delete this file to regenerate defaults.");
        sb.AppendLine();
        foreach (var (k, v, c) in entries)
        {
            if (!string.IsNullOrEmpty(c))
                foreach (var line in c.Split('\n'))
                    sb.AppendLine($"# {line}");
            sb.AppendLine($"{k} = {v}");
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString());
    }
}
