[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string]$VsixPath
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$vs2010Root = 'C:\Program Files (x86)\Microsoft Visual Studio 10.0'
$installer = Join-Path $vs2010Root 'Common7\IDE\VSIXInstaller.exe'
$userExtensionsRoot = Join-Path $env:LOCALAPPDATA 'Microsoft\VisualStudio\10.0\Extensions'
$enabledExtensionsRegistryPath = 'HKCU:\Software\Microsoft\VisualStudio\10.0\ExtensionManager\EnabledExtensions'

function Read-VsixManifest {
    param([Parameter(Mandatory = $true)][string]$Path)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $archive.GetEntry('extension.vsixmanifest')
        if ($null -eq $entry) {
            throw "VSIX에 extension.vsixmanifest가 없습니다: $Path"
        }

        $stream = $entry.Open()
        try {
            $reader = [System.IO.StreamReader]::new($stream)
            try {
                [xml]$manifest = $reader.ReadToEnd()
            }
            finally {
                $reader.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }

    $identifier = $manifest.SelectSingleNode(
        '/*[local-name()="Vsix"]/*[local-name()="Identifier"]')
    $versionNode = $manifest.SelectSingleNode(
        '/*[local-name()="Vsix"]/*[local-name()="Identifier"]/*[local-name()="Version"]')
    if ($null -eq $identifier -or [string]::IsNullOrWhiteSpace($identifier.Id)) {
        throw "VSIX 매니페스트에서 확장 ID를 읽을 수 없습니다: $Path"
    }
    if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
        throw "VSIX 매니페스트에서 버전을 읽을 수 없습니다: $Path"
    }

    return [pscustomobject]@{
        Id = [string]$identifier.Id
        Version = $versionNode.InnerText.Trim()
    }
}

function Get-InstalledVsix {
    param([Parameter(Mandatory = $true)][string]$ExtensionId)

    $roots = @(
        [pscustomobject]@{
            Path = $userExtensionsRoot
            Scope = '현재 사용자'
        },
        [pscustomobject]@{
            Path = Join-Path $vs2010Root 'Common7\IDE\Extensions'
            Scope = '관리자'
        }
    )

    $matches = @()
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root.Path)) {
            continue
        }

        try {
            $manifests = Get-ChildItem -LiteralPath $root.Path `
                -Filter 'extension.vsixmanifest' `
                -File `
                -Recurse `
                -ErrorAction Stop
        }
        catch {
            throw "설치된 VSIX 탐색 실패: $($root.Path): $($_.Exception.Message)"
        }
        foreach ($manifestPath in $manifests) {
            try {
                [xml]$installedManifest = Get-Content -LiteralPath $manifestPath.FullName -Raw
                $identifier = $installedManifest.SelectSingleNode(
                    '/*[local-name()="Vsix"]/*[local-name()="Identifier"]')
                if ($null -eq $identifier -or
                    -not [string]::Equals(
                        [string]$identifier.Id,
                        $ExtensionId,
                        [System.StringComparison]::OrdinalIgnoreCase)) {
                    continue
                }

                $versionNode = $installedManifest.SelectSingleNode(
                    '/*[local-name()="Vsix"]/*[local-name()="Identifier"]/*[local-name()="Version"]')
                $matches += [pscustomobject]@{
                    Id = [string]$identifier.Id
                    Version = if ($null -eq $versionNode) { '<알 수 없음>' } else { $versionNode.InnerText.Trim() }
                    Scope = $root.Scope
                    ManifestPath = $manifestPath.FullName
                }
            }
            catch {
                Write-Warning "설치된 VSIX 매니페스트 읽기 실패: $($manifestPath.FullName): $($_.Exception.Message)"
            }
        }
    }

    return @($matches)
}

