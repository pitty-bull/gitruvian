# Funções compartilhadas pelos scripts PowerShell. Não execute diretamente.
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot

function Test-Uv { return [bool](Get-Command uv -ErrorAction SilentlyContinue) }

function Get-VenvBin([string]$Venv) {
    if (Test-Path (Join-Path $Venv 'Scripts')) { return (Join-Path $Venv 'Scripts') }
    return (Join-Path $Venv 'bin')
}

function Find-Python312 {
    foreach ($candidate in @('python3.12', 'python3', 'python')) {
        $cmd = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($cmd) {
            & $cmd.Source -c 'import sys; raise SystemExit(sys.version_info[:2] != (3, 12))' 2>$null
            if ($LASTEXITCODE -eq 0) { return $cmd.Source }
        }
    }
    $launcher = Get-Command py -ErrorAction SilentlyContinue
    if ($launcher) {
        & $launcher.Source -3.12 -c 'import sys' 2>$null
        if ($LASTEXITCODE -eq 0) { return "py -3.12" }
    }
    return $null
}

function Use-ProjectEnvFile {
    # dev/run podem ser chamados de qualquer pasta (o repositório-alvo é a pasta atual);
    # o .env lido é o deste projeto, salvo se GITRUVIAN_ENV_FILE já estiver definido.
    if (-not $env:GITRUVIAN_ENV_FILE -and (Test-Path (Join-Path $Root '.env'))) {
        $env:GITRUVIAN_ENV_FILE = Join-Path $Root '.env'
    }
}

function Assert-DevEnv {
    if (-not (Test-Path (Join-Path $Root '.venv'))) {
        Write-Error 'Ambiente de desenvolvimento ausente. Rode primeiro: scripts\install.ps1'
    }
}

function Invoke-Checked([scriptblock]$Block) {
    & $Block
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
