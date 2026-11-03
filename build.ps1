# Builds lms-tokps as a self-contained single-file exe into publish\
# Requires the .NET 8 SDK. Run from the repo root (next to lms-tokps.csproj).

$ErrorActionPreference = "Stop"
Set-Location -LiteralPath $PSScriptRoot

# Kill any running instance so the exe is not locked.
Get-Process -Name "lms-tokps" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Remove-Item -Recurse -Force -ErrorAction SilentlyContinue ./bin, ./publish

dotnet publish -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host ""
Write-Host "Done: $(Resolve-Path ./publish/lms-tokps.exe)" -ForegroundColor Green
