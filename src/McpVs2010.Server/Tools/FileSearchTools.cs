using System.ComponentModel;
using System.Text.Json;
using McpVs2010.Server;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpVs2010.Server.Tools;

[McpServerToolType]
public static class FileSearchTools
{
    [McpServerTool, Description("MCP 서버 프로세스가 읽을 수 있는 주요 파일 시스템 경로를 조회합니다. 드라이브 루트, 현재 폴더, 서버 폴더와 사용자 폴더를 반환합니다.")]
    public static CallToolResult list_accessible_paths()
    {
        try
        {
            var paths = new List<object>();
            string path = ServerSettings.ReadWorkingFolder();
            if (Directory.Exists(path))
            {
                try
                {
                    Directory.EnumerateFileSystemEntries(path).Take(1).ToArray();
                    paths.Add(new { path = Path.GetFullPath(path), accessible = true });
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                {
                    paths.Add(new { path = Path.GetFullPath(path), accessible = false });
                }
            }
            return Success(paths);
        }
        catch (Exception ex) { return Error(ex); }
    }

    [McpServerTool, Description("지정한 접근 가능 경로에서 .sln 솔루션 파일을 조회합니다.")]
    public static CallToolResult list_solutions_in_path(
        [Description("검색할 폴더 전체 경로입니다. 생략하면 MCP 서버 현재 폴더를 사용합니다.")] string? path = null,
        [Description("하위 폴더까지 검색할지 여부입니다.")] bool recursive = true,
        [Description("반환할 최대 파일 수입니다.")] int max_results = 1000)
    {
        return ListFiles(path, "*.sln", recursive, max_results);
    }

    [McpServerTool, Description("지정한 접근 가능 경로에서 Visual C++ .vcxproj 프로젝트 파일을 조회합니다.")]
    public static CallToolResult list_vcxprojects_in_path(
        [Description("검색할 폴더 전체 경로입니다. 생략하면 MCP 서버 현재 폴더를 사용합니다.")] string? path = null,
        [Description("하위 폴더까지 검색할지 여부입니다.")] bool recursive = true,
        [Description("반환할 최대 파일 수입니다.")] int max_results = 1000)
    {
        return ListFiles(path, "*.vcxproj", recursive, max_results);
    }

    private static CallToolResult ListFiles(string? path, string pattern, bool recursive, int maxResults)
    {
        try
        {
            if (maxResults < 1 || maxResults > 10000) throw new ArgumentOutOfRangeException(nameof(maxResults), "max_results는 1~10000이어야 합니다.");
            string root = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? ServerSettings.ReadWorkingFolder() : path);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("검색 폴더를 찾을 수 없습니다: " + root);
            if (!ServerSettings.IsInsideWorkingFolder(root))
                throw new UnauthorizedAccessException("Working folder 외부 경로는 검색할 수 없습니다.");
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            var files = Directory.EnumerateFiles(root, pattern, options)
                .Take(maxResults)
                .Select(file => new
                {
                    path = Path.GetFullPath(file),
                    name = Path.GetFileName(file),
                    directory = Path.GetDirectoryName(Path.GetFullPath(file)),
                    size = new FileInfo(file).Length,
                    lastWriteTime = File.GetLastWriteTimeUtc(file)
                })
                .ToList();
            return Success(new { root, pattern, recursive, count = files.Count, files });
        }
        catch (Exception ex) { return Error(ex); }
    }

    private static CallToolResult Success(object value) => new()
    {
        Content = new List<ContentBlock> { new TextContentBlock { Text = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) } }
    };

    private static CallToolResult Error(Exception ex) => new()
    {
        IsError = true,
        Content = new List<ContentBlock> { new TextContentBlock { Text = ex.Message } }
    };
}
