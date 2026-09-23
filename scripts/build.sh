#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet build "$repo_root/Jellyfin.Plugin.MetaTagger/Jellyfin.Plugin.MetaTagger.csproj" --configuration Release --nologo "$@"
