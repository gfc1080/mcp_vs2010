[CmdletBinding()]
param(
    [string]$ServerPath,

    [string]$VsixPath
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent

if ([string]::IsNullOrWhiteSpace($ServerPath)) {
    $serverProjectPath = Join-Path $projectRoot 'src\McpVs2010.Server\McpVs2010.Server.csproj'
    [xml]$serverProject = Get-Content -LiteralPath $serverProjectPath -Raw
    $serverVersionNode = $serverProject.SelectSingleNode(
        '/*[local-name()="Project"]/*[local-name()="PropertyGroup"]/*[local-name()="Version"]')
    if ($null -eq $serverVersionNode -or [string]::IsNullOrWhiteSpace($serverVersionNode.InnerText)) {
        throw "MCP 서버 프로젝트에서 버전을 읽을 수 없습니다: $serverProjectPath"
    }

    $server = Join-Path $projectRoot (
        'artifacts\server-{0}\McpVs2010.Server.exe' -f $serverVersionNode.InnerText.Trim())
}
else {
    $server = [System.IO.Path]::GetFullPath($ServerPath)
}

if ([string]::IsNullOrWhiteSpace($VsixPath)) {
    $manifestPath = Join-Path $projectRoot 'src\McpVs2010.Bridge\Vsix\extension.vsixmanifest'
    [xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
    $versionNode = $manifest.SelectSingleNode(
        '/*[local-name()="Vsix"]/*[local-name()="Identifier"]/*[local-name()="Version"]')
    if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
        throw "VSIX 매니페스트에서 버전을 읽을 수 없습니다: $manifestPath"
    }

    $vsix = Join-Path $projectRoot (
        'artifacts\McpVs2010.Bridge-{0}.vsix' -f $versionNode.InnerText.Trim())
}
else {
    $vsix = [System.IO.Path]::GetFullPath($VsixPath)
}

if (-not (Test-Path -LiteralPath $server)) {
    throw "MCP 서버 산출물이 없습니다: $server"
}
if (-not (Test-Path -LiteralPath $vsix)) {
    throw "VSIX 산출물이 없습니다: $vsix"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($vsix)
try {
    $requiredEntries = @(
        '[Content_Types].xml',
        'extension.vsixmanifest',
        'McpVs2010.Bridge.pkgdef',
        'McpVs2010.Bridge.dll'
    )
    $entryNames = @($archive.Entries | ForEach-Object FullName)
    foreach ($entry in $requiredEntries) {
        if ($entryNames -notcontains $entry) {
            throw "VSIX 필수 항목이 없습니다: $entry"
        }
    }

    $manifestEntry = $archive.GetEntry('extension.vsixmanifest')
    $manifestStream = $manifestEntry.Open()
    try {
        $manifestReader = [System.IO.StreamReader]::new($manifestStream)
        try {
            [xml]$packageManifest = $manifestReader.ReadToEnd()
        }
        finally {
            $manifestReader.Dispose()
        }
    }
    finally {
        $manifestStream.Dispose()
    }

    $packageIdentifier = $packageManifest.SelectSingleNode(
        '/*[local-name()="Vsix"]/*[local-name()="Identifier"]')
    $packageVersionNode = $packageManifest.SelectSingleNode(
        '/*[local-name()="Vsix"]/*[local-name()="Identifier"]/*[local-name()="Version"]')
    if ($null -eq $packageIdentifier -or $packageIdentifier.Id -ne 'McpVs2010.Bridge') {
        throw "VSIX 업데이트용 확장 ID가 고정되어 있지 않습니다: $($packageIdentifier.Id)"
    }
    if ($null -eq $packageVersionNode) {
        throw 'VSIX 패키지 버전이 없습니다.'
    }

    $packageVersion = [Version]$packageVersionNode.InnerText.Trim()
    $pkgdefEntry = $archive.GetEntry('McpVs2010.Bridge.pkgdef')
    $pkgdefStream = $pkgdefEntry.Open()
    try {
        $pkgdefReader = [System.IO.StreamReader]::new($pkgdefStream)
        try {
            $pkgdefText = $pkgdefReader.ReadToEnd()
        }
        finally {
            $pkgdefReader.Dispose()
        }
    }
    finally {
        $pkgdefStream.Dispose()
    }

    $packageAssemblyVersion = '{0}.{1}.{2}.{3}' -f @(
        $packageVersion.Major,
        $packageVersion.Minor,
        [Math]::Max($packageVersion.Build, 0),
        [Math]::Max($packageVersion.Revision, 0)
    )
    if (-not $pkgdefText.Contains(
        "McpVs2010.Bridge, Version=$packageAssemblyVersion",
        [System.StringComparison]::Ordinal)) {
        throw "VSIX 매니페스트와 pkgdef 어셈블리 버전이 일치하지 않습니다: $packageVersion"
    }
}
finally {
    $archive.Dispose()
}

$probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$probe.Start()
try {
    $port = ([System.Net.IPEndPoint]$probe.LocalEndpoint).Port
}
finally {
    $probe.Stop()
}

$serverBaseUrl = "http://127.0.0.1:$port"
$mcpUrl = "$serverBaseUrl/stream"
$tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$tempPrefix = $tempBase.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar
$testServerRoot = [System.IO.Path]::GetFullPath((Join-Path $tempBase (
    'mcp-vs2010-config-smoke-{0}' -f [Guid]::NewGuid().ToString('N'))))
if (-not $testServerRoot.StartsWith($tempPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "설정 테스트 폴더가 임시 폴더 밖입니다: $testServerRoot"
}

New-Item -ItemType Directory -Path $testServerRoot | Out-Null
Copy-Item -Path (Join-Path (Split-Path $server -Parent) '*') `
    -Destination $testServerRoot `
    -Recurse `
    -Force
$registryPath = 'Software\McpVs2010'
$registryValue = 'HttpStreamPort'
$registryBase = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
    [Microsoft.Win32.RegistryHive]::CurrentUser,
    [Microsoft.Win32.RegistryView]::Registry32)
$registryKey = $registryBase.CreateSubKey($registryPath)
$oldRegistryValue = $registryKey.GetValue($registryValue, $null)
$registryKey.SetValue($registryValue, $port, [Microsoft.Win32.RegistryValueKind]::DWord)
$registryKey.Dispose(); $registryBase.Dispose()
$testServer = Join-Path $testServerRoot (Split-Path $server -Leaf)

$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $testServer
$startInfo.Arguments = ''
$startInfo.WorkingDirectory = $testServerRoot
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.CreateNoWindow = $true

$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $startInfo
[void]$process.Start()

Add-Type -AssemblyName System.Net.Http
$httpClient = [System.Net.Http.HttpClient]::new()
$httpClient.Timeout = [TimeSpan]::FromSeconds(10)

function ConvertFrom-McpResponse {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,

        [Parameter(Mandatory = $true)]
        [int]$ExpectedId
    )

    try {
        $json = $Text | ConvertFrom-Json
        if ($json.id -eq $ExpectedId) {
            return $json
        }
    }
    catch {
    }

    foreach ($line in ($Text -split '\r?\n')) {
        if (-not $line.StartsWith('data:', [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $data = $line.Substring(5).TrimStart()
        try {
            $json = $data | ConvertFrom-Json
            if ($json.id -eq $ExpectedId) {
                return $json
            }
        }
        catch {
        }
    }

    throw "MCP 응답에서 ID $ExpectedId JSON-RPC 메시지를 찾을 수 없습니다: $Text"
}

function Invoke-McpRequest {
    param(
        [Parameter(Mandatory = $true)]
        [int]$Id,

        [Parameter(Mandatory = $true)]
        [string]$Method,

        [Parameter(Mandatory = $true)]
        [string]$Json,

        [string]$Name
    )

    $request = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Post,
        $mcpUrl)
    try {
        [void]$request.Headers.TryAddWithoutValidation('Accept', 'application/json, text/event-stream')
        [void]$request.Headers.TryAddWithoutValidation('MCP-Protocol-Version', '2026-07-28')
        [void]$request.Headers.TryAddWithoutValidation('Mcp-Method', $Method)
        if (-not [string]::IsNullOrWhiteSpace($Name)) {
            [void]$request.Headers.TryAddWithoutValidation('Mcp-Name', $Name)
        }
        $request.Content = [System.Net.Http.StringContent]::new(
            $Json,
            [System.Text.Encoding]::UTF8,
            'application/json')

        $response = $httpClient.SendAsync($request).GetAwaiter().GetResult()
        try {
            $responseText = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if (-not $response.IsSuccessStatusCode) {
                throw "MCP HTTP 요청 실패: $([int]$response.StatusCode) $($response.ReasonPhrase) $responseText"
            }

            return ConvertFrom-McpResponse -Text $responseText -ExpectedId $Id
        }
        finally {
            $response.Dispose()
        }
    }
    finally {
        $request.Dispose()
    }
}

try {
    $tools = $null
    $lastStartupError = $null
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        if ($process.HasExited) {
            $stderr = $process.StandardError.ReadToEnd()
            throw "MCP 서버가 시작 중 종료되었습니다($($process.ExitCode)): $stderr"
        }

        try {
            $tools = Invoke-McpRequest `
                -Id 1 `
                -Method 'tools/list' `
                -Json '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"mcp-vs2010-smoke","version":"1.0.11"},"io.modelcontextprotocol/clientCapabilities":{}}}}'
            break
        }
        catch {
            $lastStartupError = $_
            Start-Sleep -Milliseconds 250
        }
    }

    if ($null -eq $tools) {
        throw "MCP HTTP 서버 시작 또는 tools/list 호출에 실패했습니다: $lastStartupError"
    }

    $actualNames = @($tools.result.tools | ForEach-Object name)
    $expectedNames = @(
        'list_vs2010_instances',
        'list_vs2010_recent_projects',
        'open_vs2010_recent_solution',
        'get_vs2010_state',
        'build_vs2010_solution',
        'build_vs2010_project',
        'cancel_vs2010_build'
    )
    foreach ($name in $expectedNames) {
        if ($actualNames -notcontains $name) {
            throw "MCP 도구가 노출되지 않았습니다: $name"
        }
    }

    $solutionTool = @($tools.result.tools | Where-Object name -eq 'build_vs2010_solution')[0]
    $solutionProperties = @($solutionTool.inputSchema.properties.PSObject.Properties.Name)
    if ($solutionProperties -notcontains 'operation') {
        throw 'build_vs2010_solution 입력 스키마에 operation이 없습니다.'
    }

    $projectTool = @($tools.result.tools | Where-Object name -eq 'build_vs2010_project')[0]
    $projectProperties = @($projectTool.inputSchema.properties.PSObject.Properties.Name)
    foreach ($propertyName in @('project', 'operation', 'processId', 'configuration', 'platform')) {
        if ($projectProperties -notcontains $propertyName) {
            throw "build_vs2010_project 입력 스키마에 $propertyName 이(가) 없습니다."
        }
    }
    if (@($projectTool.inputSchema.required) -notcontains 'project') {
        throw 'build_vs2010_project 입력 스키마에서 project가 필수가 아닙니다.'
    }

    $openRecentTool = @($tools.result.tools | Where-Object name -eq 'open_vs2010_recent_solution')[0]
    $openRecentProperties = @($openRecentTool.inputSchema.properties.PSObject.Properties.Name)
    foreach ($propertyName in @('position', 'processId', 'saveCurrentSolution')) {
        if ($openRecentProperties -notcontains $propertyName) {
            throw "open_vs2010_recent_solution 입력 스키마에 $propertyName 이(가) 없습니다."
        }
    }

    $call = Invoke-McpRequest `
        -Id 2 `
        -Method 'tools/call' `
        -Name 'list_vs2010_instances' `
        -Json '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_vs2010_instances","arguments":{},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"mcp-vs2010-smoke","version":"1.0.11"},"io.modelcontextprotocol/clientCapabilities":{}}}}'
    if ($call.id -ne 2 -or $call.result.isError) {
        throw 'list_vs2010_instances 스모크 호출이 실패했습니다.'
    }

    $recentCall = Invoke-McpRequest `
        -Id 3 `
        -Method 'tools/call' `
        -Name 'list_vs2010_recent_projects' `
        -Json '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_vs2010_recent_projects","arguments":{},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"mcp-vs2010-smoke","version":"1.0.11"},"io.modelcontextprotocol/clientCapabilities":{}}}}'
    if ($recentCall.id -ne 3 -or $recentCall.result.isError) {
        throw 'list_vs2010_recent_projects 스모크 호출이 실패했습니다.'
    }
    $recentResult = $recentCall.result.content[0].text | ConvertFrom-Json
    if ($recentResult.RegistryView -ne 'Registry32' -or
        $recentResult.RegistryPath -ne 'HKCU\Software\Microsoft\VisualStudio\10.0\ProjectMRUList' -or
        $null -eq $recentResult.Items) {
        throw 'list_vs2010_recent_projects 결과 구조가 올바르지 않습니다.'
    }

    $legacyRequest = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Post,
        "$serverBaseUrl/mcp")
    try {
        $legacyRequest.Content = [System.Net.Http.StringContent]::new(
            '{}',
            [System.Text.Encoding]::UTF8,
            'application/json')
        $legacyResponse = $httpClient.SendAsync($legacyRequest).GetAwaiter().GetResult()
        try {
            if ([int]$legacyResponse.StatusCode -ne 404) {
                throw "기존 /mcp 경로 비활성화 검사 실패: $([int]$legacyResponse.StatusCode)"
            }
        }
        finally {
            $legacyResponse.Dispose()
        }
    }
    finally {
        $legacyRequest.Dispose()
    }

    $originRequest = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Post,
        $mcpUrl)
    try {
        [void]$originRequest.Headers.TryAddWithoutValidation('Origin', 'https://example.invalid')
        $originRequest.Content = [System.Net.Http.StringContent]::new(
            '{}',
            [System.Text.Encoding]::UTF8,
            'application/json')
        $originResponse = $httpClient.SendAsync($originRequest).GetAwaiter().GetResult()
        try {
            if ([int]$originResponse.StatusCode -ne 403) {
                throw "외부 Origin 차단 검사 실패: $([int]$originResponse.StatusCode)"
            }
        }
        finally {
            $originResponse.Dispose()
        }
    }
    finally {
        $originRequest.Dispose()
    }

    Write-Output 'VSIX 구조 검사: 성공'
    Write-Output "VSIX 업데이트 ID/버전 검사: 성공 (McpVs2010.Bridge $packageVersion)"
    Write-Output "MCP Streamable HTTP: 성공 ($mcpUrl)"
    Write-Output "Windows 레지스트리 포트 적용: 성공 ($port)"
    Write-Output ('MCP 도구: ' + ($actualNames -join ', '))
    Write-Output 'Project Only 도구 스키마: 성공'
    Write-Output '최근 솔루션 열기 도구 스키마: 성공'
    Write-Output 'list_vs2010_instances 호출: 성공'
    Write-Output 'list_vs2010_recent_projects 호출: 성공'
    Write-Output '기존 /mcp 경로 비활성화: 성공'
    Write-Output '외부 Origin 차단: 성공'
}
finally {
    $httpClient.Dispose()
    if (-not $process.HasExited) {
        $process.Kill()
        [void]$process.WaitForExit(5000)
    }
    $process.Dispose()
    $restoreBase = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry32)
    $restoreKey = $restoreBase.CreateSubKey($registryPath)
    if ($null -eq $oldRegistryValue) { $restoreKey.DeleteValue($registryValue, $false) } else { $restoreKey.SetValue($registryValue, $oldRegistryValue, [Microsoft.Win32.RegistryValueKind]::DWord) }
    $restoreKey.Dispose(); $restoreBase.Dispose()
    if (Test-Path -LiteralPath $testServerRoot) {
        $resolvedTestRoot = [System.IO.Path]::GetFullPath($testServerRoot)
        if (-not $resolvedTestRoot.StartsWith($tempPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "삭제할 설정 테스트 폴더가 임시 폴더 밖입니다: $resolvedTestRoot"
        }
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
