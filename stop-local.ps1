$ErrorActionPreference = "Stop"

$ports = @(5153, 5173)
$processIds = @()

foreach ($port in $ports) {
    $listeners = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
    foreach ($listener in $listeners) {
        if ($listener.OwningProcess -and ($processIds -notcontains $listener.OwningProcess)) {
            $processIds += $listener.OwningProcess
        }
    }
}

if ($processIds.Count -eq 0) {
    Write-Host "No local Soford ERP services are listening on ports 5153 or 5173."
    exit 0
}

foreach ($processId in $processIds) {
    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($process) {
        Write-Host "Stopping $($process.ProcessName) pid=$processId"
        Stop-Process -Id $processId -Force
    }
}

Write-Host "Stopped local Soford ERP services."
