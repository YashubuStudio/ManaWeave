#!/usr/bin/env bash
set -euo pipefail
project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet build "$project_root/Tests/PortableCheck.csproj" -c Release
dotnet run --project "$project_root/Tests/CoreHarness.csproj" -c Release
dotnet run --project "$project_root/Tests/MagicHarness.csproj" -c Release -- "$project_root/UnityProject/Assets/Resources/ManaWeave/magic_model.bytes"
