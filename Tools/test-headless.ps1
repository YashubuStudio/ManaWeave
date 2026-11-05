$ErrorActionPreference = "Stop"
$projectRoot = Split-Path $PSScriptRoot -Parent
& dotnet build (Join-Path $projectRoot "Tests/PortableCheck.csproj") -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& dotnet run --project (Join-Path $projectRoot "Tests/CoreHarness.csproj") -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& dotnet run --project (Join-Path $projectRoot "Tests/MagicHarness.csproj") -c Release -- (Join-Path $projectRoot "UnityProject/Assets/Resources/ManaWeave/magic_model.bytes")
exit $LASTEXITCODE
