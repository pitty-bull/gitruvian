# Funções compartilhadas pelos scripts POSIX do Gitruvian (.NET). Não execute diretamente.
set -eu
ROOT=$(cd "$(dirname "$0")/.." && pwd)
SOLUTION="$ROOT/Gitruvian.slnx"
DESKTOP="$ROOT/src/Gitruvian.Desktop/Gitruvian.Desktop.csproj"
# Sem telemetria da CLI do .NET nos scripts do projeto.
DOTNET_CLI_TELEMETRY_OPTOUT=1; DOTNET_NOLOGO=1; export DOTNET_CLI_TELEMETRY_OPTOUT DOTNET_NOLOGO

require_dotnet() {
  command -v dotnet >/dev/null 2>&1 || { echo "ERRO: SDK do .NET não encontrado. Instale o .NET 10 SDK: https://dotnet.microsoft.com/download" >&2; exit 1; }
  dotnet --list-sdks | grep -q "^10\." || { echo "ERRO: é preciso o .NET 10 SDK." >&2; exit 1; }
}

require_git() {
  command -v git >/dev/null 2>&1 || { echo "ERRO: git não encontrado no PATH." >&2; exit 1; }
}

use_project_env_file() {
  if [ -z "${GITRUVIAN_ENV_FILE:-}" ] && [ -f "$ROOT/.env" ]; then
    GITRUVIAN_ENV_FILE="$ROOT/.env"; export GITRUVIAN_ENV_FILE
  fi
}
