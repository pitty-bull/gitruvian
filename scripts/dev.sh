#!/usr/bin/env sh
# Roda o Gitruvian a partir do código-fonte (instalação editável). Ex.: scripts/dev.sh doctor
. "$(dirname "$0")/_common.sh"
require_dev_env
use_project_env_file
if has_uv; then exec uv run --project "$ROOT" --python 3.12 gitruvian "$@"; fi
exec "$(venv_bin "$ROOT/.venv")/gitruvian" "$@"
