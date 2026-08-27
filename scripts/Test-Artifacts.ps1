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
        throw "Cannot read the MCP server project version: $serverProjectPath"
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
        throw "Cannot read the VSIX manifest version: $manifestPath"
    }

    $vsix = Join-Path $projectRoot (
        'artifacts\McpVs2010.Bridge-{0}.vsix' -f $versionNode.InnerText.Trim())
}
else {
    $vsix = [System.IO.Path]::GetFullPath($VsixPath)
}

if (-not (Test-Path -LiteralPath $server)) {
    throw "MCP server artifact is missing: $server"
}
if (-not (Test-Path -LiteralPath $vsix)) {
    throw "VSIX artifact is missing: $vsix"
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
        throw "Required VSIX entry is missing: $entry"
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
        throw "VSIX update extension ID is not fixed: $($packageIdentifier.Id)"
    }
    if ($null -eq $packageVersionNode) {
        throw 'VSIX package version is missing.'
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
        throw "VSIX manifest and pkgdef assembly versions do not match: $packageVersion"
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
    throw "Configuration test folder is outside the temporary folder: $testServerRoot"
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

    throw "Expected JSON-RPC message ID $ExpectedId was not found in the MCP response: $Text"
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
                throw "MCP HTTP request failed: $([int]$response.StatusCode) $($response.ReasonPhrase) $responseText"
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
            throw "MCP server exited during startup ($($process.ExitCode)): $stderr"
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
            throw "MCP HTTP server startup or tools/list failed: $lastStartupError"
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
            throw "MCP tool was not exposed: $name"
        }
    }

    $solutionTool = @($tools.result.tools | Where-Object name -eq 'build_vs2010_solution')[0]
    $solutionProperties = @($solutionTool.inputSchema.properties.PSObject.Properties.Name)
    if ($solutionProperties -notcontains 'operation') {
        throw 'build_vs2010_solution input schema is missing operation.'
    }

    $projectTool = @($tools.result.tools | Where-Object name -eq 'build_vs2010_project')[0]
    $projectProperties = @($projectTool.inputSchema.properties.PSObject.Properties.Name)
    foreach ($propertyName in @('project', 'operation', 'processId', 'configuration', 'platform')) {
        if ($projectProperties -notcontains $propertyName) {
        throw "build_vs2010_project input schema is missing $propertyName."
        }
    }
    if (@($projectTool.inputSchema.required) -notcontains 'project') {
        throw 'project must be required in the build_vs2010_project input schema.'
    }

    $openRecentTool = @($tools.result.tools | Where-Object name -eq 'open_vs2010_recent_solution')[0]
    $openRecentProperties = @($openRecentTool.inputSchema.properties.PSObject.Properties.Name)
    foreach ($propertyName in @('position', 'processId', 'saveCurrentSolution')) {
        if ($openRecentProperties -notcontains $propertyName) {
        throw "open_vs2010_recent_solution input schema is missing $propertyName."
        }
    }

    $call = Invoke-McpRequest `
        -Id 2 `
        -Method 'tools/call' `
        -Name 'list_vs2010_instances' `
        -Json '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_vs2010_instances","arguments":{},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"mcp-vs2010-smoke","version":"1.0.11"},"io.modelcontextprotocol/clientCapabilities":{}}}}'
    if ($call.id -ne 2 -or $call.result.isError) {
        throw 'list_vs2010_instances smoke call failed.'
    }

    $recentCall = Invoke-McpRequest `
        -Id 3 `
        -Method 'tools/call' `
        -Name 'list_vs2010_recent_projects' `
        -Json '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_vs2010_recent_projects","arguments":{},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"mcp-vs2010-smoke","version":"1.0.11"},"io.modelcontextprotocol/clientCapabilities":{}}}}'
    if ($recentCall.id -ne 3 -or $recentCall.result.isError) {
        throw 'list_vs2010_recent_projects smoke call failed.'
    }
    $recentResult = $recentCall.result.content[0].text | ConvertFrom-Json
    if ($recentResult.RegistryView -ne 'Registry32' -or
        $recentResult.RegistryPath -ne 'HKCU\Software\Microsoft\VisualStudio\10.0\ProjectMRUList' -or
        $null -eq $recentResult.Items) {
        throw 'list_vs2010_recent_projects result structure is invalid.'
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
                throw "Legacy /mcp path check failed: $([int]$legacyResponse.StatusCode)"
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
                throw "External Origin rejection check failed: $([int]$originResponse.StatusCode)"
            }
        }
        finally {
            $originResponse.Dispose()
        }
    }
    finally {
        $originRequest.Dispose()
    }

    Write-Output 'VSIX structure: PASS'
    Write-Output "VSIX update ID/version: PASS (McpVs2010.Bridge $packageVersion)"
    Write-Output "MCP Streamable HTTP: PASS ($mcpUrl)"
    Write-Output "Windows registry port: PASS ($port)"
    Write-Output ('MCP tools: ' + ($actualNames -join ', '))
    Write-Output 'Project Only tool schema: PASS'
    Write-Output 'Open recent solution schema: PASS'
    Write-Output 'list_vs2010_instances call: PASS'
    Write-Output 'list_vs2010_recent_projects call: PASS'
    Write-Output 'Legacy /mcp path disabled: PASS'
    Write-Output 'External Origin rejected: PASS'
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
            throw "Configuration test folder to delete is outside the temporary folder: $resolvedTestRoot"
        }
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
