#!/usr/bin/env bash
# Source from any working directory; prefer the repository-local pinned SDK.
easysellput_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
if [[ -x "$easysellput_root/tools/dotnet/dotnet" ]]; then
  export DOTNET_ROOT="$easysellput_root/tools/dotnet"
  export PATH="$DOTNET_ROOT:$PATH"
fi
export DOTNET_CLI_HOME="$easysellput_root/tools/.dotnet-home"
export NUGET_PACKAGES="$easysellput_root/tools/nuget-packages"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_CLI_UI_LANGUAGE=en
unset easysellput_root
