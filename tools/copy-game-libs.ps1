<#
.SYNOPSIS
  Copy the game + MelonLoader managed DLLs needed to build the Stash-based mods out of your own Schedule I
  install into Stash/lib/il2cpp/{game,melonloader}. These are TVGS/third-party binaries and are never
  committed (see .gitignore) - each builder supplies them from their own copy of the game.

.EXAMPLE
  ./tools/copy-game-libs.ps1 -GameRoot "D:\SteamLibrary\steamapps\common\Schedule I"
#>
param(
  [Parameter(Mandatory = $true)]
  [string]$GameRoot
)

$ErrorActionPreference = "Stop"

$stash   = Split-Path -Parent $PSScriptRoot
$gameDst = Join-Path $stash "lib/il2cpp/game"
$mlDst   = Join-Path $stash "lib/il2cpp/melonloader"

$il2cpp = Join-Path $GameRoot "MelonLoader/Il2CppAssemblies"
$mlNet6 = Join-Path $GameRoot "MelonLoader/net6"

if (-not (Test-Path $il2cpp)) { throw "Il2CppAssemblies not found at '$il2cpp'. Launch the game once with MelonLoader so it generates them, then retry." }
if (-not (Test-Path $mlNet6)) { $mlNet6 = Join-Path $GameRoot "MelonLoader/net6.0" }

New-Item -ItemType Directory -Force -Path $gameDst, $mlDst | Out-Null

# The managed game DLLs FullHouse references (extend this list as more Stash source is added).
$gameDlls = @(
  "Assembly-CSharp.dll",
  "Il2Cppmscorlib.dll",
  "Il2CppSystem.dll",
  "UnityEngine.CoreModule.dll",
  "UnityEngine.UI.dll",
  "UnityEngine.UIModule.dll",
  "Unity.TextMeshPro.dll",
  "UnityEngine.TextRenderingModule.dll",
  "Il2Cppcom.rlabrecque.steamworks.net.dll"
)
$mlDlls = @("MelonLoader.dll", "0Harmony.dll", "Il2CppInterop.Runtime.dll")

foreach ($d in $gameDlls) {
  $src = Join-Path $il2cpp $d
  if (Test-Path $src) { Copy-Item $src $gameDst -Force; Write-Host "  game/$d" } else { Write-Warning "missing: $d (in $il2cpp)" }
}
foreach ($d in $mlDlls) {
  $src = Join-Path $mlNet6 $d
  if (Test-Path $src) { Copy-Item $src $mlDst -Force; Write-Host "  melonloader/$d" } else { Write-Warning "missing: $d (in $mlNet6)" }
}

Write-Host "Done. Build with: dotnet build ../FullHouse/FullHouse.csproj -c Release -p:WorkspaceLibPath=`"$stash/lib`""
