# 一键发布新版本：更新版本号 -> 提交 -> 打 tag -> 推送
# GitHub Actions 会自动构建便携包并创建 Release。
#
# 用法：
#   .\publish.ps1 -Version 0.1.1
#   .\publish.ps1 -Patch              # 自动把最后一位 +1，例如 0.1.0 -> 0.1.1
#   .\publish.ps1 -Patch -DryRun      # 只检查并打印，不修改/提交/推送
#   .\publish.ps1 -Version 0.2.0 -NoPush
param(
    [string]$Version,
    [switch]$Patch,
    [switch]$DryRun,
    [switch]$NoPush
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$projectFile = Join-Path $repoRoot 'cooopy\cooopy.csproj'

if (-not (Test-Path $projectFile)) {
    throw "找不到项目文件：$projectFile"
}

# Git 可能装在非默认位置，优先从 PATH 找，再尝试常见路径。
$git = (Get-Command git -ErrorAction SilentlyContinue)?.Source
if (-not $git) {
    $git = @(
        'D:\Program Files\Git\cmd\git.exe',
        (Join-Path $env:ProgramFiles 'Git\cmd\git.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Git\cmd\git.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $git) { throw '未找到 git.exe' }

function Invoke-Git {
    param([string[]]$GitArgs)
    & $git -C $repoRoot @GitArgs
    if ($LASTEXITCODE -ne 0) {
        throw "git $($GitArgs -join ' ') 失败"
    }
}

function Get-CurrentVersion {
    $text = Get-Content $projectFile -Raw
    if ($text -match '<Version>([^<]+)</Version>') { return $Matches[1].Trim() }
    throw '无法从 csproj 读取 Version'
}

$currentVersion = Get-CurrentVersion

if (-not $Version) {
    if (-not $Patch) { throw '请指定 -Version x.y.z 或 -Patch' }
    $parts = $currentVersion.Split('.')
    while ($parts.Count -lt 3) { $parts += '0' }
    $parts[2] = ([int]$parts[2] + 1).ToString()
    $Version = ($parts[0..2] -join '.')
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "版本号格式应为 x.y.z，当前输入：$Version"
}

$status = & $git -C $repoRoot status --porcelain
if ($LASTEXITCODE -ne 0) { throw 'git status 失败' }
if ($status) {
    throw "工作区还有未提交修改，请先提交或清理：`n$status"
}

$branch = (& $git -C $repoRoot rev-parse --abbrev-ref HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw '无法读取当前分支' }
if ($branch -ne 'main') { throw "当前分支是 $branch，请先切换到 main" }

$tag = "v$Version"
& $git -C $repoRoot rev-parse -q --verify "refs/tags/$tag" *> $null
if ($LASTEXITCODE -eq 0) { throw "本地已存在 tag $tag" }

$remoteTag = & $git -C $repoRoot ls-remote --tags origin $tag 2>$null
if ($remoteTag) { throw "远程已存在 tag $tag" }

Write-Host "当前版本：$currentVersion" -ForegroundColor Gray
Write-Host "目标版本：$Version" -ForegroundColor Cyan
Write-Host "tag：$tag" -ForegroundColor Cyan

if ($DryRun) {
    Write-Host 'DryRun：检查通过，未修改任何文件。' -ForegroundColor Yellow
    return
}

$text = Get-Content $projectFile -Raw
$newText = [regex]::Replace($text, '<Version>[^<]*</Version>', "<Version>$Version</Version>", 1)
if ($newText -eq $text) { throw 'csproj 中没有找到 Version 节点' }
[System.IO.File]::WriteAllText($projectFile, $newText, [System.Text.UTF8Encoding]::new($false))

Invoke-Git @('add', 'cooopy/cooopy.csproj')
Invoke-Git @('commit', '-m', "Release v$Version")
Invoke-Git @('tag', '-a', $tag, '-m', "cooopy v$Version")

if (-not $NoPush) {
    Invoke-Git @('push', 'origin', 'main')
    Invoke-Git @('push', 'origin', $tag)
    Write-Host ''
    Write-Host "已推送 $tag，GitHub Actions 会自动构建 Release：" -ForegroundColor Green
    Write-Host "https://github.com/Cilurus/cooopy/releases/tag/$tag" -ForegroundColor Green
}
else {
    Write-Host "已本地提交并打 tag $tag（未推送）。" -ForegroundColor Yellow
}
