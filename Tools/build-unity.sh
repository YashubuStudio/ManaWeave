#!/usr/bin/env bash
set -euo pipefail
if [[ $# -lt 1 ]]; then
  echo 'Usage: ./Tools/build-unity.sh /path/to/Unity [Linux|Windows]' >&2
  exit 2
fi
unity_editor="$1"
target="${2:-Linux}"
case "$target" in
  Linux) build_target=StandaloneLinux64 ;;
  Windows) build_target=StandaloneWindows64 ;;
  *) echo 'Target must be Linux or Windows.' >&2; exit 2 ;;
esac
project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
"$unity_editor" -batchmode -nographics -quit -projectPath "$project_root/UnityProject" -buildTarget "$build_target" -executeMethod "ManaWeave.Editor.ProjectTools.Build$target" -logFile "$project_root/unity-build.log"
echo "Build complete: $project_root/UnityProject/Builds/$target"
