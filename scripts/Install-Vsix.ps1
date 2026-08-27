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
            throw "VSIX has not extension.vsixmanifest: $Path"
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
        throw "Cannot read the extension ID from the VSIX manifest: $Path"
    }
    if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
        throw "Cannot read the version from the VSIX manifest: $Path"
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
            Scope = 'Current User'
        },
        [pscustomobject]@{
            Path = Join-Path $vs2010Root 'Common7\IDE\Extensions'
            Scope = 'Administrator'
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
            throw "Failed to discover installed VSIX extensions: $($root.Path): $($_.Exception.Message)"
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
                    Version = if ($null -eq $versionNode) { '<Unknown>' } else { $versionNode.InnerText.Trim() }
                    Scope = $root.Scope
                    ManifestPath = $manifestPath.FullName
                }
            }
            catch {
                Write-Warning "Failed to read the installed VSIX manifest: $($manifestPath.FullName): $($_.Exception.Message)"
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
        if ($extension.Scope -ne 'Current User') {
            continue
        }

        $extensionDirectory = [System.IO.Path]::GetFullPath(
            (Split-Path $extension.ManifestPath -Parent))
        if (-not $extensionDirectory.StartsWith(
            $rootPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "The VSIX residual folder to be deleted is outside the user extension root: $extensionDirectory"
        }

        [xml]$manifest = Get-Content -LiteralPath $extension.ManifestPath -Raw
        $identifier = $manifest.SelectSingleNode(
            '/*[local-name()="Vsix"]/*[local-name()="Identifier"]')
        if ($null -eq $identifier -or
            -not [string]::Equals(
                [string]$identifier.Id,
                $ExtensionId,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "VSIX ID verification failed immediately before deletion: $extensionDirectory"
        }

        Remove-Item -LiteralPath $extensionDirectory -Recurse -Force
        Write-Output "Remove residual VSIX folders: $extensionDirectory"
    }
}

function Remove-EnabledVsixRegistrations {
    param([Parameter(Mandatory = $true)][string]$ExtensionId)

    foreach ($registration in @(Get-EnabledVsixRegistration -ExtensionId $ExtensionId)) {
        Remove-ItemProperty `
            -LiteralPath $enabledExtensionsRegistryPath `
            -Name $registration.Name
        Write-Output "Unregister VSIX: $($registration.Name)"
    }
}

function Reset-Vs2010ExtensionCache {
    if (-not (Test-Path -LiteralPath $userExtensionsRoot)) {
        return
    }

    foreach ($pattern in @('extensions.*.cache', 'extensionSdks.*.cache')) {
        foreach ($cacheFile in @(Get-ChildItem -Path (Join-Path $userExtensionsRoot $pattern) -File -ErrorAction SilentlyContinue)) {
            Remove-Item -LiteralPath $cacheFile.FullName -Force
            Write-Output "Remove VS2010 Extension Cache: $($cacheFile.FullName)"
        }
    }
}

if ([string]::IsNullOrWhiteSpace($VsixPath)) {
    $sourceManifestPath = Join-Path $projectRoot 'src\McpVs2010.Bridge\Vsix\extension.vsixmanifest'
    [xml]$sourceManifest = Get-Content -LiteralPath $sourceManifestPath -Raw
    $versionNode = $sourceManifest.SelectSingleNode(
        '/*[local-name()="Vsix"]/*[local-name()="Identifier"]/*[local-name()="Version"]')
    if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
        throw "Cannot read the version from the VSIX manifest: $sourceManifestPath"
    }

    $VsixPath = Join-Path $projectRoot (
        'artifacts\McpVs2010.Bridge-{0}.vsix' -f $versionNode.InnerText.Trim())
}

if (-not (Test-Path -LiteralPath $installer)) {
    throw "Cannot find Visual Studio 2010 VSIXInstaller: $installer"
}
if (-not (Test-Path -LiteralPath $VsixPath)) {
    throw "The VSIX file to install could not be found: $VsixPath"
}

