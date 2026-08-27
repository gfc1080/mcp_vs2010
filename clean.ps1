[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [switch]$AllBuildOutputs
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$artifactsRoot = Join-Path $projectRoot 'artifacts'

function Remove-MatchingItems {
    param([Parameter(Mandatory = $true)][string]$Root, [Parameter(Mandatory = $true)][string[]]$Patterns)
    if (-not (Test-Path -LiteralPath $Root)) { return }
    foreach ($pattern in $Patterns) {
        foreach ($item in @(Get-ChildItem -LiteralPath $Root -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -like $pattern })) {
            if ($PSCmdlet.ShouldProcess($item.FullName, 'Remove build.ps1 artifacts')) {
                Remove-Item -LiteralPath $item.FullName -Recurse -Force
                Write-Output "Removed: $($item.FullName)"
            }
        }
    }
}

Remove-MatchingItems -Root $artifactsRoot -Patterns @(
    'server-*',
    'vsix-stage',
    'McpVs2010.Bridge-*.vsix',
    'McpVs2010-Deployment-*'
)

if ($AllBuildOutputs) {
    foreach ($sourceDirectory in @('src\McpVs2010.Bridge', 'src\McpVs2010.Server')) {
        $sourceRoot = Join-Path $projectRoot $sourceDirectory
        Remove-MatchingItems -Root $sourceRoot -Patterns @('bin', 'obj')
    }
}

Write-Output 'Clean completed.'
