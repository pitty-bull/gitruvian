#!/usr/bin/env sh
# Roda a suíte de testes (unitários + smoke test de ponta a ponta). Aceita argumentos do pytest.
. "$(dirname "$0")/_common.sh"
cd "$ROOT"
require_dev_env
if has_uv; then exec uv run --python 3.12 pytest "$@"; fi
exec "$(venv_bin .venv)/python" -m pytest "$@"
