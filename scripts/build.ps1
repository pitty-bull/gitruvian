# Compila a solução em Release (sem gerar instalador; para isso use scripts\package.ps1).
. "$PSScriptRoot\_common.ps1"
Set-Location $Root
Assert-Dotnet
Invoke-Checked { dotnet build $Solution -c Release }
Write-Host "OK: build Release concluído (versão $(Get-AppVersion))."
