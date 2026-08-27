using System.Text.Json;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace McpVs2010.Server.Bridge;

[SupportedOSPlatform("windows")]
internal static class Vs2010RecentProjectsReader
{
    private const string RegistryPath = @"Software\Microsoft\VisualStudio\10.0\ProjectMRUList";

    public static string ReadAsJson()
    {
        return JsonSerializer.Serialize(
            new RecentProjectsResult
            {
                RegistryView = "Registry32",
                RegistryPath = @"HKCU\" + RegistryPath,
                Items = ReadItems()
            },
            new JsonSerializerOptions { WriteIndented = true });
    }

    public static string GetSolutionPath(int position)
    {
        if (position < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(position),
                position,
                "최근 솔루션 순번은 1 이상이어야 합니다.");
        }

        var item = ReadItems().Find(candidate => candidate.Position == position)
                   ?? throw new InvalidOperationException($"VS2010 최근 목록 {position}번을 찾을 수 없습니다.");
        if (!string.Equals(item.Type, "solution", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"VS2010 최근 목록 {position}번은 솔루션 파일이 아닙니다: {item.Path}");
        }
        if (!item.Exists)
        {
            throw new FileNotFoundException(
                $"VS2010 최근 목록 {position}번 솔루션 파일이 존재하지 않습니다.",
                item.Path);
        }

        return item.Path;
    }

    private static List<RecentProjectInfo> ReadItems()
    {
        var items = new List<RecentProjectInfo>();
        using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32);
        using var recentProjects = currentUser.OpenSubKey(RegistryPath, writable: false);
        if (recentProjects is not null)
        {
            foreach (var valueName in recentProjects.GetValueNames()
                         .Select(name => new { Name = name, Position = ParsePosition(name) })
                         .Where(item => item.Position > 0)
                         .OrderBy(item => item.Position))
            {
                var rawValue = recentProjects.GetValue(
                    valueName.Name,
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                var path = ParsePath(rawValue);
                if (path is null)
                {
                    continue;
                }

                items.Add(new RecentProjectInfo
                {
                    Position = valueName.Position,
                    Type = GetItemType(path),
                    Path = path,
                    Exists = File.Exists(path)
                });
            }
        }

        return items;
    }

    private static int ParsePosition(string valueName)
    {
        return valueName.StartsWith("File", StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(valueName.AsSpan(4), out var position)
            ? position
            : -1;
    }

    private static string? ParsePath(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var separator = rawValue.IndexOf('|');
        var path = (separator < 0 ? rawValue : rawValue[..separator]).Trim().Trim('"');
        return path.Length == 0 ? null : Environment.ExpandEnvironmentVariables(path);
    }

    private static string GetItemType(string path)
    {
        return string.Equals(Path.GetExtension(path), ".sln", StringComparison.OrdinalIgnoreCase)
            ? "solution"
            : "project";
    }

    private sealed class RecentProjectsResult
    {
        public string RegistryView { get; set; } = string.Empty;

        public string RegistryPath { get; set; } = string.Empty;

        public List<RecentProjectInfo> Items { get; set; } = [];
    }

    private sealed class RecentProjectInfo
    {
        public int Position { get; set; }

        public string Type { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public bool Exists { get; set; }
    }
}
