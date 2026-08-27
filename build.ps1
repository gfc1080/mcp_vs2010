[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipRestore,

    [switch]$SkipServer,

    [switch]$SkipBridge
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))

function Assert-ArtifactsPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolved = [System.IO.Path]::GetFullPath($Path)
    $prefix = $artifactsRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "빌드 산출물 경로가 프로젝트 artifacts 폴더 밖입니다: $resolved"
    }
}

function Reset-ArtifactDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    Assert-ArtifactsPath -Path $Path
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Find-Vs2010InstallDir {
    $fromEnvironment = [Environment]::GetEnvironmentVariable('VS100COMNTOOLS', 'Process')
    if ([string]::IsNullOrWhiteSpace($fromEnvironment)) {
        $fromEnvironment = [Environment]::GetEnvironmentVariable('VS100COMNTOOLS', 'Machine')
    }

    if (-not [string]::IsNullOrWhiteSpace($fromEnvironment)) {
        $candidate = [System.IO.Path]::GetFullPath((Join-Path $fromEnvironment '..\..'))
        if (Test-Path -LiteralPath (Join-Path $candidate 'Common7\IDE\devenv.exe')) {
            return $candidate.TrimEnd('\')
        }
    }

    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine,
        [Microsoft.Win32.RegistryView]::Registry32)
    try {
        $key = $baseKey.OpenSubKey('SOFTWARE\Microsoft\VisualStudio\SxS\VS7')
        if ($null -ne $key) {
            try {
                $candidate = [string]$key.GetValue('10.0')
                if (-not [string]::IsNullOrWhiteSpace($candidate)) {
                    return $candidate.TrimEnd('\')
                }
            }
            finally {
                $key.Dispose()
            }
        }
    }
    finally {
        $baseKey.Dispose()
    }

    throw 'Visual Studio 2010 설치 위치를 찾을 수 없습니다.'
}

$vs2010InstallDir = Find-Vs2010InstallDir
$bridgeMsBuild = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe'
if (-not (Test-Path -LiteralPath $bridgeMsBuild)) {
    throw ".NET Framework 4.0 MSBuild가 없습니다: $bridgeMsBuild"
}

$serverProject = Join-Path $projectRoot 'src\McpVs2010.Server\McpVs2010.Server.csproj'
$bridgeProject = Join-Path $projectRoot 'src\McpVs2010.Bridge\McpVs2010.Bridge.csproj'
$vsixManifestPath = Join-Path $projectRoot 'src\McpVs2010.Bridge\Vsix\extension.vsixmanifest'
[xml]$serverProjectXml = Get-Content -LiteralPath $serverProject -Raw
$serverVersionNode = $serverProjectXml.SelectSingleNode(
    '/*[local-name()="Project"]/*[local-name()="PropertyGroup"]/*[local-name()="Version"]')
if ($null -eq $serverVersionNode -or [string]::IsNullOrWhiteSpace($serverVersionNode.InnerText)) {
    throw "MCP 서버 프로젝트에서 버전을 읽을 수 없습니다: $serverProject"
}
$serverVersion = $serverVersionNode.InnerText.Trim()
if ($serverVersion -notmatch '^\d+(\.\d+){1,3}$') {
    throw "MCP 서버 산출물 폴더에 사용할 수 없는 버전입니다: $serverVersion"
}

[xml]$vsixManifest = Get-Content -LiteralPath $vsixManifestPath -Raw
$vsixVersionNode = $vsixManifest.SelectSingleNode(
    '/*[local-name()="Vsix"]/*[local-name()="Identifier"]/*[local-name()="Version"]')
if ($null -eq $vsixVersionNode -or [string]::IsNullOrWhiteSpace($vsixVersionNode.InnerText)) {
    throw "VSIX 매니페스트에서 버전을 읽을 수 없습니다: $vsixManifestPath"
}
$vsixVersion = $vsixVersionNode.InnerText.Trim()
if ($vsixVersion -notmatch '^\d+(\.\d+){1,3}$') {
    throw "VSIX 파일 이름에 사용할 수 없는 버전입니다: $vsixVersion"
}

$serverOutput = Join-Path $artifactsRoot ("server-{0}" -f $serverVersion)
$vsixStage = Join-Path $artifactsRoot 'vsix-stage'
$vsixOutput = Join-Path $artifactsRoot ("McpVs2010.Bridge-{0}.vsix" -f $vsixVersion)
$deploymentOutput = Join-Path $artifactsRoot ("McpVs2010-Deployment-{0}" -f $vsixVersion)
$deploymentZip = Join-Path $artifactsRoot ("McpVs2010-Deployment-{0}.zip" -f $vsixVersion)
$latestDeploymentZip = Join-Path $artifactsRoot 'McpVs2010-Deployment-Latest.zip'

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null

if (-not $SkipServer) {
    Reset-ArtifactDirectory -Path $serverOutput

    if (-not $SkipRestore) {
        & dotnet restore $serverProject
        if ($LASTEXITCODE -ne 0) {
            throw "MCP 서버 NuGet 복원 실패: $LASTEXITCODE"
        }
    }

    & dotnet publish $serverProject --configuration $Configuration --no-restore --output $serverOutput
    if ($LASTEXITCODE -ne 0) {
        throw "MCP 서버 게시 실패: $LASTEXITCODE"
    }
}

if ($SkipBridge) {
    if (-not (Test-Path -LiteralPath $vsixOutput)) {
        throw "유지할 기존 VSIX 산출물이 없습니다: $vsixOutput"
    }
}
else {
    Reset-ArtifactDirectory -Path $vsixStage

    $bridgeArguments = @(
        $bridgeProject,
        '/t:Rebuild',
        "/p:Configuration=$Configuration",
        '/p:Platform=x86',
        "/p:Vs2010InstallDir=$vs2010InstallDir",
        '/v:minimal'
    )
    & $bridgeMsBuild @bridgeArguments
    if ($LASTEXITCODE -ne 0) {
        throw "VS2010 브리지 빌드 실패: $LASTEXITCODE"
    }

    $bridgeOutput = Join-Path $projectRoot "src\McpVs2010.Bridge\bin\$Configuration"
    Copy-Item -LiteralPath (Join-Path $bridgeOutput 'McpVs2010.Bridge.dll') -Destination $vsixStage
    $bridgePdb = Join-Path $bridgeOutput 'McpVs2010.Bridge.pdb'
    if (Test-Path -LiteralPath $bridgePdb) {
        Copy-Item -LiteralPath $bridgePdb -Destination $vsixStage
    }
    Copy-Item -LiteralPath $vsixManifestPath -Destination $vsixStage
    Copy-Item -LiteralPath (Join-Path $projectRoot 'src\McpVs2010.Bridge\Vsix\McpVs2010.Bridge.pkgdef') -Destination $vsixStage
    Copy-Item -LiteralPath (Join-Path $projectRoot 'src\McpVs2010.Bridge\Vsix\[Content_Types].xml') -Destination $vsixStage

    $vsixServerStage = Join-Path $vsixStage 'server'
    New-Item -ItemType Directory -Path $vsixServerStage -Force | Out-Null
    if (-not (Test-Path -LiteralPath $serverOutput)) {
        throw "VSIX에 포함할 MCP 서버 게시 폴더가 없습니다: $serverOutput"
    }
    Get-ChildItem -LiteralPath $serverOutput -File -Force | ForEach-Object {
        $serverFileName = $_.Name
        if ($_.Extension -notin @('.dll', '.pdb')) {
            # VS2010 VSIX Installer copies assemblies/symbols but drops executable and
            # configuration extensions. The bridge restores this suffix at startup.
            $serverFileName += '.payload.pdb'
        }
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $vsixServerStage $serverFileName) -Force
    }

    Assert-ArtifactsPath -Path $vsixOutput
    if (Test-Path -LiteralPath $vsixOutput) {
        Remove-Item -LiteralPath $vsixOutput -Force
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $vsixStage,
        $vsixOutput,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)
}

