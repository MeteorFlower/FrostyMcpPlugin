# Build FrostyMcpPlugin Release x64.
#
# This machine's VS BuildTools has no x64-hosted C++ compiler, so the
# FrostyHash.vcxproj project reference cannot build (LNK1112). The existing
# FrostyHash.dll is staged where that reference resolves it and the C# plugin
# is built with BuildProjectReferences=false. The plugin source uses no
# FrostyHash type; the reference only orders the build.
#
#   powershell -ExecutionPolicy Bypass -File build_plugin.ps1
#   powershell -ExecutionPolicy Bypass -File build_plugin.ps1 -Install
#
# -Install copies the DLL into the editor's Plugins folder. Close the editor
# first (save the project before killing it); a running editor keeps the old
# DLL mapped.

param(
    [switch]$Install,
    [string]$EditorPlugins = 'E:\PVZ\GW_mod\FrostyEditor-BFN\Plugins'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$suite = Resolve-Path (Join-Path $root '..\..')
$msbuild = 'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe'

$stagedHash = Join-Path $suite 'FrostySdk\bin\Release\Final\FrostyHash.dll'
$hashDir = Join-Path $suite 'FrostyHash\x64\Release - Final'

if (-not (Test-Path $msbuild)) {
    throw "MSBuild not found: $msbuild"
}
if (-not (Test-Path $stagedHash)) {
    throw "FrostyHash.dll not found: $stagedHash (build FrostySdk first)"
}

New-Item -ItemType Directory -Force $hashDir | Out-Null
Copy-Item $stagedHash (Join-Path $hashDir 'FrostyHash.dll') -Force

& $msbuild (Join-Path $root 'FrostyMcpPlugin.csproj') `
    '/p:Configuration=Release - Final' '/p:Platform=x64' `
    '/p:BuildProjectReferences=false' /m
if ($LASTEXITCODE -ne 0) {
    throw "MSBuild failed with exit code $LASTEXITCODE"
}

$dll = Join-Path $root 'bin\Release\Final\FrostyMcpPlugin.dll'
if (-not (Test-Path $dll)) {
    throw "Plugin DLL not found after build: $dll"
}
$info = Get-Item $dll
Write-Host "Built: $($info.FullName)"
Write-Host "       $($info.Length) bytes, $($info.LastWriteTime)"

if ($Install) {
    if (Get-Process FrostyEditor -ErrorAction SilentlyContinue) {
        throw "FrostyEditor is running; close it before installing (save the project first)"
    }
    $target = Join-Path $EditorPlugins 'FrostyMcpPlugin.dll'
    Copy-Item $dll $target -Force
    Write-Host "Installed: $target"
}
