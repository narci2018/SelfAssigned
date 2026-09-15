<# 
.SYNOPSIS
下载AltServer Windows所需的第三方工具
.DESCRIPTION
下载 libimobiledevice-win32 和 zsign 到 tools 目录
使用方法：.\download-windows-tools.ps1
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

$DownloadDir = Join-Path $OutputDir "downloads"

Write-Host "工具将下载到: $((Resolve-Path $OutputDir).Path)" -ForegroundColor Gray
Write-Host ""

# =============================================
# libimobiledevice-win32
# =============================================
Write-Host "[1/3] 下载 libimobiledevice-win32..." -ForegroundColor Yellow

$LibiMobiUrl = "https://github.com/libimobiledevice-win32/imobiledevice-net/releases/latest/download/imobiledevice-net-win64.zip"
$LibiMobiZip = Join-Path $DownloadDir "imobiledevice-net.zip"

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    
    Write-Host "  URL: $LibiMobiUrl" -ForegroundColor Gray
    Invoke-WebRequest -Uri $LibiMobiUrl -OutFile $LibiMobiZip -UseBasicParsing
    
    # 解压
    $extractDir = Join-Path $DownloadDir "imobiledevice-net"
    Expand-Archive -Path $LibiMobiZip -DestinationPath $extractDir -Force
    
    # 复制exe文件到tools目录
    $exeFiles = @("idevice_id.exe", "ideviceinfo.exe", "ideviceinstaller.exe", "idevicepair.exe", 
                   "ideviceimagemounter.exe", "idevicescreenshot.exe", "idevicediagnostics.exe")
    
    $dllFiles = Get-ChildItem -Path $extractDir -Recurse -Include "*.dll" -ErrorAction SilentlyContinue
    
    foreach ($exe in $exeFiles) {
        $source = Get-ChildItem -Path $extractDir -Recurse -Filter $exe -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($source) {
            Copy-Item -Path $source.FullName -Destination $OutputDir -Force
            Write-Host "  ✓ $exe" -ForegroundColor Green
        } else {
            Write-Host "  ⚠ $exe 未找到" -ForegroundColor DarkYellow
        }
    }
    
    # 复制DLL依赖
    if ($dllFiles) {
        foreach ($dll in $dllFiles) {
            Copy-Item -Path $dll.FullName -Destination $OutputDir -Force -ErrorAction SilentlyContinue
        }
        Write-Host "  ✓ 依赖DLL已复制" -ForegroundColor Green
    }
    
    Write-Host "  libimobiledevice-win32 下载完成" -ForegroundColor Green
} catch {
    Write-Host "  ✗ libimobiledevice-win32 下载失败: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "  手动下载地址: https://github.com/libimobiledevice-win32/imobiledevice-net/releases" -ForegroundColor Gray
}

Write-Host ""

# =============================================
# zsign (iOS代码签名工具)
# =============================================
Write-Host "[2/3] 下载 zsign..." -ForegroundColor Yellow

$ZsignUrl = "https://github.com/zhlynn/zsign/releases/latest/download/zsign-windows.zip"
$ZsignZip = Join-Path $DownloadDir "zsign.zip"

try {
    Write-Host "  URL: $ZsignUrl" -ForegroundColor Gray
    Invoke-WebRequest -Uri $ZsignUrl -OutFile $ZsignZip -UseBasicParsing
    
    $extractDir = Join-Path $DownloadDir "zsign"
    Expand-Archive -Path $ZsignZip -DestinationPath $extractDir -Force
    
    # 复制zsign.exe
    $zsignExe = Get-ChildItem -Path $extractDir -Recurse -Filter "zsign.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($zsignExe) {
        Copy-Item -Path $zsignExe.FullName -Destination $OutputDir -Force
        Write-Host "  ✓ zsign.exe" -ForegroundColor Green
    } else {
        Write-Host "  ⚠ zsign.exe 未找到（可能在子目录中）" -ForegroundColor DarkYellow
        # 尝试查找任何exe
        $anyExe = Get-ChildItem -Path $extractDir -Recurse -Filter "*.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($anyExe) {
            Copy-Item -Path $anyExe.FullName -Destination $OutputDir -Force
            Write-Host "  ✓ 已使用 $($anyExe.Name)" -ForegroundColor Green
        }
    }
    
    # 复制依赖DLL
    $dllFiles = Get-ChildItem -Path $extractDir -Recurse -Filter "*.dll" -ErrorAction SilentlyContinue
    if ($dllFiles) {
        foreach ($dll in $dllFiles) {
            Copy-Item -Path $dll.FullName -Destination $OutputDir -Force -ErrorAction SilentlyContinue
        }
    }
    
    Write-Host "  zsign 下载完成" -ForegroundColor Green
} catch {
    Write-Host "  ✗ zsign 下载失败: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "  手动下载地址: https://github.com/zhlynn/zsign/releases" -ForegroundColor Gray
}

Write-Host ""

# =============================================
# 清理和结果
# =============================================
Write-Host "[3/3] 清理和验证..." -ForegroundColor Yellow

# 清理下载目录
if (Test-Path $DownloadDir) {
    Remove-Item -Path $DownloadDir -Recurse -Force
}

# 验证工具
$requiredTools = @("idevice_id.exe", "ideviceinfo.exe", "ideviceinstaller.exe", "idevicepair.exe", "zsign.exe")
$missing = @()

Write-Host ""
Write-Host "工具验证:" -ForegroundColor Cyan

foreach ($tool in $requiredTools) {
    $toolPath = Join-Path $OutputDir $tool
    if (Test-Path $toolPath) {
        $size = (Get-Item $toolPath).Length
        $sizeKB = [math]::Round($size / 1024, 1)
        Write-Host "  ✓ $tool ($sizeKB KB)" -ForegroundColor Green
    } else {
        Write-Host "  ✗ $tool - 缺失" -ForegroundColor Red
        $missing += $tool
    }
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan

if ($missing.Count -eq 0) {
    Write-Host "所有工具已下载完成！" -ForegroundColor Green
} else {
    Write-Host "部分工具下载失败，请手动下载:" -ForegroundColor Yellow
    foreach ($tool in $missing) {
        Write-Host "  - $tool" -ForegroundColor Yellow
    }
}

Write-Host "========================================" -ForegroundColor Cyan