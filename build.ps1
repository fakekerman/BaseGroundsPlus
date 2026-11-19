# Builds BaseGroundsPlus.dll with the C# compiler that ships with Windows (.NET Framework 4.x), no SDK needed.
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1 [-KspPath "D:\...\Kerbal Space Program"] [-Install]
param(
    [string]$KspPath = "D:\SteamLibrary\steamapps\common\Kerbal Space Program",
    [switch]$Install
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$managed = Join-Path $KspPath "KSP_x64_Data\Managed"
if (-not (Test-Path (Join-Path $managed "Assembly-CSharp.dll"))) { throw "KSP not found at '$KspPath' (pass -KspPath)" }

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe" }

$outDir = Join-Path $root "GameData\BaseGroundsPlus\Plugins"
New-Item -ItemType Directory -Force $outDir | Out-Null
$out = Join-Path $outDir "BaseGroundsPlus.dll"

$refs = @("mscorlib.dll", "System.dll", "System.Core.dll", "Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll", "UnityEngine.UI.dll")
$refs += Get-ChildItem $managed -Filter "UnityEngine*.dll" | Where-Object { $_.Name -ne "UnityEngine.UI.dll" } | ForEach-Object { $_.Name }
$refArgs = $refs | Select-Object -Unique | ForEach-Object { "/r:" + (Join-Path $managed $_) }
# Harmony (000_Harmony, required by KSPCommunityFixes / RP-1) for the part-menu cache fix
$harmony = Join-Path $KspPath "GameData\000_Harmony\0Harmony.dll"
if (-not (Test-Path $harmony)) { throw "Harmony not found at '$harmony' (install 000_Harmony)" }
$refArgs += "/r:$harmony"
$src = Get-ChildItem (Join-Path $root "Source") -Filter *.cs | ForEach-Object { $_.FullName }

& $csc /nologo /nostdlib+ /noconfig /target:library /optimize+ /debug- /warn:4 /nowarn:1701,1702 "/out:$out" $refArgs $src
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built $out"

if ($Install) {
    $dest = Join-Path $KspPath "GameData\BaseGroundsPlus"
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item -Recurse -Force (Join-Path $root "GameData\BaseGroundsPlus\*") $dest
    Write-Host "Installed to $dest"
}
