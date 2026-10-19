# Funções compartilhadas pelos scripts (POSIX sh). Não execute diretamente.
set -eu
ROOT=$(cd "$(dirname "$0")/.." && pwd)

has_uv() { command -v uv >/dev/null 2>&1; }

venv_bin() {
  # Windows (Git Bash) usa Scripts/; Linux e macOS usam bin/.
  if [ -d "$1/Scripts" ]; then echo "$1/Scripts"; else echo "$1/bin"; fi
}

find_python312() {
  for candidate in python3.12 python3 python; do
    if command -v "$candidate" >/dev/null 2>&1 &&
       "$candidate" -c 'import sys; raise SystemExit(sys.version_info[:2] != (3, 12))' 2>/dev/null; then
      command -v "$candidate"; return 0
    fi
  done
  return 1
}

use_project_env_file() {
  # dev/run podem ser chamados de qualquer pasta (o repositório-alvo é a pasta atual);
  # o .env lido é o deste projeto, salvo se GITRUVIAN_ENV_FILE já estiver definido.
  if [ -z "${GITRUVIAN_ENV_FILE:-}" ] && [ -f "$ROOT/.env" ]; then
    GITRUVIAN_ENV_FILE="$ROOT/.env"; export GITRUVIAN_ENV_FILE
  fi
}

require_dev_env() {
  if [ ! -d "$ROOT/.venv" ]; then
    echo "ERRO: ambiente de desenvolvimento ausente. Rode primeiro: scripts/install.sh" >&2
    exit 1
  fi
}
