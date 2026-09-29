param(
    [string]$Output = ".publish"
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$PublishRoot = Join-Path $Root $Output
$ApiOut = Join-Path $PublishRoot "api"
$WebOut = Join-Path $PublishRoot "web"
$WebDir = Join-Path $Root "WebSofordRep"

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

function Invoke-Native {
    param(
        [string]$FilePath,
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath exited with code $LASTEXITCODE"
    }
}

if (Test-Path $PublishRoot) {
    Remove-Item -LiteralPath $PublishRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $ApiOut, $WebOut | Out-Null

Write-Host "Publishing API..."
Invoke-Native "dotnet" @("publish", (Join-Path $Root "SofordRepApi\SofordRepApi.csproj"), "-c", "Release", "-o", $ApiOut, "--self-contained", "false")

Write-Host "Building web app..."
Push-Location $WebDir
try {
    $npm = Find-Npm
    if (-not (Test-Path "node_modules")) {
        if (Test-Path "package-lock.json") {
            Invoke-Native $npm @("ci")
        }
        else {
            Invoke-Native $npm @("install")
        }
    }

    Invoke-Native $npm @("run", "build")
}
finally {
    Pop-Location
}

Copy-Item -Path (Join-Path $WebDir "dist\*") -Destination $WebOut -Recurse -Force

Copy-Item -Path (Join-Path $Root "deploy") -Destination (Join-Path $PublishRoot "deploy") -Recurse -Force

$zip = Join-Path $Root "soford-erp-server.zip"
if (Test-Path $zip) {
    Remove-Item -LiteralPath $zip -Force
}

Compress-Archive -Path (Join-Path $PublishRoot "*") -DestinationPath $zip -Force

Write-Host ""
Write-Host "Server package ready:"
Write-Host "  $zip"
Write-Host "Upload it to the server and follow deploy/DEPLOY_UBUNTU.md."
