[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$packageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$vsix = @(Get-ChildItem -LiteralPath $packageRoot -Filter 'McpVs2010.Bridge-*.vsix' -File | Where-Object { $_.BaseName -match '^McpVs2010\.Bridge-\d+(\.\d+){1,3}$' } | Sort-Object { [version]($_.BaseName -replace '^McpVs2010\.Bridge-', '') } -Descending)
if ($vsix.Count -eq 0) { $fallback = Join-Path $packageRoot 'McpVs2010.Bridge.Latest.vsix'; if (Test-Path -LiteralPath $fallback) { $vsix = @([System.IO.FileInfo]$fallback) } }
if ($vsix.Count -eq 0) { throw "The VSIX to install could not be found: $packageRoot" }
Write-Output "VSIX to install: $($vsix[0].FullName)"
& (Join-Path $packageRoot 'Install-Vsix.ps1') -VsixPath $vsix[0].FullName
exit $LASTEXITCODE
