<# 
.SYNOPSIS
下载AltServer Windows所需的第三方工具
.DESCRIPTION
下载 libimobiledevice-win32 和 zsign 到 tools 目录
使用方法：.\download-windows-tools.ps1 [-OutputDir tools]
#>

param(
    [string]$OutputDir = "tools"
)

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " AltServer Windows - 工具下载器" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# 创建输出目录
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

# 使用绝对路径（脚本所在目录的上级作为默认）
if (-not [System.IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir = Join-Path $PSScriptRoot $OutputDir
}

$DownloadDir = Join-Path $OutputDir "_dl_temp"

Write-Host "工具目录: $($OutputDir)" -ForegroundColor Gray
Write-Host ""

# =============================================
# 辅助函数：使用 curl 下载（更可靠的重定向处理）
# =============================================
function Download-File {
    param([string]$Url, [string]$OutFile, [int]$Retries = 2)
    
    # 优先使用 curl（更可靠的重定向处理）
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) {
        for ($i = 0; $i -le $Retries; $i++) {
            Write-Host "  下载中... ($Url)" -ForegroundColor Gray
            $proc = Start-Process -FilePath "curl.exe" -ArgumentList @(
                "-L", "--silent", "--show-error", "--fail",
                "-o", $OutFile,
                $Url
            ) -NoNewWindow -Wait -PassThru
            if ($proc.ExitCode -eq 0 -and (Test-Path $OutFile)) {
                return $true
            }
            if ($i -lt $Retries) { Start-Sleep -Seconds 2 }
        }
    }
    
    # 回退到 Invoke-WebRequest
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    for ($i = 0; $i -le $Retries; $i++) {
        try {
            Write-Host "  下载中... ($Url)" -ForegroundColor Gray
            Invoke-WebRequest -Uri $Url -OutFile $OutFile -UseBasicParsing -TimeoutSec 60
            if (Test-Path $OutFile) { return $true }
        } catch {
            if ($i -lt $Retries) { Start-Sleep -Seconds 2 }
        }
    }
    return $false
}

# =============================================
# libimobiledevice-win32
# =============================================
Write-Host "[1/3] 下载 libimobiledevice-win32..." -ForegroundColor Yellow

if (-not (Test-Path $DownloadDir)) {
    New-Item -ItemType Directory -Path $DownloadDir -Force | Out-Null
}

# 从 GitHub API 获取最新 release 实际下载地址（避免跟随错误重定向）
$LibiMobiReleaseApi = "https://api.github.com/repos/libimobiledevice-win32/imobiledevice-net/releases/latest"
$ZsignReleaseApi = "https://api.github.com/repos/zhlynn/zsign/releases/latest"

function Get-GitHubAssetUrl {
    param([string]$ApiUrl, [string]$AssetPattern)
    try {
        $release = Invoke-RestMethod -Uri $ApiUrl -UseBasicParsing -TimeoutSec 15
        $asset = $release.assets | Where-Object { $_.name -like $AssetPattern } | Select-Object -First 1
        if ($asset) { return $asset.browser_download_url }
    } catch { }
    return $null
}

# --- libimobiledevice ---
$LibiMobiUrl = Get-GitHubAssetUrl -ApiUrl $LibiMobiReleaseApi -AssetPattern "*win64.zip"
if (-not $LibiMobiUrl) {
    # 回退到固定 URL
    $LibiMobiUrl = "https://github.com/libimobiledevice-win32/imobiledevice-net/releases/download/v2024.10.22/libimobiledevice-net-2024.10.22-win64.zip"
}
$LibiMobiZip = Join-Path $DownloadDir "imobiledevice-net.zip"

Write-Host "  URL: $LibiMobiUrl" -ForegroundColor Gray

$ok = Download-File -Url $LibiMobiUrl -OutFile $LibiMobiZip
if ($ok) {
    $extractDir = Join-Path $DownloadDir "imobiledevice-net"
    Expand-Archive -Path $LibiMobiZip -DestinationPath $extractDir -Force
    
    $exeFiles = @("idevice_id.exe", "ideviceinfo.exe", "ideviceinstaller.exe", "idevicepair.exe")
    
    foreach ($exe in $exeFiles) {
        $source = Get-ChildItem -Path $extractDir -Recurse -Filter $exe -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($source) {
            Copy-Item -Path $source.FullName -Destination $OutputDir -Force
            Write-Host "  ✓ $exe" -ForegroundColor Green
        } else {
            Write-Host "  ⚠ $exe 未找到" -ForegroundColor DarkYellow
        }
    }
    
    # 复制 DLL 依赖
    $dllFiles = Get-ChildItem -Path $extractDir -Recurse -Include "*.dll" -ErrorAction SilentlyContinue
    if ($dllFiles) {
        foreach ($dll in $dllFiles) {
            Copy-Item -Path $dll.FullName -Destination $OutputDir -Force -ErrorAction SilentlyContinue
        }
        Write-Host "  ✓ 依赖DLL已复制" -ForegroundColor Green
    }
    
    Write-Host "  libimobiledevice-win32 下载完成" -ForegroundColor Green
} else {
    Write-Host "  ✗ 下载失败，请手动下载:" -ForegroundColor Red
    Write-Host "    https://github.com/libimobiledevice-win32/imobiledevice-net/releases" -ForegroundColor Gray
}

