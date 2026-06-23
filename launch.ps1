# Clawbrower launcher — check/start OpenClaw Gateway, then launch overlay
$ErrorActionPreference = "Stop"

$gatewayPort = 18789
$projectDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$exePath      = Join-Path $projectDir "bin\Debug\net8.0-windows\Clawbrower.exe"

# ── Helpers ──
function Test-Port($port, $ms) {
    try {
        $c = [System.Net.Sockets.TcpClient]::new()
        $task = $c.ConnectAsync("127.0.0.1", $port)
        if ($task.Wait($ms)) { $ok = $c.Connected; $c.Close(); return $ok }
        $c.Close(); return $false
    } catch { return $false }
}

function Start-Gateway {
    $nodeExe  = (Get-Command node).Source
    $npmRoot  = npm root -g 2>$null
    if (-not $npmRoot) {
        Write-Host "[FAIL] Cannot find npm global root" -ForegroundColor Red
        exit 1
    }
    $openclawJs = Join-Path $npmRoot "openclaw\dist\index.js"
    if (-not (Test-Path $openclawJs)) {
        Write-Host "[FAIL] openclaw not found at: $openclawJs" -ForegroundColor Red
        exit 1
    }

    Write-Host "[..] Starting OpenClaw Gateway via node..." -ForegroundColor Yellow
    Start-Process -FilePath $nodeExe `
        -ArgumentList $openclawJs,"gateway","--port","$gatewayPort" `
        -WindowStyle Hidden

    $waited = 0
    while ($waited -lt 30) {
        if (Test-Port $gatewayPort 800) {
            Write-Host "[OK] Gateway ready (${waited}s)" -ForegroundColor Green
            return
        }
        Start-Sleep -Seconds 1
        $waited++
    }
    Write-Host "[FAIL] Gateway did not start within 30s" -ForegroundColor Red
    exit 1
}

# ── 1. Gateway ──
if (Test-Port $gatewayPort 800) {
    Write-Host "[OK] OpenClaw Gateway already running (port $gatewayPort)" -ForegroundColor Green
} else {
    Start-Gateway
}

# ── 2. Overlay ──
if (-not (Test-Path $exePath)) {
    Write-Host "[FAIL] Clawbrower.exe not found. Run: dotnet build" -ForegroundColor Red
    exit 1
}

# Kill old instance if running (prevents file lock on rebuild)
Get-Process -Name Clawbrower -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "[>>] Launching overlay..." -ForegroundColor Cyan
Start-Process -FilePath $exePath -WindowStyle Normal
Write-Host "[OK] Done" -ForegroundColor Green
