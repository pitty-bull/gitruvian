# Instala o Gitruvian em .venv (Python 3.12) com dependências de desenvolvimento.
. "$PSScriptRoot\_common.ps1"
Set-Location $Root

if (Test-Uv) {
    Invoke-Checked { uv sync --python 3.12 --all-extras }
} else {
    $py = Find-Python312
    if (-not $py) { Write-Error 'Python 3.12 não encontrado. Instale-o ou instale o uv: https://docs.astral.sh/uv/' }
    Invoke-Checked { Invoke-Expression "$py -m venv .venv" }
    $bin = Get-VenvBin '.venv'
    Invoke-Checked { & "$bin\python" -m pip install --upgrade pip }
    Invoke-Checked { & "$bin\python" -m pip install -e '.[anthropic]' 'pytest>=8.0' 'build>=1.2' }
}

if (-not (Test-Path '.env')) {
    Copy-Item '.env.example' '.env'
    Write-Host 'INFO: .env criado a partir de .env.example (modo padrão: sem modelo, totalmente local).'
}
Write-Host 'OK: instalação concluída. Experimente: scripts\dev.ps1 sample create .\loja-exemplo'
