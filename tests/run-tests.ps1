<#
  Runs the offline tests: JSON bodies, backoff, the report queue and the registration and
  reporting state machine, without the game. Exit code 0 means every check passed.
  Usage: .\run-tests.ps1
#>
param(
    [string]$CscDll
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = (Resolve-Path (Join-Path $here '..')).Path

if (-not $CscDll -and $env:CSC_DLL) { $CscDll = $env:CSC_DLL }
if (-not $CscDll) {
    $dotnetCmd = Get-Command dotnet -ErrorAction Stop
    $candidates = foreach ($line in (& $dotnetCmd.Source --list-sdks)) {
        if ($line -match '^(\S+)\s+\[(.+)\]$') {
            $dll = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
            if (Test-Path -LiteralPath $dll) {
                $ver = $null
                if (-not [Version]::TryParse((($Matches[1] -split '-')[0]), [ref]$ver)) { $ver = [Version]'0.0' }
                [pscustomobject]@{ Version = $ver; Path = $dll }
            }
        }
    }
    $best = $candidates | Sort-Object Version -Descending | Select-Object -First 1
    if (-not $best) { throw 'No .NET SDK with Roslyn\bincore\csc.dll found (pass -CscDll).' }
    $CscDll = $best.Path
}

$fx = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath $fx)) { $fx = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$references = 'mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Net.Http.dll' | ForEach-Object { Join-Path $fx $_ }

# Only the sources that do not reference the game or LabAPI.
$coreFiles = 'ApiClient.cs', 'IReporterHost.cs', 'Json.cs', 'Payloads.cs', 'PluginConfig.cs', 'PluginInfo.cs',
    'ReportQueue.cs', 'Reporter.cs', 'RetryBackoff.cs', 'StoredCredential.cs'
$sources = @($coreFiles | ForEach-Object { Join-Path $root "src\$_" }) + (Join-Path $here 'Tests.cs')

$binDir = Join-Path $here 'bin'
New-Item -ItemType Directory -Force -Path $binDir | Out-Null
$outExe = Join-Path $binDir 'Tests.exe'

$rsp = New-Object System.Collections.Generic.List[string]
$rsp.Add('/nostdlib+')
$rsp.Add('/nologo')
$rsp.Add('/target:exe')
$rsp.Add('/langversion:latest')
$rsp.Add('/debug-')
$rsp.Add('/utf8output')
$rsp.Add("/out:`"$outExe`"")
foreach ($r in $references) { $rsp.Add("/reference:`"$r`"") }
foreach ($s in $sources) { $rsp.Add("`"$s`"") }
$rspPath = Join-Path $binDir 'tests.rsp'
[IO.File]::WriteAllLines($rspPath, $rsp, (New-Object System.Text.UTF8Encoding($false)))

$dotnetExe = (Get-Command dotnet -ErrorAction Stop).Source
& $dotnetExe $CscDll /noconfig "@$rspPath"
if ($LASTEXITCODE -ne 0) { throw "test build failed with exit code $LASTEXITCODE" }

& $outExe
exit $LASTEXITCODE
