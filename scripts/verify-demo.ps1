$ErrorActionPreference = "Stop"

function Assert-Endpoint {
    param([string]$Name, [string]$Uri)

    try {
        Invoke-WebRequest -UseBasicParsing -Uri $Uri -TimeoutSec 10 | Out-Null
        Write-Host "[OK] $Name"
    }
    catch {
        throw "$Name is not reachable at $Uri"
    }
}

Assert-Endpoint "OrderService" "http://localhost:5101/health"
Assert-Endpoint "InventoryService" "http://localhost:5102/health"
Assert-Endpoint "ObservabilityMcp" "http://localhost:5103/health"
Assert-Endpoint "DiagnosticAgent" "http://localhost:5104/health"
Assert-Endpoint "Grafana" "http://localhost:3000/api/health"
Assert-Endpoint "Prometheus" "http://localhost:9090/-/ready"
Assert-Endpoint "Loki" "http://localhost:3100/ready"
Assert-Endpoint "Tempo" "http://localhost:3200/ready"

& "$PSScriptRoot\run-healthy.ps1" | Out-Null
& "$PSScriptRoot\run-failure.ps1" | Out-Null
$replay = & "$PSScriptRoot\run-agent.ps1" -Mode Replay | ConvertFrom-Json

if ($replay.mode -ne "replay" -or $replay.diagnosis -notmatch "700 ms") {
    throw "Replay diagnosis did not contain the expected evidence."
}

Write-Host "[OK] Healthy flow, deterministic failure and replay diagnosis verified."
