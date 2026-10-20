#!/usr/bin/env sh
# Compila a solução em Release (o instalador sai de scripts/package.ps1, só no Windows).
. "$(dirname "$0")/_common.sh"
cd "$ROOT"
require_dotnet
dotnet build "$SOLUTION" -c Release
