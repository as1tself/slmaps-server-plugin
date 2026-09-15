<#
  Builds bin\SlmapsServerPlugin.dll for net48/Mono with the .NET SDK's Roslyn. No NuGet, no network.
  Usage: .\build.ps1 -ManagedDir "<server>\SCPSL_Data\Managed"
  The folder can also come from the SCPSL_MANAGED_DIR environment variable.
#>
param(
    [string]$ManagedDir,
    [string]$CscDll
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not $ManagedDir) { $ManagedDir = $env:SCPSL_MANAGED_DIR }
if (-not $ManagedDir) {
    throw 'Usage: .\build.ps1 -ManagedDir "<server>\SCPSL_Data\Managed" (or set SCPSL_MANAGED_DIR).'
}
if (-not (Test-Path -LiteralPath $ManagedDir)) {
    throw "Managed folder not found: $ManagedDir"
}
$ManagedDir = (Resolve-Path -LiteralPath $ManagedDir).Path

if (-not $CscDll -and $env:CSC_DLL) { $CscDll = $env:CSC_DLL }
if (-not $CscDll) {
    $dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnetCmd) { throw 'dotnet was not found on PATH (install the .NET SDK or pass -CscDll).' }
    $sdkLines = & $dotnetCmd.Source --list-sdks
    $candidates = foreach ($line in $sdkLines) {
        if ($line -match '^(\S+)\s+\[(.+)\]$') {
            $dll = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
            if (Test-Path -LiteralPath $dll) {
                $ver = $null
                $numeric = ($Matches[1] -split '-')[0]
                if (-not [Version]::TryParse($numeric, [ref]$ver)) { $ver = [Version]'0.0' }
                [pscustomobject]@{ Version = $ver; Path = $dll }
            }
        }
    }
    $best = $candidates | Sort-Object Version -Descending | Select-Object -First 1
    if (-not $best) { throw 'No .NET SDK with Roslyn\bincore\csc.dll found (pass -CscDll).' }
    $CscDll = $best.Path
}
if (-not (Test-Path -LiteralPath $CscDll)) { throw "csc.dll not found: $CscDll" }

$references = @(
    'mscorlib.dll', 'System.dll', 'System.Core.dll', 'netstandard.dll', 'System.Net.Http.dll',
    'LabApi.dll', 'Assembly-CSharp.dll', 'Assembly-CSharp-firstpass.dll', 'UnityEngine.CoreModule.dll',
    'Mirror.dll', 'NorthwoodLib.dll', 'YamlDotNet.dll'
)
foreach ($r in $references) {
    if (-not (Test-Path -LiteralPath (Join-Path $ManagedDir $r))) { throw "Missing reference: $(Join-Path $ManagedDir $r)" }
}

$binDir = Join-Path $root 'bin'
$objDir = Join-Path $root 'obj'
New-Item -ItemType Directory -Force -Path $binDir, $objDir | Out-Null

$sources = Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter *.cs -Recurse | Sort-Object FullName
if (-not $sources) { throw 'No source files under src\' }

$outDll = Join-Path $binDir 'SlmapsServerPlugin.dll'
$rsp = New-Object System.Collections.Generic.List[string]
$rsp.Add('/nostdlib+')
$rsp.Add('/nologo')
$rsp.Add('/target:library')
$rsp.Add('/langversion:latest')
$rsp.Add('/optimize+')
$rsp.Add('/deterministic+')
$rsp.Add('/debug-')
$rsp.Add('/warn:4')
$rsp.Add('/utf8output')
$rsp.Add("/out:`"$outDll`"")
foreach ($r in $references) { $rsp.Add("/reference:`"$(Join-Path $ManagedDir $r)`"") }
foreach ($s in $sources) { $rsp.Add("`"$($s.FullName)`"") }

$rspPath = Join-Path $objDir 'csc.rsp'
[IO.File]::WriteAllLines($rspPath, $rsp, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "csc     : $CscDll"
Write-Host "managed : $ManagedDir"
Write-Host "sources : $($sources.Count) file(s)"

$dotnetExe = (Get-Command dotnet -ErrorAction Stop).Source
& $dotnetExe $CscDll /noconfig "@$rspPath"
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }

$info = Get-Item -LiteralPath $outDll
Write-Host ("built   : {0} ({1:N0} bytes)" -f $info.FullName, $info.Length)
