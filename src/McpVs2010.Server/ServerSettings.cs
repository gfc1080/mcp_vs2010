using Microsoft.Win32;

namespace McpVs2010.Server;

internal static class ServerSettings
{
    private const string RegistryPath = @"Software\McpVs2010";

    public static string ReadWorkingFolder()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        string? value = key?.GetValue("WorkingFolder") as string;
        if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
            return Path.GetFullPath(value);
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(documents) && Directory.Exists(documents))
        {
            try
            {
                Directory.EnumerateFileSystemEntries(documents).Take(1).ToArray();
                return Path.GetFullPath(documents);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
            }
        }
        throw new DirectoryNotFoundException("접근 가능한 Working folder가 없습니다. MCP Config에서 유효한 폴더를 지정하십시오.");
    }

    public static string ReadWorkingFolderOrEmpty()
    {
        try { return ReadWorkingFolder(); }
        catch (DirectoryNotFoundException) { return string.Empty; }
    }

    public static void WriteWorkingFolder(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException("Working folder does not exist: " + fullPath);
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath, true)!;
        key.SetValue("WorkingFolder", fullPath, RegistryValueKind.String);
    }

    public static bool IsInsideWorkingFolder(string path)
    {
        string root = EnsureTrailingSeparator(Path.GetFullPath(ReadWorkingFolder()));
        string candidate = Path.GetFullPath(path);
        return candidate.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}
