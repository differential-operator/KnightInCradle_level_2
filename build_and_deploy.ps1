<#
  build_and_deploy.ps1 —— 按“目标游戏安装”编译并部署 KnightInCradle

  为什么必须按目标安装编译：
    KIC 的补丁是用 Harmony 打到游戏的 Managed 程序集上的，如果 DLL 是用**别的版本**
    的游戏程序集编译的，运行时会解析不到类型，表现为
    `[KIC][补丁] … 成功 / N 失败（HarmonyException: IL Compile Error）` —— 补丁挂不上。
    （0.30g 新版上踩过这个坑：用旧版引用编译 → 21 个补丁挂不上；按新版编译 → 71/0 全通过。）

  用法（示例，路径按你的实际安装改）：
    powershell -ExecutionPolicy Bypass -File "D:\Documents\Knight In Cradle\KnightInCradle\build_and_deploy.ps1" `
      -GameRoot "D:\Documents\AliceInCradle\AICmultiplayer\AliceInCradle Win ver030\AliceInCradle\_ver030"

  参数：
    -GameRoot  目标游戏根目录 = 含 AliceInCradle.exe / AliceInCradle_Data / BepInEx 的那一层
    -NoBuild   只部署，不重新编译（复用 bin\Release 里现成的 DLL）
#>
param(
    [Parameter(Mandatory = $true)][string]$GameRoot,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$ProjectRoot = $PSScriptRoot

function Resolve-GameRoot([string]$root) {
    if (-not (Test-Path -LiteralPath $root)) { throw "目录不存在：$root" }
    if (Test-Path -LiteralPath (Join-Path $root "AliceInCradle.exe")) { return (Resolve-Path -LiteralPath $root).Path }
    # 允许把上一级目录传进来：往下找 1~2 层里的 AliceInCradle.exe
    $hit = Get-ChildItem -LiteralPath $root -Recurse -Depth 2 -Filter "AliceInCradle.exe" -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($hit) { return (Split-Path -Parent $hit.FullName) }
    throw "在 $root 及其下两层都没找到 AliceInCradle.exe，请把 -GameRoot 指向含它的目录"
}

$GameRoot = Resolve-GameRoot $GameRoot
Write-Host "游戏根目录: $GameRoot" -ForegroundColor Cyan

# ---- 目标安装的版本指纹（便于和别的安装对照）----
$asm = Join-Path $GameRoot "AliceInCradle_Data\Managed\Assembly-CSharp.dll"
if (Test-Path -LiteralPath $asm) {
    $len = (Get-Item -LiteralPath $asm).Length
    $sha = (Get-FileHash -LiteralPath $asm -Algorithm SHA256).Hash
    Write-Host ("游戏程序集: Assembly-CSharp.dll  {0} bytes  {1}" -f $len, $sha.Substring(0, 16)) -ForegroundColor Cyan
} else {
    throw "找不到 $asm"
}

# ---- 编译（用目标安装的程序集做引用）----
$SourceDll = Join-Path $ProjectRoot "bin\Release\net472\KnightInCradle.dll"
if (-not $NoBuild) {
    Write-Host "开始编译（AicRoot = $GameRoot）..." -ForegroundColor Cyan
    Push-Location $ProjectRoot
    try {
        & dotnet build -c Release -p:AicRoot="$GameRoot"
        if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败（退出码 $LASTEXITCODE）" }
    } finally {
        Pop-Location
    }
}
if (-not (Test-Path -LiteralPath $SourceDll)) { throw "找不到 $SourceDll（先执行 dotnet build -c Release）" }

# ---- 部署 ----
$PluginsDir = Join-Path $GameRoot "BepInEx\plugins"
$DstDir     = Join-Path $PluginsDir "KnightInCradle"
New-Item -ItemType Directory -Path $DstDir -Force | Out-Null

# 提醒：plugins 根目录若还留着一份同名 DLL，BepInEx 只会加载其中一份（通常是根目录那份），
# 会导致“新构建没生效”。
$StrayDll = Join-Path $PluginsDir "KnightInCradle.dll"
if (Test-Path -LiteralPath $StrayDll) {
    Write-Warning "检测到重复插件 $StrayDll —— 会被 BepInEx 优先加载。建议删除或改名后再启动。"
}

Copy-Item -LiteralPath $SourceDll -Destination (Join-Path $DstDir "KnightInCradle.dll") -Force
Write-Host "已部署 DLL -> $(Join-Path $DstDir 'KnightInCradle.dll')" -ForegroundColor Green

# 素材（以工程为准整体覆盖：工程 assets/hk 是唯一素材源）
$SrcAssets = Join-Path $ProjectRoot "assets\hk"
$DstAssets = Join-Path $DstDir "assets\hk"
if (Test-Path -LiteralPath $SrcAssets) {
    New-Item -ItemType Directory -Path $DstAssets -Force | Out-Null
    Copy-Item -Path (Join-Path $SrcAssets "*") -Destination $DstAssets -Recurse -Force
    $n = (Get-ChildItem -LiteralPath $DstAssets -Recurse -File | Measure-Object).Count
    Write-Host "已同步素材 -> $DstAssets（$n 个文件）" -ForegroundColor Green
} else {
    Write-Warning "工程里没有 assets\hk（$SrcAssets），跳过素材同步。"
}

# 护符 UI：CharmUiLoader 运行时读插件目录下 charm_ui\layout.json + images\*.png，
# 不同步会表现为“护符界面空白 / 素材缺失”。同样以工程为准整体覆盖。
$SrcCharmUi = Join-Path $ProjectRoot "charm_ui"
$DstCharmUi = Join-Path $DstDir "charm_ui"
if (Test-Path -LiteralPath $SrcCharmUi) {
    New-Item -ItemType Directory -Path $DstCharmUi -Force | Out-Null
    Copy-Item -Path (Join-Path $SrcCharmUi "*") -Destination $DstCharmUi -Recurse -Force
    $n = (Get-ChildItem -LiteralPath $DstCharmUi -Recurse -File | Measure-Object).Count
    Write-Host "已同步护符 UI -> $DstCharmUi（$n 个文件）" -ForegroundColor Green
} else {
    Write-Warning "工程里没有 charm_ui（$SrcCharmUi），跳过护符 UI 同步。"
}

# 键位.txt：目标没有才放一份，避免覆盖你在游戏目录里改好的键位（来源 = 工程根目录的 键位.txt）
$DstKey = Join-Path $DstDir "键位.txt"
if (-not (Test-Path -LiteralPath $DstKey)) {
    $SrcKey = Join-Path $ProjectRoot "键位.txt"
    if (Test-Path -LiteralPath $SrcKey) {
        Copy-Item -LiteralPath $SrcKey -Destination $DstKey -Force
        Write-Host "已放置键位.txt（来自 $SrcKey）" -ForegroundColor Green
    } else {
        Write-Warning "目标没有 键位.txt，也没找到 $SrcKey —— 首次启动模组会自动生成一份默认模板。"
    }
} else {
    Write-Host "键位.txt 已存在，保持不动。" -ForegroundColor Yellow
}

$finalSha = (Get-FileHash -LiteralPath (Join-Path $DstDir "KnightInCradle.dll") -Algorithm SHA256).Hash
Write-Host ""
Write-Host "完成。部署后 SHA256: $finalSha" -ForegroundColor Green
Write-Host "启动游戏后，日志里确认这一行：" -ForegroundColor Cyan
Write-Host "  [KIC][补丁] KnightInCradle build=… 71 成功 / 0 失败 dll=…\KnightInCradle\KnightInCradle.dll (…)" -ForegroundColor Cyan
