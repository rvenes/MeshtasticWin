param(
    [string]$Port = "COM10",
    [int]$IntervalSeconds = 300,
    [string]$MessagePrefix = "test message from COM10 to mesh at",
    [string]$LogDir = "H:\Koding\logging",
    [int]$TimeoutSeconds = 45
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($IntervalSeconds -lt 5) {
    throw "IntervalSeconds must be >= 5"
}

if (-not (Get-Command meshtastic -ErrorAction SilentlyContinue)) {
    throw "meshtastic CLI not found in PATH. Open the shell where meshtastic works first."
}

if (-not (Test-Path -LiteralPath $LogDir)) {
    New-Item -ItemType Directory -Path $LogDir | Out-Null
}

$sessionStamp = Get-Date -Format "yyyyMMdd_HHmmss"
$logPath = Join-Path $LogDir "mesh_watchdog_sender_$sessionStamp.log"

function Write-Log {
    param([string]$Line)
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $full = "[$ts] $Line"
    $full | Tee-Object -FilePath $logPath -Append
}

Write-Log "Starting watchdog sender. port=$Port interval=${IntervalSeconds}s mode=public timeout=${TimeoutSeconds}s"
Write-Log "Press Ctrl+C to stop."

$counter = 1
while ($true) {
    $stamp = Get-Date -Format "HH:mm"
    $msg = "$MessagePrefix $stamp (#$counter)"
    $args = @("--port", $Port, "--sendtext", $msg, "--timeout", "$TimeoutSeconds")

    Write-Log "SEND: $msg"
    try {
        $output = & meshtastic @args 2>&1
        $exitCode = $LASTEXITCODE
        if ($output) {
            foreach ($line in $output) {
                Write-Log "CLI: $line"
            }
        }

        if ($exitCode -eq 0) {
            Write-Log "RESULT: OK"
        }
        else {
            Write-Log "RESULT: FAIL exit=$exitCode"
        }
    }
    catch {
        Write-Log "RESULT: EXCEPTION $($_.Exception.Message)"
    }

    $counter++
    Start-Sleep -Seconds $IntervalSeconds
}
