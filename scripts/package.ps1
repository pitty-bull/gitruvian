# Gera o executável autossuficiente e o instalador com atualização automática (Velopack).
#   artifacts\publish\   -> Gitruvian.exe e dependências (não exige .NET instalado)
#   artifacts\releases\  -> Gitruvian-win-Setup.exe, Gitruvian-win-Portable.zip, pacotes .nupkg e releases.win.json
# Parâmetros:
#   -Version 0.3.1        versão do pacote (padrão: Directory.Build.props)
#   -SignParams "..."     parâmetros do signtool para assinar (ex.: '/a /fd SHA256 /f cert.pfx /p SENHA');
#                         sem isso o instalador sai SEM assinatura e o Windows SmartScreen vai avisar.
param(
    [string]$Version = '',
    [string]$SignParams = ''
)
. "$PSScriptRoot\_common.ps1"
Set-Location $Root
Assert-Dotnet
if (-not $Version) { $Version = Get-AppVersion }
$publish = Join-Path $Artifacts 'publish'
$releases = Join-Path $Artifacts 'releases'
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }

Invoke-Checked { dotnet tool restore }
Write-Host "... publicando Gitruvian $Version ($Runtime, autossuficiente)"
Invoke-Checked { dotnet publish $DesktopProject -c Release -r $Runtime --self-contained true -p:Version=$Version -o $publish }

Write-Host '... gerando instalador e pacotes de atualização (vpk pack)'
$vpkArgs = @('pack', '--packId', 'Gitruvian', '--packVersion', $Version, '--packDir', $publish,
    '--mainExe', 'Gitruvian.exe', '--packTitle', 'Gitruvian', '--packAuthors', 'Gitruvian',
    '--icon', (Join-Path $Root 'src\Gitruvian.Desktop\Assets\gitruvian.ico'), '--outputDir', $releases)
if ($SignParams) { $vpkArgs += @('--signParams', $SignParams) }
Invoke-Checked { dotnet vpk @vpkArgs }

Get-ChildItem $releases | Where-Object { $_.Name -like "*$Version*" -or $_.Name -like '*Setup*' -or $_.Name -like 'releases*' } |
    ForEach-Object { '{0,-48} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
if (-not $SignParams) {
    Write-Warning 'Instalador NÃO assinado: o Windows SmartScreen vai mostrar "Editor desconhecido". Veja README › Distribuição.'
}
