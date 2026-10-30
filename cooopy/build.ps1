# cooopy 构建 / 安装脚本
# 用法：
#   .\build.ps1            只构建到 .\app
#   .\build.ps1 -Install   构建并安装到 %LOCALAPPDATA%\Programs\cooopy

param([switch]$Install)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

Write-Host "==> 构建 cooopy（self-contained, win-x64）" -ForegroundColor Cyan
& $dotnet publish (Join-Path $root 'cooopy.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=None `
    -o (Join-Path $root 'app')
if ($LASTEXITCODE -ne 0) { throw "构建失败" }

$exe = Join-Path $root 'app\cooopy.exe'
Write-Host "==> 构建完成: $exe" -ForegroundColor Green

if (-not $Install) { return }

$target = Join-Path $env:LOCALAPPDATA 'Programs\cooopy'
Write-Host "==> 安装到 $target" -ForegroundColor Cyan

Get-Process cooopy, dotnet -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowTitle -eq 'cooopy' -or $_.ProcessName -eq 'cooopy' } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $root 'app\*') $target -Recurse -Force

$installed = Join-Path $target 'cooopy.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'cooopy.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $installed
$link.WorkingDirectory = $target
$link.Description = 'cooopy 文本剪藏'
$link.Save()

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Set-ItemProperty -Path $runKey -Name 'cooopy' -Value "`"$installed`" --silent"

Start-Process -FilePath $installed
Write-Host "==> 已安装并启动：$installed" -ForegroundColor Green
Write-Host "    开机自启已开启，可在 设置 中关闭" -ForegroundColor Gray
