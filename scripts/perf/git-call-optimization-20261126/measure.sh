#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "usage: $0 SOURCE_WORKTREE OUTPUT_JSON" >&2
  exit 2
fi

source_root="$(cd "$1" && pwd)"
output_json="$2"
script_dir="$(cd "$(dirname "$0")" && pwd)"

dotnet run --project "$script_dir/GitCallOptimization.Harness.fsproj" -c Release \
  -p:JarvisClientProject="$source_root/Client/Client.fsproj" \
  -p:JarvisServerProject="$source_root/Server/Server.fsproj" -- \
  --source-root "$source_root" --out "$output_json"
