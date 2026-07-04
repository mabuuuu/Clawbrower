# Clawbrower launcher
$ErrorActionPreference = "Stop"

$projectDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$exePath      = Join-Path $projectDir "bin\Debug\net8.0-windows\Clawbrower.exe"

# ── Launch overlay ──
if (-not (Test-Path $exePath)) {
    Write-Host "[FAIL] Clawbrower.exe not found. Run: dotnet build" -ForegroundColor Red
    exit 1
}

# Kill old instance if running (prevents file lock on rebuild)
Get-Process -Name Clawbrower -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "[>>] Launching overlay..." -ForegroundColor Cyan
Start-Process -FilePath $exePath -WindowStyle Normal
Write-Host "[OK] Done" -ForegroundColor Green
