# Local go-live gate for Soford (does not touch production).
# Exit 0 = code-side ready; exit 1 = fix before deploy; exit 2 = human blockers remain (expected).
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$fail = 0

function Step([string]$name, [scriptblock]$block) {
  Write-Host "==> $name"
  & $block
  if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) {
    Write-Host "FAIL $name (exit $LASTEXITCODE)"
    $script:fail = 1
  } else {
    Write-Host "OK   $name"
  }
}

Step "API Release build+test" {
  dotnet build SofordRepApi -c Release -warnaserror
  if ($LASTEXITCODE -ne 0) { return }
  dotnet test SofordRepApi.Tests -c Release --logger "console;verbosity=minimal"
}

Step "ERP frontend lint+build" {
  Push-Location WebSofordRep
  try {
    npm run lint
    if ($LASTEXITCODE -ne 0) { return }
    npm run build
  } finally { Pop-Location }
}

Step "Website scripts" {
  node --check SofordWebsite/assets/site.js
  if ($LASTEXITCODE -ne 0) { return }
  node --check SofordWebsite/scripts/export-products.mjs
  if ($LASTEXITCODE -ne 0) { return }
  node --test SofordWebsite/tests/*.test.mjs
}

Step "SITE placeholders" {
  $js = Get-Content SofordWebsite/assets/site.js -Raw
  if ($js -match 'phone:\s*"[^"]*000\s*0000') {
    Write-Host "SITE.phone still contains placeholder zeros"
    $script:fail = 1
  }
  if ($js -notmatch 'claimsVerified:\s*false' -and $js -notmatch 'claimsVerified:\s*true') {
    Write-Host "SITE.claimsVerified missing"
    $script:fail = 1
  }
}

if ($fail -ne 0) {
  Write-Host "`nGo-live gate FAILED (code)."
  exit 1
}

Write-Host @"

Go-live gate PASSED (code side).
Human blockers still required before production traffic:
  - Fill docs/06-官网上线资料清单.md and set SITE (+ claimsVerified: true when FAQ is verified)
  - Deploy website nginx + Website__AllowedOrigins on the server
  - Offsite backup restore drill
  - ICP / domain cutover when ready
See docs/07-自动执行交接.md
"@
exit 0
