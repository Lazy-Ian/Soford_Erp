param(
    [string]$Server = "39.106.188.160",
    [string]$User = "root",
    [string]$Domain = "erp.soford.cn",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Zip = Join-Path $Root "soford-erp-server.zip"
$Installer = Join-Path $Root "deploy\install-ubuntu.sh"
$Target = "$User@$Server"

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

if (-not $SkipBuild) {
    Invoke-Native "powershell" @("-ExecutionPolicy", "Bypass", "-File", (Join-Path $Root "publish-server.ps1"))
}

if (-not (Test-Path $Zip)) {
    throw "Missing $Zip. Run .\publish-server.ps1 first."
}

if (-not (Test-Path $Installer)) {
    throw "Missing $Installer."
}

Write-Host "Uploading package to $Target ..."
Invoke-Native "scp" @($Zip, "${Target}:~/soford-erp-server.zip")
Invoke-Native "scp" @($Installer, "${Target}:~/install-soford-erp.sh")

Write-Host ""
Write-Host "Running server installer. You may be asked for the server password and sudo password."
Write-Host "This adds a separate Nginx site for $Domain and does not overwrite existing www.stepnex.cn config."
Write-Host ""

Invoke-Native "ssh" @("-t", $Target, "bash ~/install-soford-erp.sh '$Domain'")

Write-Host ""
Write-Host "Upload/deploy step finished."
Write-Host "Now SSH into the server and edit secrets:"
Write-Host "  ssh $Target"
Write-Host "  sudo nano /opt/soford-erp/env/soford-api.env"
Write-Host "  sudo systemctl restart soford-erp-api"
Write-Host ""
Write-Host "After DNS is ready, enable HTTPS:"
Write-Host "  sudo certbot --nginx -d $Domain"
