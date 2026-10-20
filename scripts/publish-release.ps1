# Publica a versão gerada por scripts\package.ps1 como release no GitHub (fonte das atualizações automáticas).
# AÇÃO EXTERNA: cria uma release pública em https://github.com/pitty-bull/gitruvian. Pede confirmação antes.
# O token vem de GITHUB_TOKEN no .env do projeto (ou da variável de ambiente) e nunca é impresso.
# Precisa de permissão "Contents: read and write" no repositório.
param(
    [string]$Version = '',
    [string]$RepoUrl = 'https://github.com/pitty-bull/gitruvian',
    [switch]$PreRelease
)
. "$PSScriptRoot\_common.ps1"
Set-Location $Root
Assert-Dotnet
if (-not $Version) { $Version = Get-AppVersion }
$releases = Join-Path $Artifacts 'releases'
if (-not (Get-ChildItem $releases -Filter "*$Version*.nupkg" -ErrorAction SilentlyContinue)) {
    Write-Error "Nenhum pacote $Version em $releases. Rode antes: scripts\package.ps1 -Version $Version"
}
$token = $env:GITHUB_TOKEN
if (-not $token) { $token = Read-DotEnvValue 'GITHUB_TOKEN' }
if (-not $token) { Write-Error 'Defina GITHUB_TOKEN no .env do projeto (veja .env.example).' }

$answer = Read-Host "Publicar Gitruvian $Version em $RepoUrl (release pública)? Digite 'publicar' para confirmar"
if ($answer -ne 'publicar') { Write-Host 'Cancelado. Nada foi enviado.'; exit 1 }

$vpkArgs = @('upload', 'github', '--repoUrl', $RepoUrl, '--token', $token, '--outputDir', $releases,
    '--publish', '--releaseName', "Gitruvian $Version", '--tag', "v$Version")
if ($PreRelease) { $vpkArgs += '--pre' }
Invoke-Checked { dotnet vpk @vpkArgs }
Write-Host "OK: release v$Version publicada. Quem tem o app instalado recebe a atualização na próxima verificação."
