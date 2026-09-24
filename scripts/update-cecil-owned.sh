#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
patcher="$repo_root/Optimum.Patcher/bin/Release/net10.0/Optimum.Patcher.dll"
output="$repo_root/patches/cecil-owned.list"
temporary="$(mktemp "$repo_root/patches/.cecil-owned.XXXXXX")"
trap 'rm -f -- "$temporary"' EXIT

dotnet build "$repo_root/Optimum.Patcher/Optimum.Patcher.csproj" -c Release --nologo -v quiet
{
  printf '%s\n' \
    '# Generated from Optimum.Patcher/PatchManifest.cs.' \
    '# Regenerate: bash scripts/update-cecil-owned.sh' \
    '# Source patches with a release effect transplanted into vanilla by Mono.Cecil.'
  dotnet "$patcher" --list-cecil-owned "$repo_root" | tr -d '\r'
} > "$temporary"
mv -- "$temporary" "$output"
