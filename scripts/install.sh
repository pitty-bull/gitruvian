#!/usr/bin/env sh
# Instala o Gitruvian em .venv (Python 3.12) com dependências de desenvolvimento.
. "$(dirname "$0")/_common.sh"
cd "$ROOT"

if has_uv; then
  uv sync --python 3.12 --all-extras
else
  PY=$(find_python312) || { echo "ERRO: Python 3.12 não encontrado. Instale-o ou instale o uv: https://docs.astral.sh/uv/" >&2; exit 1; }
  "$PY" -m venv .venv
  BIN=$(venv_bin .venv)
  "$BIN/python" -m pip install --upgrade pip
  "$BIN/python" -m pip install -e ".[anthropic]" "pytest>=8.0" "build>=1.2"
fi

if [ ! -f .env ]; then
  cp .env.example .env
  echo "INFO: .env criado a partir de .env.example (modo padrão: sem modelo, totalmente local)."
fi
echo "OK: instalação concluída. Experimente: scripts/dev.sh sample create ./loja-exemplo"
