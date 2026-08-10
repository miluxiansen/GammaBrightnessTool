# ============================================================
# Gamma Brightness Tool - 绿色版构建脚本 (3.1.0)
# 用法: powershell -ExecutionPolicy Bypass -File build-green.ps1
# 产物: GreenVersion\GammaBrightnessTool_<版本>_<yyyyMMdd_HHmm>.exe
#       (带时间戳文件名，新老版本共存，无需先删除旧文件)
# ============================================================

$ErrorActionPreference = "Stop"

$projDir  = $PSScriptRoot
$projFile = Join-Path $projDir "GammaBrightnessTool.csproj"
$outDir   = Join-Path $projDir "GreenVersion"

if (-not (Test-Path $projFile)) {
    Write-Host "[ERROR] project file not found: $projFile" -ForegroundColor Red
    exit 1
}

# Read version from csproj via regex (robust across PS editions)
$raw = [System.IO.File]::ReadAllText($projFile, [System.Text.Encoding]::UTF8)
$m   = [regex]::Match($raw, '<Version>([^<]+)</Version>')
$ver = if ($m.Success) { $m.Groups[1].Value } else { "0.0.0" }

$ts     = Get-Date -Format "yyyyMMdd_HHmm"
$base   = "GammaBrightnessTool_${ver}_${ts}"
$outExe = Join-Path $outDir "$base.exe"

New-Item -ItemType Directory -Path $outDir -Force | Out-Null

Write-Host "=== Building green version $ver ($ts) ===" -ForegroundColor Cyan
Write-Host "Publishing (self-contained single file, ~1-3 min)..."

dotnet publish $projFile -c Release -o $outDir `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --nologo -v q

if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] publish failed (exit=$LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
}

# Publisher outputs fixed name GammaBrightnessTool.exe -> rename with timestamp
$publishedExe = Join-Path $outDir "GammaBrightnessTool.exe"
if (Test-Path $publishedExe) {
    Move-Item -Force $publishedExe $outExe
}
$publishedPdb = Join-Path $outDir "GammaBrightnessTool.pdb"
if (Test-Path $publishedPdb) {
    Move-Item -Force $publishedPdb (Join-Path $outDir "$base.pdb")
}

$size = (Get-Item $outExe).Length
$hash = (Get-FileHash $outExe -Algorithm SHA256).Hash.Substring(0, 8)
Write-Host ""
Write-Host "[DONE] $base.exe" -ForegroundColor Green
Write-Host "  Size: $([math]::Round($size/1MB, 1)) MB  SHA256(first8): $hash" -ForegroundColor Green
Write-Host ""
