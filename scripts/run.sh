#!/usr/bin/env sh
# Execução em estilo de produção: publica em Release (autossuficiente) para o sistema atual e executa.
# O instalador com atualização automática é gerado só no Windows (scripts/package.ps1).
. "$(dirname "$0")/_common.sh"
cd "$ROOT"
require_dotnet
RID=$(dotnet --info | sed -n "s/^ *RID: *//p" | head -n 1)
OUT="$ROOT/artifacts/publish-$RID"
if [ ! -d "$OUT" ]; then
  dotnet publish "$DESKTOP" -c Release -r "$RID" --self-contained true -o "$OUT"
fi
use_project_env_file
if [ -f "$OUT/Gitruvian.exe" ]; then exec "$OUT/Gitruvian.exe" "$@"; fi
exec "$OUT/Gitruvian" "$@"
