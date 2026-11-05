param(
    [Parameter(Mandatory=$true)][string]$UnityEditor,
    [ValidateSet("Windows", "Linux")][string]$Target = "Windows"
)
$ErrorActionPreference = "Stop"
$projectRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $projectRoot "UnityProject"
$method = "ManaWeave.Editor.ProjectTools.Build" + $Target
$buildTarget = if ($Target -eq "Windows") { "StandaloneWindows64" } else { "StandaloneLinux64" }
$logPath = Join-Path $projectRoot "unity-build.log"
if (-not (Test-Path -LiteralPath $UnityEditor -PathType Leaf)) { throw "Unity Editor executable not found: $UnityEditor" }
& $UnityEditor -batchmode -nographics -quit -projectPath $project -buildTarget $buildTarget -executeMethod $method -logFile $logPath
if ($LASTEXITCODE -ne 0) { throw "Unity build failed; read $logPath" }
Write-Host "Build complete: $project/Builds/$Target"
