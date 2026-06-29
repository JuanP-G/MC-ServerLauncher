# Builds and serves the documentation site locally with DocFX.
# Usage:  .\docs\build-docs.ps1
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot

# Install DocFX as a global .NET tool if it isn't already available.
if (-not (Get-Command docfx -ErrorAction SilentlyContinue)) {
    Write-Host "==> Installing DocFX (global .NET tool)..." -ForegroundColor Cyan
    dotnet tool install -g docfx
    Write-Host "If 'docfx' is still not found, open a new terminal so the PATH refreshes." -ForegroundColor Yellow
}

Write-Host "==> Building docs and serving at http://localhost:8080 ..." -ForegroundColor Cyan
docfx "$here\docfx.json" --serve
