$ErrorActionPreference = 'Stop'
dotnet restore
dotnet build -c Release
Write-Host "Build complete. Output: bin\Release\net8.0-windows\"
