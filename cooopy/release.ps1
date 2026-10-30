# 构建 GitHub Release 用的便携包
#   .\release.ps1            构建完整版（自包含，约 60MB）和精简版（需要 .NET 8 Desktop Runtime）
#   .\release.ps1 -NoLite    只构建完整版
param(
    [switch]$NoLite
)

$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$repoRoot = Split-Path $projectDir -Parent
$dist = Join-Path $repoRoot 'dist'

$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

[xml]$project = Get-Content (Join-Path $projectDir 'cooopy.csproj')
$version = $project.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { $version = '0.0.0' }

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force $dist | Out-Null

function Publish-Cooopy {
    param(
        [string]$OutputDir,
        [bool]$SelfContained
    )

    Write-Host "==> 发布 $(if ($SelfContained) { '完整版（自包含）' } else { '精简版（依赖 .NET 8）' })" -ForegroundColor Cyan
    & $dotnet publish (Join-Path $projectDir 'cooopy.csproj') `
        -c Release -r win-x64 --self-contained $SelfContained `
        -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false `
        -o $OutputDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }

    # 发布包只保留运行必需文件
    Get-ChildItem $OutputDir -Recurse -File -Include *.pdb, *.xml | Remove-Item -Force -ErrorAction SilentlyContinue

    # 附带的轻量安装脚本（可选）
    Copy-Item (Join-Path $projectDir 'installer\install.ps1') $OutputDir -Force
    Copy-Item (Join-Path $projectDir 'installer\install.cmd') $OutputDir -Force
}

Publish-Cooopy -OutputDir (Join-Path $dist 'portable') -SelfContained $true
Compress-Archive -Path (Join-Path $dist 'portable\*') `
    -DestinationPath (Join-Path $dist 'cooopy-portable-win-x64.zip') -Force

if (-not $NoLite) {
    Publish-Cooopy -OutputDir (Join-Path $dist 'lite') -SelfContained $false
    Compress-Archive -Path (Join-Path $dist 'lite\*') `
        -DestinationPath (Join-Path $dist 'cooopy-lite-win-x64.zip') -Force
}

$zipFiles = Get-ChildItem $dist -File -Filter '*.zip' | Sort-Object Name
$lines = foreach ($file in $zipFiles) {
    $hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($file.Name)"
}
Set-Content (Join-Path $dist 'SHA256SUMS.txt') $lines -Encoding ascii

Write-Host ''
Write-Host "==> 构建完成：$dist" -ForegroundColor Green
Get-ChildItem $dist -File | ForEach-Object {
    "{0,-34} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)
}
Write-Host "版本：$version"
