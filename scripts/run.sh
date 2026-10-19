#!/usr/bin/env sh
# Execução local em estilo de produção: instala o wheel de dist/ num ambiente isolado
# (.venv-prod, sem código-fonte editável nem dependências de desenvolvimento) e executa.
# Rode a partir da pasta do repositório que quer usar. Use GITRUVIAN_WITH_ANTHROPIC=1 para incluir o SDK opcional da Anthropic.
. "$(dirname "$0")/_common.sh"
use_project_env_file
PROD="$ROOT/.venv-prod"

WHEEL=$(ls -t "$ROOT"/dist/gitruvian-*.whl 2>/dev/null | head -n 1 || true)
if [ -z "$WHEEL" ]; then
  sh "$ROOT/scripts/build.sh" >/dev/null
  WHEEL=$(ls -t "$ROOT"/dist/gitruvian-*.whl | head -n 1)
fi
EXTRA=""
if [ "${GITRUVIAN_WITH_ANTHROPIC:-0}" = "1" ]; then EXTRA="anthropic>=1.0"; fi
STAMP="$PROD/.installed"
WANT="$WHEEL $(cksum < "$WHEEL") $EXTRA"

if [ ! -f "$STAMP" ] || [ "$(cat "$STAMP")" != "$WANT" ]; then
  echo "... instalando $(basename "$WHEEL") em .venv-prod" >&2
  if has_uv; then
    uv venv --python 3.12 --quiet --clear "$PROD"
    uv pip install --python "$PROD" --quiet "$WHEEL" $EXTRA
  else
    PY=$(find_python312) || { echo "ERRO: Python 3.12 não encontrado." >&2; exit 1; }
    rm -rf "$PROD" && "$PY" -m venv "$PROD"
    "$(venv_bin "$PROD")/python" -m pip install --quiet "$WHEEL" $EXTRA
  fi
  echo "$WANT" > "$STAMP"
fi
exec "$(venv_bin "$PROD")/gitruvian" "$@"