Write-Host ""

# =============================================
# zsign (iOS代码签名工具)
# =============================================
Write-Host "[2/3] 下载 zsign..." -ForegroundColor Yellow

$ZsignUrl = Get-GitHubAssetUrl -ApiUrl $ZsignReleaseApi -AssetPattern "*windows*"
if (-not $ZsignUrl) {
    $ZsignUrl = "https://github.com/zhlynn/zsign/releases/latest/download/zsign-windows.zip"
}
$ZsignZip = Join-Path $DownloadDir "zsign.zip"

$ok = Download-File -Url $ZsignUrl -OutFile $ZsignZip
if ($ok) {
    $extractDir = Join-Path $DownloadDir "zsign"
    Expand-Archive -Path $ZsignZip -DestinationPath $extractDir -Force
    
    $zsignExe = Get-ChildItem -Path $extractDir -Recurse -Filter "zsign.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($zsignExe) {
        Copy-Item -Path $zsignExe.FullName -Destination $OutputDir -Force
        Write-Host "  ✓ zsign.exe" -ForegroundColor Green
    } else {
        Write-Host "  ⚠ zsign.exe 未找到（尝试查找其他exe）" -ForegroundColor DarkYellow
        $anyExe = Get-ChildItem -Path $extractDir -Recurse -Filter "*.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($anyExe) {
            Copy-Item -Path $anyExe.FullName -Destination $OutputDir -Force
            Write-Host "  ✓ 已使用 $($anyExe.Name)" -ForegroundColor Green
        }
    }
    
    # 复制依赖 DLL
    $dllFiles = Get-ChildItem -Path $extractDir -Recurse -Filter "*.dll" -ErrorAction SilentlyContinue
    if ($dllFiles) {
        foreach ($dll in $dllFiles) {
            Copy-Item -Path $dll.FullName -Destination $OutputDir -Force -ErrorAction SilentlyContinue
        }
    }
    
    Write-Host "  zsign 下载完成" -ForegroundColor Green
} else {
    Write-Host "  ✗ 下载失败，请手动下载:" -ForegroundColor Red
    Write-Host "    https://github.com/zhlynn/zsign/releases" -ForegroundColor Gray
}

Write-Host ""

# =============================================
# 清理和结果
# =============================================
Write-Host "[3/3] 验证工具..." -ForegroundColor Yellow

# 清理临时下载目录
if (Test-Path $DownloadDir) {
    Remove-Item -Path $DownloadDir -Recurse -Force -ErrorAction SilentlyContinue
}

$requiredTools = @("idevice_id.exe", "ideviceinfo.exe", "ideviceinstaller.exe", "idevicepair.exe", "zsign.exe")
$found = @()
$missing = @()

Write-Host ""
Write-Host "工具验证:" -ForegroundColor Cyan

foreach ($tool in $requiredTools) {
    $toolPath = Join-Path $OutputDir $tool
    if (Test-Path $toolPath) {
        $size = (Get-Item $toolPath).Length
        $sizeKB = [math]::Round($size / 1024, 1)
        Write-Host "  ✓ $tool ($sizeKB KB)" -ForegroundColor Green
        $found += $tool
    } else {
        Write-Host "  ✗ $tool - 缺失" -ForegroundColor Red
        $missing += $tool
    }
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan

if ($missing.Count -eq 0) {
    Write-Host "全部工具已就绪！" -ForegroundColor Green
} else {
    Write-Host "已下载 $($found.Count)/$($requiredTools.Count) 个工具" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "手动下载地址:" -ForegroundColor Yellow
    if ($missing -contains "idevice_id.exe") {
        Write-Host "  libimobiledevice: https://github.com/libimobiledevice-win32/imobiledevice-net/releases" -ForegroundColor Gray
    }
    if ($missing -contains "zsign.exe") {
        Write-Host "  zsign: https://github.com/zhlynn/zsign/releases" -ForegroundColor Gray
    }
}

Write-Host "========================================" -ForegroundColor Cyan