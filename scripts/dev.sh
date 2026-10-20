#!/usr/bin/env sh
# Abre o Gitruvian a partir do código-fonte (Debug). Argumento opcional: pasta de um repositório.
. "$(dirname "$0")/_common.sh"
require_dotnet
use_project_env_file
exec dotnet run --project "$DESKTOP" -- "$@"
