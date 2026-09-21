param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$ProjectRoot = $PSScriptRoot
$SourceDll = Join-Path $ProjectRoot "bin\$Configuration\net472\KnightInCradle.dll"
$GamePluginsDir = Join-Path $ProjectRoot "..\AliceInCradle Win ver030\AliceInCradle_ver030\BepInEx\plugins"
$GameDllDir = Join-Path $GamePluginsDir "KnightInCradle"
$SourceAssets = Join-Path $ProjectRoot "assets\hk"
$GameAssetsDir = Join-Path $GamePluginsDir "KnightInCradle\assets\hk"

if (-not (Test-Path -LiteralPath $SourceDll)) {
    Write-Error "未找到 $SourceDll，请先执行 dotnet build"
}

New-Item -ItemType Directory -Path $GamePluginsDir -Force | Out-Null
New-Item -ItemType Directory -Path $GameDllDir -Force | Out-Null

# 只部署到 plugins\KnightInCradle\ 这一处：BepInEx 是递归扫描 *.dll 的，
# 如果 plugins 根目录还留着一份 KnightInCradle.dll，会出现两份同 GUID 插件，
# BepInEx 只加载其中一份（通常先扫到根目录那份），导致“新构建没生效”。
$StrayDll = Join-Path $GamePluginsDir "KnightInCradle.dll"
if (Test-Path -LiteralPath $StrayDll) {
    Write-Warning "检测到重复插件 $StrayDll —— BepInEx 会优先加载它，导致新构建不生效。请删除或改名后再部署。"
}

Copy-Item -LiteralPath $SourceDll -Destination (Join-Path $GameDllDir "KnightInCradle.dll") -Force
Write-Host "已部署到 $GameDllDir\KnightInCradle.dll"

if (Test-Path -LiteralPath $SourceAssets) {
    New-Item -ItemType Directory -Path $GameAssetsDir -Force | Out-Null
    Copy-Item -Path (Join-Path $SourceAssets "*") -Destination $GameAssetsDir -Recurse -Force
    Write-Host "素材已部署到 $GameAssetsDir"
} else {
    Write-Host "警告: 未找到素材目录 $SourceAssets"
}
