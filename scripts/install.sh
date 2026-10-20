#!/usr/bin/env sh
# Prepara o ambiente de desenvolvimento: .NET 10, git, pacotes, ferramentas (vpk) e o .env do projeto.
. "$(dirname "$0")/_common.sh"
cd "$ROOT"
require_dotnet
require_git
dotnet tool restore
dotnet restore "$SOLUTION"
if [ ! -f .env ]; then
  cp .env.example .env
  echo "INFO: .env criado a partir de .env.example (sem modelo: nada sai desta máquina)."
fi
echo "OK: ambiente pronto. Abra o app com: scripts/dev.sh   ·   testes: scripts/test.sh"
