#Requires -Version 7.4
<#
.SYNOPSIS
  构建 Release 并部署到游戏的 Mods 目录（旧版先备份）。游戏在运行时直接退出，不覆盖。
.PARAMETER GameDir
  游戏安装目录；缺省读环境变量 PS_GAME_DIR。
.PARAMETER Audit
  不构建也不部署，只读上一次游戏运行的 Player.log 里本 mod 的 [ModLoader] audit 行，
  对比清单声明的 Capabilities，提示未声明的能力（等价于 F8 mod-scan 的 detected 部分，不替代它）。
#>
param(
    [string]$GameDir = $env:PS_GAME_DIR,
    [switch]$Audit,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if (-not $GameDir) { throw '传 -GameDir 或设置环境变量 PS_GAME_DIR 为游戏安装目录。' }
$game = (Resolve-Path -LiteralPath $GameDir).Path

[xml]$manifest = Get-Content -LiteralPath (Join-Path $repo 'manifest.xml') -Raw
$id = [string]$manifest.Manifest.ID
$version = [string]$manifest.Manifest.ModVersion
$declared = @($manifest.Manifest.Capabilities.Capability | Where-Object { $_ } | ForEach-Object { [string]$_ })

if ($Audit) {
    $log = Join-Path $env:USERPROFILE 'AppData\LocalLow\Questing Goose Studio\Probably Stolen\Player.log'
    if (-not (Test-Path -LiteralPath $log)) { throw "找不到日志：$log" }
    # 游戏运行时日志被占用：用共享读打开
    $fs = [System.IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
    try { $text = (New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)).ReadToEnd() } finally { $fs.Dispose() }
    $hit = [regex]::Matches($text, "(?m)^\[ModLoader\] audit: $([regex]::Escape($id)) .*$") | Select-Object -Last 1
    if (-not $hit) { throw "日志里没有 $id 的 audit 行：游戏还没加载过它，或日志已被新一轮启动覆盖。" }
    $line = [pscustomobject]@{ Line = $hit.Value.TrimEnd() }
    Write-Output $line.Line
    $m = [regex]::Match($line.Line, 'caps=(?<caps>.*?) hash=')
    $detected = @()
    if ($m.Success) {
        $detected = $m.Groups['caps'].Value -split ',\s*' | ForEach-Object { $_.Trim() } |
            Where-Object { $_ -and $_ -ne 'none' -and $_ -ne 'declared' }
    }
    $missing = @($detected | Where-Object { $declared -notcontains $_ })
    Write-Output ("清单声明：{0}" -f ($(if ($declared.Count) { $declared -join ', ' } else { '（空）' })))
    Write-Output ("日志检测：{0}" -f ($(if ($detected.Count) { $detected -join ', ' } else { '（无）' })))
    if ($missing.Count -gt 0) { Write-Warning ("检测到但清单未声明：{0}。最终仍以 F8 mod-scan 为准。" -f ($missing -join ', ')); exit 1 }
    Write-Output '检测到的能力均已声明（最终仍以 F8 mod-scan 为准）。'
    return
}

if (Get-Process -Name 'Probably Stolen' -ErrorAction SilentlyContinue) {
    throw '游戏正在运行：先退出游戏再部署（运行中 DLL 被占用，覆盖会失败或留下半套文件）。'
}

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -GameDir $game
    if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) { throw "构建失败，退出码 $LASTEXITCODE。" }
}
$dll = Join-Path $repo 'bin/Release/netstandard2.1/ItemDetails.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw "没有构建产物：$dll" }

$modDir = Join-Path $game "Mods/$id"
if (Test-Path -LiteralPath $modDir) {
    $backup = Join-Path $game ("ModsBackup/deploy-{0:yyyy-MM-dd-HHmm}" -f (Get-Date))
    New-Item -ItemType Directory -Force $backup | Out-Null
    Copy-Item -Path (Join-Path $modDir '*') -Destination $backup -Recurse -Force
    Write-Output "已备份旧版 -> $backup"
}
New-Item -ItemType Directory -Force (Join-Path $modDir 'Localization') | Out-Null
Copy-Item -LiteralPath $dll -Destination $modDir -Force
# 与发布包同一组文件：清单、预览图（模组列表要用）、说明与许可
foreach ($name in @('manifest.xml', 'README.md', 'CONTRIBUTING.md', 'LICENSE', 'preview.png')) {
    Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $modDir -Force
}
Copy-Item -Path (Join-Path $repo 'Localization/*.csv') -Destination (Join-Path $modDir 'Localization') -Force

$deployed = Get-Item -LiteralPath (Join-Path $modDir 'ItemDetails.dll')
Write-Output ("已部署 {0} v{1} -> {2}" -f $id, $version, $modDir)
Write-Output ("DLL {0:N0} 字节，修改于 {1:yyyy-MM-dd HH:mm:ss}；声明的 Capabilities：{2}" -f
    $deployed.Length, $deployed.LastWriteTime, ($(if ($declared.Count) { $declared -join ', ' } else { '（空）' })))
Write-Output '下一步：启动游戏激活后，F8 执行 mod-scan item_details；退出后可运行 scripts/deploy.ps1 -Audit 对照日志。'
