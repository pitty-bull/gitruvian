# Roda os testes: unitários do núcleo + E2E da interface (Avalonia headless). Aceita argumentos do dotnet test.
# Ex.: scripts\test.ps1 --filter FullyQualifiedName~Transfer
. "$PSScriptRoot\_common.ps1"
Set-Location $Root
Assert-Dotnet
Assert-Git
& dotnet test $Solution @args
exit $LASTEXITCODE