$resolvedVsixPath = [System.IO.Path]::GetFullPath($VsixPath)
$target = Read-VsixManifest -Path $resolvedVsixPath
if (-not [string]::Equals(
    $target.Id,
    'McpVs2010.Bridge',
    [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "This is an extension ID that the installation script does not allow: $($target.Id)"
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
    throw "Close all Visual Studio 2010 instances and try again. Running PIDs: $($processIds -join ', ')"
}

$installed = @(Get-InstalledVsix -ExtensionId $target.Id)
$enabled = @(Get-EnabledVsixRegistration -ExtensionId $target.Id)
$adminInstalled = @($installed | Where-Object Scope -eq 'Administrator')
if ($adminInstalled.Count -gt 0) {
    $adminDescriptions = @($adminInstalled | ForEach-Object {
        "$($_.Version) [$($_.ManifestPath)]"
    })
    throw "Administrator-scope installations are not replaced automatically. Remove them first: $($adminDescriptions -join ', ')"
}

Write-Output "Install target: $($target.Id) $($target.Version)"
Write-Output "VSIX file: $resolvedVsixPath"
if ($installed.Count -gt 0) {
    Write-Output ('Installation folders to remove: ' + (($installed | ForEach-Object { $_.Version }) -join ', '))
}
else {
    Write-Output 'Installation folders to remove: none'
}
if ($enabled.Count -gt 0) {
    Write-Output ('Enabled registrations to remove: ' + (($enabled | ForEach-Object { $_.Name }) -join ', '))
}
else {
    Write-Output 'Enabled registrations to remove: none'
}

if (-not $PSCmdlet.ShouldProcess(
    "$($target.Id) $($target.Version)",
    'Remove existing user installation and install the new VSIX')) {
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
        # If only EnabledExtensions remains after the VSIX folder was deleted,
        # VSIXInstaller returns 2003 (not installed). Continue and clean the stale registry.
        if ($uninstallProcess.ExitCode -ne 0 -and
            -not ($uninstallProcess.ExitCode -eq 2003 -and $installed.Count -eq 0)) {
            throw "Failed to remove the existing VSIX (exit code $($uninstallProcess.ExitCode)): $($target.Id)"
        }
        if ($uninstallProcess.ExitCode -eq 2003 -and $installed.Count -eq 0) {
            Write-Output "VSIXInstaller returned 2003 (folder not installed); continuing with stale registration cleanup."
        }
    }
    finally {
        $uninstallProcess.Dispose()
    }

    Write-Output "VSIXInstaller removed the existing installation: $($target.Id)"
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
    throw "An installation folder or enabled registration remains after cleanup: $($target.Id)"
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
        throw "New VSIX installation failed (exit code $($installProcess.ExitCode)): $resolvedVsixPath"
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
    throw "VSIXInstaller succeeded, but installed version $($target.Version) was not found."
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
    throw "The enabled VS2010 registration version does not match. Expected: $expectedRegistrationName, actual: $($actualRegistrationNames -join ', ')"
}

$activePath = [System.IO.Path]::GetFullPath($matchingEnabled[0].Path)
$activeManifest = Join-Path $activePath 'extension.vsixmanifest'
if (-not (Test-Path -LiteralPath $activeManifest)) {
    throw "The active registration path has no manifest after installation: $activeManifest"
}
[xml]$activeManifestXml = Get-Content -LiteralPath $activeManifest -Raw
$activeVersionNode = $activeManifestXml.SelectSingleNode(
    '/*[local-name()="Vsix"]/*[local-name()="Identifier"]/*[local-name()="Version"]')
if ($null -eq $activeVersionNode -or
    -not [string]::Equals(
        $activeVersionNode.InnerText.Trim(),
        $target.Version,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The VSIX version in the active registration path does not match the install target: $activeManifest"
}

# VS2010 VSIXInstaller can omit arbitrary nested payload files. Restore the
# bundled MCP server files from the VSIX into the active extension directory.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($resolvedVsixPath)
try {
    foreach ($entry in $archive.Entries | Where-Object { $_.FullName -like 'server/*' -and -not $_.FullName.EndsWith('/') }) {
        $relative = $entry.FullName.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        if ($relative.EndsWith('.payload.pdb', [System.StringComparison]::OrdinalIgnoreCase)) {
            $relative = $relative.Substring(0, $relative.Length - '.payload.pdb'.Length)
        }
        $destination = Join-Path $activePath $relative
        $destinationDirectory = Split-Path $destination -Parent
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        $input = $entry.Open()
        try {
            $output = [System.IO.File]::Open($destination, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
            try { $input.CopyTo($output) } finally { $output.Dispose() }
        }
        finally { $input.Dispose() }
    }
}
finally { $archive.Dispose() }

# Install the server payload in a stable per-user location independent of the
# VSIX version, so START continues to work after extension upgrades.
$localServerDirectory = Join-Path $env:LOCALAPPDATA 'McpVs2010'
New-Item -ItemType Directory -Path $localServerDirectory -Force | Out-Null
$archive = [System.IO.Compression.ZipFile]::OpenRead($resolvedVsixPath)
try {
    foreach ($entry in $archive.Entries | Where-Object { $_.FullName -like 'server/*' -and -not $_.FullName.EndsWith('/') }) {
        $fileName = [System.IO.Path]::GetFileName($entry.FullName)
        if ($fileName.EndsWith('.payload.pdb', [System.StringComparison]::OrdinalIgnoreCase)) {
            $fileName = $fileName.Substring(0, $fileName.Length - '.payload.pdb'.Length)
        }
        $destination = Join-Path $localServerDirectory $fileName
        $input = $entry.Open()
        try {
            $output = [System.IO.File]::Open($destination, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
            try { $input.CopyTo($output) } finally { $output.Dispose() }
        }
        finally { $input.Dispose() }
    }
}
finally { $archive.Dispose() }

Write-Output "VSIX update completed and enabled registration verified: $($target.Id) $($target.Version)"
