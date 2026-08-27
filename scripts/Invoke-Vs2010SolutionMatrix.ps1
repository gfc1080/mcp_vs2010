[CmdletBinding()]
param(
    [int]$ProcessId = 14496,

    [string]$ExpectedSolutionPath = 'C:\work\svn\5.1.1100\Viewer\Viewer.sln',

    [string]$OutputPath,

    [switch]$ListBuildCommandsOnly
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputPath = Join-Path $projectRoot "artifacts\vs2010-solution-matrix-$timestamp.json"
}
else {
    $OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
}

$outputDirectory = Split-Path $OutputPath -Parent
if (-not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$envDtePath = 'C:\Program Files (x86)\Microsoft Visual Studio 10.0\Common7\IDE\PublicAssemblies\EnvDTE.dll'
if (Test-Path -LiteralPath $envDtePath) {
    Add-Type -Path $envDtePath
}

if (-not ('Vs2010BuildMatrix.NativeMethods' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Vs2010BuildMatrix
{
    public static class NativeMethods
    {
        [DllImport("ole32.dll")]
        private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable runningObjectTable);

        [DllImport("ole32.dll")]
        private static extern int CreateBindCtx(int reserved, out IBindCtx bindContext);

        public static object GetVisualStudioDte(int processId)
        {
            IRunningObjectTable runningObjectTable;
            if (GetRunningObjectTable(0, out runningObjectTable) != 0)
            {
                throw new COMException("Running Object Table을 열 수 없습니다.");
            }

            IBindCtx bindContext;
            if (CreateBindCtx(0, out bindContext) != 0)
            {
                throw new COMException("COM 바인딩 컨텍스트를 만들 수 없습니다.");
            }

            IEnumMoniker enumerator;
            runningObjectTable.EnumRunning(out enumerator);
            enumerator.Reset();
            IMoniker[] monikers = new IMoniker[1];
            string suffix = "VisualStudio.DTE.10.0:" + processId;

            while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
            {
                string displayName = null;
                try
                {
                    monikers[0].GetDisplayName(bindContext, null, out displayName);
                    if (displayName != null && displayName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        object dte;
                        runningObjectTable.GetObject(monikers[0], out dte);
                        return dte;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            throw new COMException("PID " + processId + " VS2010 DTE를 Running Object Table에서 찾을 수 없습니다.");
        }
    }
}
'@
}

$dte = [Vs2010BuildMatrix.NativeMethods]::GetVisualStudioDte($ProcessId)

$solutionPath = [string]$dte.Solution.FullName
if (-not [string]::Equals(
    [System.IO.Path]::GetFullPath($solutionPath),
    [System.IO.Path]::GetFullPath($ExpectedSolutionPath),
    [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "열린 솔루션이 요청과 다릅니다. 요청=$ExpectedSolutionPath, 실제=$solutionPath"
}

if ($ListBuildCommandsOnly) {
    for ($index = 1; $index -le $dte.Commands.Count; $index++) {
        try {
            $command = $dte.Commands.Item($index)
            $name = [string]$command.Name
            if ($name -match 'BuildOnlyProject|RebuildOnlyProject|CleanOnlyProject|BuildSelection|RebuildSelection|CleanSelection') {
                Write-Output $name
            }
        }
        catch {
        }
    }
    return
}

$configurations = @(
    [pscustomobject]@{ Configuration = 'Debug';   Platform = 'Win32' },
    [pscustomobject]@{ Configuration = 'Release'; Platform = 'Win32' },
    [pscustomobject]@{ Configuration = 'Debug';   Platform = 'x64' },
    [pscustomobject]@{ Configuration = 'Release'; Platform = 'x64' }
)
$operations = @(
    [pscustomobject]@{ Name = 'Clean';   Command = 'Build.CleanSolution' },
    [pscustomobject]@{ Name = 'Build';   Command = 'Build.BuildSolution' },
    [pscustomobject]@{ Name = 'Rebuild'; Command = 'Build.RebuildSolution' }
)
$results = New-Object System.Collections.Generic.List[object]
$solutionBuild = $dte.Solution.SolutionBuild

function Get-BuildStateValue {
    return [int]$dte.Solution.SolutionBuild.BuildState
}

function Get-BuildStateName {
    param([int]$Value)

    switch ($Value) {
        1 { return 'vsBuildStateNotStarted' }
        2 { return 'vsBuildStateInProgress' }
        3 { return 'vsBuildStateDone' }
        default { return "Unknown($Value)" }
    }
}

function Activate-SolutionConfiguration {
    param(
        [Parameter(Mandatory = $true)][string]$Configuration,
        [Parameter(Mandatory = $true)][string]$Platform
    )

    $available = $dte.Solution.SolutionBuild.SolutionConfigurations
    $choices = New-Object System.Collections.Generic.List[string]
    for ($index = 1; $index -le $available.Count; $index++) {
        $candidate = $available.Item($index)
        $candidatePlatform = [string]$candidate.PlatformName
        [void]$choices.Add("$($candidate.Name)|$candidatePlatform")
        if ([string]::Equals([string]$candidate.Name, $Configuration, [System.StringComparison]::OrdinalIgnoreCase) -and
            [string]::Equals($candidatePlatform, $Platform, [System.StringComparison]::OrdinalIgnoreCase)) {
            $candidate.Activate()
            Start-Sleep -Milliseconds 200
            $active = $dte.Solution.SolutionBuild.ActiveConfiguration
            if (-not [string]::Equals([string]$active.Name, $Configuration, [System.StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals([string]$active.PlatformName, $Platform, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "솔루션 구성 활성화 확인 실패: $Configuration|$Platform"
            }
            return
        }
    }

    throw "솔루션 구성을 찾을 수 없습니다: $Configuration|$Platform. 사용 가능: $($choices -join ', ')"
}

function Wait-SolutionOperation {
    param([TimeSpan]$Timeout)

    $deadline = [DateTime]::UtcNow.Add($Timeout)
    $observedInProgress = $false
    $stableNotInProgress = 0
    $lastState = Get-BuildStateValue

    while ([DateTime]::UtcNow -lt $deadline) {
        $lastState = Get-BuildStateValue
        if ($lastState -eq 2) {
            $observedInProgress = $true
            $stableNotInProgress = 0
        }
        else {
            $stableNotInProgress++
            if (($observedInProgress -and $stableNotInProgress -ge 2) -or
                (-not $observedInProgress -and $stableNotInProgress -ge 20)) {
                return [pscustomobject]@{
                    ObservedInProgress = $observedInProgress
                    FinalState = Get-BuildStateName -Value $lastState
                }
            }
        }

        Start-Sleep -Milliseconds 250
    }

    try {
        $dte.ExecuteCommand('Build.Cancel', '')
    }
    catch {
    }
    throw '솔루션 작업이 2시간 안에 끝나지 않아 취소를 요청했습니다.'
}

function Get-ErrorCount {
    try {
        return [int]$dte.ToolWindows.ErrorList.ErrorItems.Count
    }
    catch {
        return -1
    }
}

Write-Output "MATRIX_BEGIN|PID=$ProcessId|SOLUTION=$solutionPath"
foreach ($configuration in $configurations) {
    Activate-SolutionConfiguration `
        -Configuration $configuration.Configuration `
        -Platform $configuration.Platform

    foreach ($operation in $operations) {
        $startedAt = [DateTime]::UtcNow
        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $exceptionText = $null
        $waitResult = $null
        $failedProjects = -1
        $errorCount = -1

        Write-Output (
            'START|{0}|{1}|{2}|{3}' -f
            $configuration.Configuration,
            $configuration.Platform,
            $operation.Name,
            $startedAt.ToString('o'))

        try {
            if ((Get-BuildStateValue) -eq 2) {
                throw 'VS2010에서 이미 솔루션 작업이 진행 중입니다.'
            }

            $dte.ExecuteCommand($operation.Command, '')
            $waitResult = Wait-SolutionOperation -Timeout ([TimeSpan]::FromHours(2))
            Start-Sleep -Milliseconds 300
            $failedProjects = [int]$dte.Solution.SolutionBuild.LastBuildInfo
            $errorCount = Get-ErrorCount
        }
        catch {
            $exceptionText = $_.Exception.ToString()
            $errorCount = Get-ErrorCount
        }
        finally {
            $stopwatch.Stop()
        }

        $success = [string]::IsNullOrEmpty($exceptionText) -and $failedProjects -eq 0
        $result = [pscustomobject]@{
            Configuration = $configuration.Configuration
            Platform = $configuration.Platform
            Operation = $operation.Name
            Command = $operation.Command
            Success = $success
            FailedProjects = $failedProjects
            ErrorCount = $errorCount
            ObservedInProgress = if ($null -eq $waitResult) { $false } else { $waitResult.ObservedInProgress }
            FinalState = if ($null -eq $waitResult) { Get-BuildStateName -Value (Get-BuildStateValue) } else { $waitResult.FinalState }
            StartedAtUtc = $startedAt.ToString('o')
            FinishedAtUtc = [DateTime]::UtcNow.ToString('o')
            DurationSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
            Error = $exceptionText
        }
        [void]$results.Add($result)
        $results | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8

        Write-Output (
            'RESULT|{0}|{1}|{2}|SUCCESS={3}|FAILED_PROJECTS={4}|ERRORS={5}|STATE={6}|SECONDS={7}' -f
            $result.Configuration,
            $result.Platform,
            $result.Operation,
            $result.Success,
            $result.FailedProjects,
            $result.ErrorCount,
            $result.FinalState,
            $result.DurationSeconds)
        if (-not [string]::IsNullOrEmpty($exceptionText)) {
            Write-Output "EXCEPTION|$($result.Configuration)|$($result.Platform)|$($result.Operation)|$exceptionText"
        }
    }
}

Write-Output "MATRIX_RESULT=$OutputPath"
Write-Output 'MATRIX_END'
