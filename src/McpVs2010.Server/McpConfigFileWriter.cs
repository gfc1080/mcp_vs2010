using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpVs2010.Server;

internal static class McpConfigFileWriter
{
    public static bool IsClaudeRegistered(int port)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string path = string.IsNullOrWhiteSpace(profile) ? string.Empty : Path.Combine(profile, ".claude.json");
        if (!File.Exists(path)) return false;
        try
        {
            JsonObject? root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            string expected = $"http://127.0.0.1:{port}/stream";
            return root?["mcpServers"]?["vs2010"]?["url"]?.GetValue<string>() == expected;
        }
        catch { return false; }
    }

    public static bool IsCodexRegistered(int port)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string path = string.IsNullOrWhiteSpace(profile) ? string.Empty : Path.Combine(profile, ".codex", "config.toml");
        if (!File.Exists(path)) return false;
        string[] lines;
        try { lines = File.ReadAllLines(path); } catch { return false; }
        int section = Array.FindIndex(lines, line => line.Trim().Equals("[mcp_servers.vs2010]", StringComparison.Ordinal));
        if (section < 0) return false;
        string expected = $"http://127.0.0.1:{port}/stream";
        for (int index = section + 1; index < lines.Length && !lines[index].TrimStart().StartsWith("["); index++)
        {
            string line = lines[index].Trim();
            if (line.StartsWith("url", StringComparison.OrdinalIgnoreCase))
                return line.Contains(expected, StringComparison.Ordinal);
        }
        return false;
    }

    public static void EnsureClaudeRegistered(int port)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile)) return;
        string path = Path.Combine(profile, ".claude.json");
        if (!File.Exists(path)) return;
        JsonObject root;
        string text = File.ReadAllText(path);
        JsonNode? parsed = JsonNode.Parse(text);
        root = parsed as JsonObject
            ?? throw new InvalidDataException(".claude.json의 최상위 값은 JSON object여야 합니다: " + path);
        WriteServerEntry(root, path, port);
    }

    public static void EnsureCodexRegistered(int port)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile)) return;
        string path = Path.Combine(profile, ".codex", "config.toml");
        if (!File.Exists(path)) return;

        string newline = "\r\n";
        string text = File.ReadAllText(path);
        string normalized = text.Replace("\r\n", "\n");
        List<string> lines = normalized.Split('\n').ToList();
        const string section = "[mcp_servers.vs2010]";
        int sectionIndex = lines.FindIndex(line => line.Trim().Equals(section, StringComparison.Ordinal));
        string urlLine = $"url = \"http://127.0.0.1:{port}/stream\"";
        if (sectionIndex < 0)
        {
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
            lines.Add(string.Empty);
            lines.Add(section);
            lines.Add(urlLine);
            lines.Add("startup_timeout_sec = 20");
            lines.Add("tool_timeout_sec = 3600");
        }
        else
        {
            int end = sectionIndex + 1;
            while (end < lines.Count && !lines[end].TrimStart().StartsWith("[", StringComparison.Ordinal)) end++;
            int urlIndex = -1;
            for (int index = sectionIndex + 1; index < end; index++)
            {
                if (lines[index].TrimStart().StartsWith("url", StringComparison.OrdinalIgnoreCase) &&
                    lines[index].Contains("=", StringComparison.Ordinal))
                {
                    urlIndex = index;
                    break;
                }
            }
            if (urlIndex >= 0) lines[urlIndex] = urlLine;
            else lines.Insert(sectionIndex + 1, urlLine);
        }
        File.WriteAllText(path, string.Join(newline, lines));
    }

    private static void WriteServerEntry(JsonObject root, string path, int port)
    {
        JsonObject servers;
        if (root["mcpServers"] == null)
        {
            servers = new JsonObject();
            root["mcpServers"] = servers;
        }
        else
        {
            servers = root["mcpServers"] as JsonObject
                ?? throw new InvalidDataException(".claude.json의 mcpServers는 JSON object여야 합니다: " + path);
        }

        string url = $"http://127.0.0.1:{port}/stream";
        servers["vs2010"] = new JsonObject
        {
            ["type"] = "streamable-http",
            ["url"] = url
        };
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
