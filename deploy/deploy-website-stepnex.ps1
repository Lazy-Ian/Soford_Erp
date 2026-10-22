# Deploy SofordWebsite to https://stepnex.cn/soford/ on the existing stepnex blog nginx host.
param(
    [string]$Server = "39.106.188.160",
    [string]$User = "root",
    [string]$IdentityFile = "$env:USERPROFILE\.ssh\stepnex_deployer_ed25519"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Site = Join-Path $Root "SofordWebsite"
$Snippet = Join-Path $Root "deploy\nginx\snippets\stepnex-soford-website.locations.conf"
$PatchPy = Join-Path $Root "deploy\patch-stepnex-website-nginx.py"
$RemoteSh = Join-Path $Root "deploy\deploy-website-stepnex.sh"
$Target = "$User@$Server"
$Ssh = @()
if ($IdentityFile -and (Test-Path -LiteralPath $IdentityFile)) {
    $Ssh = @("-i", $IdentityFile, "-o", "BatchMode=yes")
}

function Native([string]$FilePath, [string[]]$Arguments) {
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$FilePath exited $LASTEXITCODE" }
}

foreach ($p in @($Site, $Snippet, $PatchPy, $RemoteSh)) {
    if (-not (Test-Path $p)) { throw "Missing $p" }
}

Write-Host "Uploading website + scripts to $Target ..."
Native scp ($Ssh + @("-r", $Site, "${Target}:~/SofordWebsite.upload"))
Native scp ($Ssh + @($Snippet, "${Target}:~/stepnex-soford-website.locations.conf"))
Native scp ($Ssh + @($PatchPy, "${Target}:~/patch-stepnex-website-nginx.py"))
Native scp ($Ssh + @($RemoteSh, "${Target}:~/deploy-website-stepnex.sh"))
Native ssh ($Ssh + @($Target, "chmod +x ~/deploy-website-stepnex.sh ~/patch-stepnex-website-nginx.py && bash ~/deploy-website-stepnex.sh"))

Write-Host "Website deployed: https://stepnex.cn/soford/"
