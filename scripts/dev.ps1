# Abre o Gitruvian a partir do código-fonte (Debug). Argumento opcional: pasta de um repositório para abrir.
# Ex.: scripts\dev.ps1    ou    scripts\dev.ps1 D:\meu-projeto
. "$PSScriptRoot\_common.ps1"
Assert-Dotnet
Use-ProjectEnvFile
& dotnet run --project $DesktopProject -- @args
exit $LASTEXITCODE
