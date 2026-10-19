#!/usr/bin/env sh
# Gera wheel e sdist em dist/.
. "$(dirname "$0")/_common.sh"
cd "$ROOT"
rm -rf dist
if has_uv; then
  uv build
else
  require_dev_env
  "$(venv_bin .venv)/python" -m build
fi
ls dist
