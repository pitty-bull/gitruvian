# Roda o Gitruvian a partir do código-fonte (instalação editável). Ex.: scripts\dev.ps1 doctor
. "$PSScriptRoot\_common.ps1"
Assert-DevEnv
Use-ProjectEnvFile
if (Test-Uv) { uv run --project $Root --python 3.12 gitruvian @args; exit $LASTEXITCODE }
& "$(Get-VenvBin (Join-Path $Root '.venv'))\gitruvian" @args
exit $LASTEXITCODE
