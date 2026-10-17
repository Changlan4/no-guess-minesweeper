# 在桌面创建「无猜扫雷」快捷方式。
# 不写死绝对路径：脚本放在项目根目录，自动定位到 publish 目录。
$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }

$candidates = @(
    (Join-Path $root 'publish\Minesweeper.exe'),
    (Join-Path $root 'publish_staging\Minesweeper.exe')
)
$exe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $exe) {
    Write-Error '找不到 Minesweeper.exe。请先在项目根目录执行：dotnet publish src/Minesweeper/Minesweeper.csproj -c Release -o publish'
    exit 1
}

$ws = New-Object -ComObject WScript.Shell
$lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) '无猜扫雷.lnk'
$sc = $ws.CreateShortcut($lnk)
$sc.TargetPath = $exe
$sc.WorkingDirectory = Split-Path -Parent $exe

# 图标跟着 exe 走（图标已嵌进程序集）
$icon = Join-Path $root 'src\Minesweeper\app.ico'
if (Test-Path $icon) { $sc.IconLocation = "$icon,0" }

$sc.Save()

Write-Host "桌面快捷方式已创建：$lnk"
Write-Host "指向：$exe"