# 다른 사용자에게 전달할 독립 배포 패키지를 만든다. 소스 저장소 없이도
# VSIX와 설치 스크립트만으로 현재 사용자 범위 설치를 수행할 수 있다.
Reset-ArtifactDirectory -Path $deploymentOutput
Copy-Item -LiteralPath $vsixOutput -Destination $deploymentOutput
Copy-Item -LiteralPath $vsixOutput -Destination (Join-Path $deploymentOutput 'McpVs2010.Bridge.Latest.vsix')
Copy-Item -LiteralPath (Join-Path $projectRoot 'scripts\Install-Vsix.ps1') -Destination (Join-Path $deploymentOutput 'Install-Vsix.ps1')
Copy-Item -LiteralPath (Join-Path $projectRoot 'deployment\Install-McpVs2010-Bridge.cmd') -Destination $deploymentOutput
Copy-Item -LiteralPath (Join-Path $projectRoot 'deployment\Install-McpVs2010-Bridge.ps1') -Destination $deploymentOutput
Copy-Item -LiteralPath (Join-Path $projectRoot 'deployment\README-Deployment.txt') -Destination $deploymentOutput
if (Test-Path -LiteralPath $deploymentZip) {
    Remove-Item -LiteralPath $deploymentZip -Force
}
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $deploymentOutput,
    $deploymentZip,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)
Copy-Item -LiteralPath $deploymentZip -Destination $latestDeploymentZip -Force

if ($SkipServer) {
    Write-Output 'MCP server: publish skipped (existing files preserved)'
}
else {
    Write-Output "MCP server: $serverOutput"
}
if ($SkipBridge) {
    Write-Output "VS2010 VSIX: existing artifact preserved ($vsixOutput)"
}
else {
    Write-Output "VS2010 VSIX: $vsixOutput"
}
Write-Output "Deployment folder: $deploymentOutput"
Write-Output "Deployment ZIP: $deploymentZip"
Write-Output "Latest deployment ZIP: $latestDeploymentZip"
