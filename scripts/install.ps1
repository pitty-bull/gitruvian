# Prepara o ambiente de desenvolvimento: confere .NET 10 e git, restaura pacotes e ferramentas (vpk)
# e cria o .env do projeto a partir do .env.example (modo padrão: sem modelo, totalmente local).
. "$PSScriptRoot\_common.ps1"
Set-Location $Root
Assert-Dotnet
Assert-Git
Invoke-Checked { dotnet tool restore }
Invoke-Checked { dotnet restore $Solution }
if (-not (Test-Path '.env')) {
    Copy-Item '.env.example' '.env'
    Write-Host 'INFO: .env criado a partir de .env.example (sem modelo: nada sai desta máquina).'
}
Write-Host 'OK: ambiente pronto. Abra o app com: scripts\dev.ps1   ·   testes: scripts\test.ps1'
