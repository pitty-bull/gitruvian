# Execução em estilo de produção: roda o Gitruvian.exe publicado (Release, autossuficiente) de artifacts\publish.
# Gera a publicação se ela ainda não existir. Argumento opcional: pasta de um repositório para abrir.
# Para testar o instalador e a atualização automática, use artifacts\releases\Gitruvian-win-Setup.exe.
. "$PSScriptRoot\_common.ps1"
$exe = Join-Path $Artifacts 'publish\Gitruvian.exe'
if (-not (Test-Path $exe)) {
    Assert-Dotnet
    Invoke-Checked { dotnet publish $DesktopProject -c Release -r $Runtime --self-contained true -o (Join-Path $Artifacts 'publish') }
}
Use-ProjectEnvFile
Start-Process -FilePath $exe -ArgumentList $args
