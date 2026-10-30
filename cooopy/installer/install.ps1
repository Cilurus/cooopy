# cooopy 轻量安装脚本（发布包内使用，无需管理员权限）
# 由 install.cmd 调用：把当前目录的程序复制到 %LOCALAPPDATA%\Programs\cooopy，
# 创建桌面/开始菜单快捷方式并启动。
$ErrorActionPreference = 'Stop'

$source = $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA 'Programs\cooopy'
$sourceExe = Join-Path $source 'cooopy.exe'
if (-not (Test-Path $sourceExe)) {
    throw '未找到 cooopy.exe，请把 install.cmd 放在解压后的程序目录中运行。'
}

try {
    Get-Process cooopy -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500

    $sameDir = [System.IO.Path]::GetFullPath($source).TrimEnd('\') -eq
               [System.IO.Path]::GetFullPath($target).TrimEnd('\')
    if (-not $sameDir) {
        if (Test-Path $target) { Remove-Item $target -Recurse -Force }
        New-Item -ItemType Directory -Force $target | Out-Null
        Copy-Item (Join-Path $source '*') $target -Recurse -Force
    }

    $exe = Join-Path $target 'cooopy.exe'
    $shell = New-Object -ComObject WScript.Shell

    $desktop = [Environment]::GetFolderPath('Desktop')
    if (-not [string]::IsNullOrEmpty($desktop)) {
        $link = $shell.CreateShortcut((Join-Path $desktop 'cooopy.lnk'))
        $link.TargetPath = $exe
        $link.WorkingDirectory = $target
        $link.Description = 'cooopy'
        $link.Save()
    }

    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\cooopy.lnk'
    $link = $shell.CreateShortcut($startMenu)
    $link.TargetPath = $exe
    $link.WorkingDirectory = $target
    $link.Description = 'cooopy'
    $link.Save()

    Start-Process -FilePath $exe
    Write-Host "已安装到：$target" -ForegroundColor Green
    Write-Host '已创建桌面和开始菜单快捷方式。' -ForegroundColor Green
}
catch {
    Write-Host ('安装失败：' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
