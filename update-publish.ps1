# 把 publish_staging 里的最新构建同步到 publish。
# 游戏正在运行时 dll 被锁定、无法覆盖，脚本会提示先关闭游戏。
# 用法：关闭游戏后，右键本文件 → 使用 PowerShell 运行；或在本目录执行 .\update-publish.ps1

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }

$src = Join-Path $root 'publish_staging'
$dst = Join-Path $root 'publish'

if (-not (Test-Path $src)) {
    Write-Host "找不到 $src ，请先重新发布一次。" -ForegroundColor Red
    exit 1
}

$running = Get-Process -Name 'Minesweeper' -ErrorAction SilentlyContinue
if ($running) {
    $pids = ($running | ForEach-Object { $_.Id }) -join ', '
    Write-Host "扫雷正在运行（PID $pids），文件被占用。" -ForegroundColor Yellow
    Write-Host "请先关闭游戏，然后重新运行本脚本。" -ForegroundColor Yellow
    exit 1
}

if (-not (Test-Path $dst)) { New-Item -ItemType Directory -Path $dst | Out-Null }

Copy-Item -Path (Join-Path $src '*') -Destination $dst -Force -Recurse
Remove-Item -Path $src -Recurse -Force

Write-Host "已更新到 $dst" -ForegroundColor Green
Write-Host "现在可以双击 $(Join-Path $dst 'Minesweeper.exe') 启动了。"
