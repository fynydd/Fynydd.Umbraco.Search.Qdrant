. ./clean.ps1

if (Test-Path ".\src\nupkg") { Remove-Item ".\src\nupkg" -Recurse -Force }
Set-Location src
dotnet pack --configuration Release
Set-Location ..

. ./clean.ps1
