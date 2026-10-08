$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "Starting the Aspire AppHost..."
Write-Host "The Aspire Dashboard URL and login token will be printed below."
$env:ASPIRE_DCP_USE_DEVELOPER_CERTIFICATE = "false"
$env:ASPIRE_ALLOW_UNSECURED_TRANSPORT = "true"
dotnet run --project .\src\Demo.AppHost\Demo.AppHost.csproj --launch-profile http
