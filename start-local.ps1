param(
    [string]$Username = "admin",
    [string]$Password = "admin123",
    [string]$DataPath = "",
    [switch]$NoBrowser
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$ApiDir = Join-Path $Root "SofordRepApi"
$WebDir = Join-Path $Root "WebSofordRep"
$LogDir = Join-Path $Root ".local-logs"
$ApiLog = Join-Path $LogDir "api.log"
$ApiErr = Join-Path $LogDir "api.err.log"
$WebLog = Join-Path $LogDir "web.log"
$WebErr = Join-Path $LogDir "web.err.log"

function Test-PortListening {
    param([int]$Port)
    return [bool](Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1)
}

function Find-Npm {
    $npm = Get-Command npm.cmd -ErrorAction SilentlyContinue
    if ($npm) {
        return $npm.Source
    }

    $npm = Get-Command npm -ErrorAction SilentlyContinue
    if ($npm) {
        return $npm.Source
    }

    throw "npm was not found. Install Node.js first."
}

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet was not found. Install .NET 8 SDK first."
}

if (-not (Test-Path (Join-Path $WebDir "node_modules"))) {
    Write-Host "Installing frontend dependencies..."
    Push-Location $WebDir
    try {
        & (Find-Npm) install
    }
    finally {
        Pop-Location
    }
}

if (-not (Test-PortListening 5153)) {
    Write-Host "Starting API on http://localhost:5153 ..."
    $apiEnv = @{
        Auth__AdminUsername = $Username
        Auth__AdminPassword = $Password
    }

    if ($DataPath.Trim().Length -gt 0) {
        $apiEnv["Soford__DataPath"] = $DataPath
    }

    $apiCommand = @"
`$env:Auth__AdminUsername='$($Username.Replace("'", "''"))'
`$env:Auth__AdminPassword='$($Password.Replace("'", "''"))'
$(if ($DataPath.Trim().Length -gt 0) { "`$env:Soford__DataPath='$($DataPath.Replace("'", "''"))'" } else { "" })
dotnet run --urls http://localhost:5153
"@

    Start-Process `
        -FilePath "powershell" `
        -ArgumentList @("-NoExit", "-ExecutionPolicy", "Bypass", "-Command", $apiCommand) `
        -WorkingDirectory $ApiDir `
        -RedirectStandardOutput $ApiLog `
        -RedirectStandardError $ApiErr `
        -WindowStyle Hidden
}
else {
    Write-Host "API is already listening on http://localhost:5153"
}

if (-not (Test-PortListening 5173)) {
    Write-Host "Starting web app on http://localhost:5173 ..."
    Start-Process `
        -FilePath (Find-Npm) `
        -ArgumentList @("run", "dev", "--", "--host", "127.0.0.1") `
        -WorkingDirectory $WebDir `
        -RedirectStandardOutput $WebLog `
        -RedirectStandardError $WebErr `
        -WindowStyle Hidden
}
else {
    Write-Host "Web app is already listening on http://localhost:5173"
}

Write-Host "Waiting for services..."
for ($i = 0; $i -lt 20; $i++) {
    if ((Test-PortListening 5153) -and (Test-PortListening 5173)) {
        break
    }

    Start-Sleep -Seconds 1
}

Write-Host ""
Write-Host "Soford ERP local run:"
Write-Host "  Web: http://localhost:5173"
Write-Host "  API: http://localhost:5153"
Write-Host "  Username: $Username"
Write-Host "  Password: $Password"
Write-Host "  Logs: $LogDir"
Write-Host ""

if (-not $NoBrowser) {
    Start-Process "http://localhost:5173"
}
