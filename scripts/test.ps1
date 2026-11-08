<#
.SYNOPSIS
    Builds WalletPK and runs every test project, writing an HTML and a TRX report per project to
    TestResults\<date_time>\ (one folder per run, so earlier runs are kept for comparison).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\test.ps1

.EXAMPLE
    pwsh ./scripts/test.ps1 -Configuration Release -NoBuild     # used by CI after its own build step
#>
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    # Skip the build when the solution was just built.
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$stamp = Get-Date -Format "yyyy-MM-dd_HH-mm-ss"
$resultsDir = [System.IO.Path]::GetFullPath((Join-Path $root "TestResults/$stamp"))
New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null

Push-Location $root
try {
    if (-not $NoBuild) {
        dotnet build WalletPK.sln --configuration $Configuration
        if ($LASTEXITCODE -ne 0) { throw "Build failed." }
    }

    $projects = Get-ChildItem -Path (Join-Path $root "tests") -Recurse -Filter "*.csproj" | Sort-Object Name
    $failed = @()

    foreach ($project in $projects) {
        $name = [System.IO.Path]::GetFileNameWithoutExtension($project.Name)
        Write-Host ""
        Write-Host "=== $name ===" -ForegroundColor Cyan

        dotnet test --project $project.FullName --configuration $Configuration --no-build `
            --results-directory $resultsDir `
            --report-xunit-html --report-xunit-html-filename "$name.html" `
            --report-xunit-trx --report-xunit-trx-filename "$name.trx"

        if ($LASTEXITCODE -ne 0) { $failed += $name }
    }

    Write-Host ""
    Write-Host "Reports written to: $resultsDir" -ForegroundColor Green
    Write-Host "Open the .html files in a browser; .trx files open in Visual Studio or CI tools."

    if ($failed.Count -gt 0) {
        Write-Host ("Failed test projects: " + ($failed -join ", ")) -ForegroundColor Red
        exit 1
    }

    Write-Host "All $($projects.Count) test projects passed." -ForegroundColor Green
}
finally {
    Pop-Location
}
