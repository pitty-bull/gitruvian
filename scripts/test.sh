#!/usr/bin/env sh
# Roda os testes (núcleo + interface headless). Aceita argumentos do dotnet test.
. "$(dirname "$0")/_common.sh"
cd "$ROOT"
require_dotnet
require_git
exec dotnet test "$SOLUTION" "$@"