function Get-EnabledVsixRegistration {
    param([Parameter(Mandatory = $true)][string]$ExtensionId)

    if (-not (Test-Path -LiteralPath $enabledExtensionsRegistryPath)) {
        return @()
    }

    $key = Get-Item -LiteralPath $enabledExtensionsRegistryPath
    $prefix = $ExtensionId + ','
    $registrations = @()
    foreach ($valueName in $key.GetValueNames()) {
        if (-not $valueName.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $registrations += [pscustomobject]@{
            Name = $valueName
            Version = $valueName.Substring($prefix.Length)
            Path = [string]$key.GetValue($valueName)
        }
    }

    return @($registrations)
}

function Remove-ResidualUserVsix {
    param(
        [Parameter(Mandatory = $true)][string]$ExtensionId,
        [Parameter(Mandatory = $true)][object[]]$InstalledExtensions
    )

    $root = [System.IO.Path]::GetFullPath($userExtensionsRoot)
    $rootPrefix = $root.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar

    foreach ($extension in $InstalledExtensions) {
        if ($extension.Scope -ne '현재 사용자') {
            continue
        }

        $extensionDirectory = [System.IO.Path]::GetFullPath(
            (Split-Path $extension.ManifestPath -Parent))
        if (-not $extensionDirectory.StartsWith(
            $rootPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "삭제할 VSIX 잔여 폴더가 사용자 확장 루트 밖입니다: $extensionDirectory"
        }

        [xml]$manifest = Get-Content -LiteralPath $extension.ManifestPath -Raw
        $identifier = $manifest.SelectSingleNode(
            '/*[local-name()="Vsix"]/*[local-name()="Identifier"]')
        if ($null -eq $identifier -or
            -not [string]::Equals(
                [string]$identifier.Id,
                $ExtensionId,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "삭제 직전 VSIX ID 검증 실패: $extensionDirectory"
        }

        Remove-Item -LiteralPath $extensionDirectory -Recurse -Force
        Write-Output "VSIX 잔여 폴더 제거: $extensionDirectory"
    }
}

function Remove-EnabledVsixRegistrations {
    param([Parameter(Mandatory = $true)][string]$ExtensionId)

    foreach ($registration in @(Get-EnabledVsixRegistration -ExtensionId $ExtensionId)) {
        Remove-ItemProperty `
            -LiteralPath $enabledExtensionsRegistryPath `
            -Name $registration.Name
        Write-Output "VSIX 활성 등록 제거: $($registration.Name)"
    }
}

function Reset-Vs2010ExtensionCache {
    if (-not (Test-Path -LiteralPath $userExtensionsRoot)) {
        return
    }

    foreach ($pattern in @('extensions.*.cache', 'extensionSdks.*.cache')) {
        foreach ($cacheFile in @(Get-ChildItem -Path (Join-Path $userExtensionsRoot $pattern) -File -ErrorAction SilentlyContinue)) {
            Remove-Item -LiteralPath $cacheFile.FullName -Force
            Write-Output "VS2010 확장 캐시 제거: $($cacheFile.FullName)"
        }
    }
}

if ([string]::IsNullOrWhiteSpace($VsixPath)) {
    $sourceManifestPath = Join-Path $projectRoot 'src\McpVs2010.Bridge\Vsix\extension.vsixmanifest'
    [xml]$sourceManifest = Get-Content -LiteralPath $sourceManifestPath -Raw
    $versionNode = $sourceManifest.SelectSingleNode(
        '/*[local-name()="Vsix"]/*[local-name()="Identifier"]/*[local-name()="Version"]')
    if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
        throw "VSIX 매니페스트에서 버전을 읽을 수 없습니다: $sourceManifestPath"
    }

    $VsixPath = Join-Path $projectRoot (
        'artifacts\McpVs2010.Bridge-{0}.vsix' -f $versionNode.InnerText.Trim())
}

if (-not (Test-Path -LiteralPath $installer)) {
    throw "Visual Studio 2010 VSIXInstaller를 찾을 수 없습니다: $installer"
}
if (-not (Test-Path -LiteralPath $VsixPath)) {
    throw "설치할 VSIX 파일을 찾을 수 없습니다: $VsixPath"
}

$resolvedVsixPath = [System.IO.Path]::GetFullPath($VsixPath)
$target = Read-VsixManifest -Path $resolvedVsixPath
if (-not [string]::Equals(
    $target.Id,
    'McpVs2010.Bridge',
    [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "설치 스크립트가 허용하지 않는 확장 ID입니다: $($target.Id)"
}

$runningVs2010 = @(Get-Process -Name devenv -ErrorAction SilentlyContinue | Where-Object {
    try {
        $_.Path.StartsWith($vs2010Root, [System.StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        $true
    }
})
if ($runningVs2010.Count -gt 0) {
    $processIds = @($runningVs2010 | ForEach-Object { $_.Id })
    throw "Visual Studio 2010을 모두 종료한 후 다시 실행하십시오. 실행 중 PID: $($processIds -join ', ')"
}

$installed = @(Get-InstalledVsix -ExtensionId $target.Id)
$enabled = @(Get-EnabledVsixRegistration -ExtensionId $target.Id)
$adminInstalled = @($installed | Where-Object Scope -eq '관리자')
if ($adminInstalled.Count -gt 0) {
    $adminDescriptions = @($adminInstalled | ForEach-Object {
        "$($_.Version) [$($_.ManifestPath)]"
    })
    throw "관리자 범위 설치본은 자동 교체하지 않습니다. 관리자 권한으로 먼저 제거하십시오: $($adminDescriptions -join ', ')"
}

Write-Output "설치 대상: $($target.Id) $($target.Version)"
Write-Output "VSIX 파일: $resolvedVsixPath"
if ($installed.Count -gt 0) {
    Write-Output ('설치 폴더 제거 대상: ' + (($installed | ForEach-Object { $_.Version }) -join ', '))
}
else {
    Write-Output '설치 폴더 제거 대상: 없음'
}
if ($enabled.Count -gt 0) {
    Write-Output ('활성 등록 제거 대상: ' + (($enabled | ForEach-Object { $_.Name }) -join ', '))
}
else {
    Write-Output '활성 등록 제거 대상: 없음'
}

if (-not $PSCmdlet.ShouldProcess(
    "$($target.Id) $($target.Version)",
    '기존 사용자 설치본을 모두 제거하고 새 VSIX 설치')) {
    return
}

if ($enabled.Count -gt 0) {
    $uninstallProcess = Start-Process `
        -FilePath $installer `
        -ArgumentList "/quiet /uninstall:$($target.Id)" `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    try {
        # VSIX 폴더가 이미 삭제된 상태에서 EnabledExtensions만 남아 있으면
        # VSIXInstaller는 2003(설치되지 않음)을 반환한다. 아래에서 잔여
        # 레지스트리를 직접 정리할 수 있으므로 이 경우는 계속 진행한다.
        if ($uninstallProcess.ExitCode -ne 0 -and
            -not ($uninstallProcess.ExitCode -eq 2003 -and $installed.Count -eq 0)) {
            throw "기존 VSIX 제거 실패(종료 코드 $($uninstallProcess.ExitCode)): $($target.Id)"
        }
        if ($uninstallProcess.ExitCode -eq 2003 -and $installed.Count -eq 0) {
            Write-Output "VSIXInstaller가 기존 폴더 없음(2003)을 반환했지만 잔여 활성 등록 정리를 계속합니다."
        }
    }
    finally {
        $uninstallProcess.Dispose()
    }

    Write-Output "VSIXInstaller 기존 설치 제거 완료: $($target.Id)"
}

$residualInstalled = @(Get-InstalledVsix -ExtensionId $target.Id)
if ($residualInstalled.Count -gt 0) {
    Remove-ResidualUserVsix `
        -ExtensionId $target.Id `
        -InstalledExtensions $residualInstalled
}
Remove-EnabledVsixRegistrations -ExtensionId $target.Id
Reset-Vs2010ExtensionCache

$remainingInstalled = @(Get-InstalledVsix -ExtensionId $target.Id)
$remainingEnabled = @(Get-EnabledVsixRegistration -ExtensionId $target.Id)
if ($remainingInstalled.Count -gt 0 -or $remainingEnabled.Count -gt 0) {
    throw "기존 VSIX 정리 후 설치 폴더 또는 활성 등록이 남아 있습니다: $($target.Id)"
}

$installArguments = '/quiet "{0}"' -f $resolvedVsixPath.Replace('"', '\"')
$installProcess = Start-Process `
    -FilePath $installer `
    -ArgumentList $installArguments `
    -WindowStyle Hidden `
    -Wait `
    -PassThru
try {
    if ($installProcess.ExitCode -ne 0) {
        throw "새 VSIX 설치 실패(종료 코드 $($installProcess.ExitCode)): $resolvedVsixPath"
    }
}
finally {
    $installProcess.Dispose()
}

$installedAfter = @(Get-InstalledVsix -ExtensionId $target.Id | Where-Object {
    [string]::Equals(
        $_.Version,
        $target.Version,
        [System.StringComparison]::OrdinalIgnoreCase)
})
if ($installedAfter.Count -eq 0) {
    throw "VSIXInstaller는 성공했지만 설치 버전 $($target.Version)을 찾을 수 없습니다."
}

$enabledAfter = @(Get-EnabledVsixRegistration -ExtensionId $target.Id)
$expectedRegistrationName = "$($target.Id),$($target.Version)"
$matchingEnabled = @($enabledAfter | Where-Object {
    [string]::Equals(
        $_.Name,
        $expectedRegistrationName,
        [System.StringComparison]::OrdinalIgnoreCase)
})
if ($matchingEnabled.Count -ne 1) {
    $actualRegistrationNames = @($enabledAfter | ForEach-Object { $_.Name })
    throw "설치 후 VS2010 활성 등록 버전이 일치하지 않습니다. 예상: $expectedRegistrationName, 실제: $($actualRegistrationNames -join ', ')"
}

$activePath = [System.IO.Path]::GetFullPath($matchingEnabled[0].Path)
$activeManifest = Join-Path $activePath 'extension.vsixmanifest'
if (-not (Test-Path -LiteralPath $activeManifest)) {
    throw "설치 후 활성 등록 경로에 매니페스트가 없습니다: $activeManifest"
}
[xml]$activeManifestXml = Get-Content -LiteralPath $activeManifest -Raw
$activeVersionNode = $activeManifestXml.SelectSingleNode(
    '/*[local-name()="Vsix"]/*[local-name()="Identifier"]/*[local-name()="Version"]')
if ($null -eq $activeVersionNode -or
    -not [string]::Equals(
        $activeVersionNode.InnerText.Trim(),
        $target.Version,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "활성 등록 경로의 VSIX 버전이 설치 대상과 일치하지 않습니다: $activeManifest"
}

Write-Output "VSIX 업데이트 완료 및 활성 등록 확인: $($target.Id) $($target.Version)"
