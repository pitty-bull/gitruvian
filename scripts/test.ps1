# Roda a suíte de testes (unitários + smoke test de ponta a ponta). Aceita argumentos do pytest.
. "$PSScriptRoot\_common.ps1"
Set-Location $Root
Assert-DevEnv
if (Test-Uv) { uv run --python 3.12 pytest @args; exit $LASTEXITCODE }
& "$(Get-VenvBin '.venv')\python" -m pytest @args
exit $LASTEXITCODE
