param(
    [Parameter(Mandatory = $true)][string[]]$Name,
    [string]$SpriteDir = "assets\hk\sprites",
    [string]$GameSpritesDir = "..\AliceInCradle_ver029\BepInEx\plugins\KnightInCradle\assets\hk\sprites"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

foreach ($n in $Name) {
    $path = Join-Path $SpriteDir "$n.png"
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Host "未找到: $path"
        continue
    }
    $bmp = [System.Drawing.Bitmap]::FromFile($path)
    try {
        $bmp.RotateFlip([System.Drawing.RotateFlipType]::RotateNoneFlipX)
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Host "已水平翻转: $n"
    }
    finally {
        $bmp.Dispose()
    }
}

# 自动部署到游戏目录
if (Test-Path -LiteralPath $GameSpritesDir) {
    foreach ($n in $Name) {
        $src = Join-Path $SpriteDir "$n.png"
        if (Test-Path -LiteralPath $src) {
            Copy-Item -LiteralPath $src -Destination (Join-Path $GameSpritesDir "$n.png") -Force
        }
    }
    Write-Host "已部署到游戏目录。"
} else {
    Write-Host "警告: 未找到游戏目录 $GameSpritesDir，请手动复制。"
}

Write-Host "完成。启动游戏测试即可。"
