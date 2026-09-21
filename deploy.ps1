<#
  deploy.ps1 —— 把已编译好的 DLL + 素材 + 护符 UI + 键位.txt 部署到目标安装（不重新编译）。
  要“按目标安装重新编译再部署”请用 build_and_deploy.ps1。

  用法：
    powershell -ExecutionPolicy Bypass -File deploy.ps1 -Configuration Release
    powershell -ExecutionPolicy Bypass -File deploy.ps1 -Configuration Release -GameRoot "D:\...\AliceInCradle_ver030"
#>
param(
    [string]$Configuration = "Debug",
    [string]$GameRoot = ""
)

$ErrorActionPreference = "Stop"

$ProjectRoot = $PSScriptRoot
$SourceDll = Join-Path $ProjectRoot "bin\$Configuration\net472\KnightInCradle.dll"

# 目标安装 = 含 AliceInCradle.exe / AliceInCradle_Data / BepInEx 的那一层。
# 默认指向工程旁边的单机安装；要部署到联机版或别处时用 -GameRoot 指定。
if ([string]::IsNullOrWhiteSpace($GameRoot)) {
    $GameRoot = Join-Path $ProjectRoot "..\AliceInCradle_ver030"
}
if (-not (Test-Path -LiteralPath $GameRoot)) {
    throw "找不到目标安装：$GameRoot（用 -GameRoot 指定游戏根目录）"
}
$GameRoot = (Resolve-Path -LiteralPath $GameRoot).Path
if (-not (Test-Path -LiteralPath (Join-Path $GameRoot "BepInEx"))) {
    throw "$GameRoot 下没有 BepInEx 目录——请把 -GameRoot 指向含 BepInEx 的游戏根目录。"
}

$GamePluginsDir = Join-Path $GameRoot "BepInEx\plugins"
$GameDllDir = Join-Path $GamePluginsDir "KnightInCradle"
$SourceAssets = Join-Path $ProjectRoot "assets\hk"
$GameAssetsDir = Join-Path $GameDllDir "assets\hk"
$SourceCharmUi = Join-Path $ProjectRoot "charm_ui"
$GameCharmUiDir = Join-Path $GameDllDir "charm_ui"

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

# 护符 UI：CharmUiLoader 运行时读插件目录下 charm_ui\layout.json + images\*.png
if (Test-Path -LiteralPath $SourceCharmUi) {
    New-Item -ItemType Directory -Path $GameCharmUiDir -Force | Out-Null
    Copy-Item -Path (Join-Path $SourceCharmUi "*") -Destination $GameCharmUiDir -Recurse -Force
    $n = (Get-ChildItem -LiteralPath $GameCharmUiDir -Recurse -File | Measure-Object).Count
    Write-Host "护符 UI 已部署到 $GameCharmUiDir（$n 个文件）"
} else {
    Write-Host "警告: 未找到护符 UI 目录 $SourceCharmUi"
}

# 键位.txt：目标没有才放一份，避免覆盖你在游戏目录里改好的键位
$DstKey = Join-Path $GameDllDir "键位.txt"
if (-not (Test-Path -LiteralPath $DstKey)) {
    $SrcKey = Join-Path $ProjectRoot "键位.txt"
    if (Test-Path -LiteralPath $SrcKey) {
        Copy-Item -LiteralPath $SrcKey -Destination $DstKey -Force
        Write-Host "已放置键位.txt（来自 $SrcKey）"
    } else {
        Write-Host "警告: 目标没有 键位.txt，工程里也没有 $SrcKey"
    }
} else {
    Write-Host "键位.txt 已存在，保持不动。"
}
