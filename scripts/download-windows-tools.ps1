<# 
.SYNOPSIS
下载AltServer Windows所需的第三方工具
.DESCRIPTION
下载 libimobiledevice-win32 和 zsign 到 tools 目录
使用方法：.\download-windows-tools.ps1 [-OutputDir "D:\ios\AltServer-Windows\tools"]
不带参数时，自动将工具下载到脚本所在位置的上层目录下的 tools\ 文件夹
（即 AltServer.exe 所在目录旁的 tools\ 子目录）
#>

param(
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " AltServer Windows - 工具下载器" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# 解析输出目录：默认为脚本上级目录下的 tools\（即 AltServer.exe 旁的 tools\）
# 这样无论 AltServer 装在 C:\、D:\ 还是 E:\，都能正确找到工具目录
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $scriptParent = Split-Path -Parent $PSScriptRoot   # scripts\ 的上层 = 项目根
    $OutputDir = Join-Path $scriptParent "AltServer.Windows\bin\Release\net8.0-windows\win-x64\tools"
    # 如果找到已发布的 exe 目录则优先使用，否则回退到项目目录
    $publishedDir = Join-Path $scriptParent "publish\tools"
    $exeDir = Split-Path -Parent (Get-Command "AltServer.exe" -ErrorAction SilentlyContinue).Source 2>$null
    if ($exeDir -and (Test-Path (Join-Path $exeDir "AltServer.exe"))) {
        $OutputDir = Join-Path $exeDir "tools"
        Write-Host "检测到运行中的 AltServer.exe，工具将安装至其旁边的 tools\ 目录" -ForegroundColor Green
    }
}

# 如果仍是相对路径，以脚本所在目录的上级为基准
if (-not [System.IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir = Join-Path (Split-Path -Parent $PSScriptRoot) $OutputDir
}

# 创建输出目录（支持任意盘符）
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
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
# CoreADI.dll (本地 anisette, 已提交在仓库内)
# =============================================
Write-Host "[3/4] 复制 CoreADI.dll (本地 anisette)..." -ForegroundColor Yellow

$coreAdiRepoSrc = Join-Path $PSScriptRoot "..\AltServer.Windows\tools\an\CoreADI.dll"
if (Test-Path $coreAdiRepoSrc) {
    $coreAdiDstDir = Join-Path $OutputDir "an"
    if (-not (Test-Path $coreAdiDstDir)) {
        New-Item -ItemType Directory -Path $coreAdiDstDir -Force | Out-Null
    }
    Copy-Item -Path $coreAdiRepoSrc -Destination $coreAdiDstDir -Force
    $sz = [math]::Round((Get-Item (Join-Path $coreAdiDstDir "CoreADI.dll")).Length / 1MB, 2)
    Write-Host "  ✓ CoreADI.dll ($sz MB) -> $coreAdiDstDir" -ForegroundColor Green
} else {
    Write-Host "  ⚠ 仓库未找到 CoreADI.dll: $coreAdiRepoSrc" -ForegroundColor DarkYellow
    Write-Host "    可从 iTunes/Apple Application Support 复制 x64 CoreADI.dll 到 tools\an\" -ForegroundColor Gray
}

Write-Host ""

# =============================================
# 清理和结果
# =============================================
Write-Host "[4/4] 验证工具..." -ForegroundColor Yellow

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

$adiPath = Join-Path $OutputDir "an\CoreADI.dll"
if (Test-Path $adiPath) {
    $sizeMB = [math]::Round((Get-Item $adiPath).Length / 1MB, 2)
    Write-Host "  ✓ an\CoreADI.dll ($sizeMB MB) [本地 anisette]" -ForegroundColor Green
} else {
    Write-Host "  ⚠ an\CoreADI.dll - 缺失 (将回退到远程 anisette)" -ForegroundColor DarkYellow
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