param(
    [string]$Output = ".publish"
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$PublishRoot = Join-Path $Root $Output
$ApiOut = Join-Path $PublishRoot "api"
$WebOut = Join-Path $PublishRoot "web"
$WebDir = Join-Path $Root "WebSofordRep"
$WebsiteOut = Join-Path $PublishRoot "website"
$RootFull = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
$PublishFull = [IO.Path]::GetFullPath($PublishRoot)
if (-not $PublishFull.StartsWith($RootFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Publish output must be a subdirectory of the workspace."
}
$Revision = (& git -C $Root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $Revision -notmatch '^[0-9a-f]{40}$') { throw "Cannot determine release commit." }
if (& git -C $Root status --porcelain) { throw "Commit workspace changes before packaging a release." }

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

New-Item -ItemType Directory -Force -Path $ApiOut, $WebOut, $WebsiteOut | Out-Null

Write-Host "Publishing API..."
Invoke-Native "dotnet" @("publish", (Join-Path $Root "SofordRepApi\SofordRepApi.csproj"), "-c", "Release", "-o", $ApiOut, "--self-contained", "false")
if ((Test-Path -LiteralPath (Join-Path $ApiOut 'App_Data')) -or (Get-ChildItem -LiteralPath $ApiOut -Force -Filter '.env*')) {
    throw 'Release output unexpectedly contains local business data or credentials.'
}

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
Copy-Item -Path (Join-Path $Root "SofordWebsite\*.html") -Destination $WebsiteOut -Force
Copy-Item -LiteralPath (Join-Path $Root "SofordWebsite\assets") -Destination (Join-Path $WebsiteOut "assets") -Recurse -Force
# Version static URLs by commit so the seven-day cache cannot keep an older script after deploy.
$Utf8 = New-Object System.Text.UTF8Encoding($false)
foreach ($HtmlFile in Get-ChildItem -LiteralPath $WebsiteOut -Filter '*.html') {
    $HtmlText = [IO.File]::ReadAllText($HtmlFile.FullName)
    $HtmlText = [regex]::Replace($HtmlText, '(assets/[^"''?]+\.(?:css|js))(?:\?v=[^"'']*)?', [System.Text.RegularExpressions.MatchEvaluator]{ param($match) $match.Groups[1].Value + '?v=' + $Revision })
    [IO.File]::WriteAllText($HtmlFile.FullName, $HtmlText, $Utf8)
}
$Manifest = @{ Release = @{ Revision = $Revision; BuiltAtUtc = [DateTime]::UtcNow.ToString('o') } } | ConvertTo-Json
foreach ($ReleaseFolder in @($ApiOut, $WebOut, $WebsiteOut)) {
    [IO.File]::WriteAllText((Join-Path $ReleaseFolder 'release.json'), $Manifest, $Utf8)
}

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
