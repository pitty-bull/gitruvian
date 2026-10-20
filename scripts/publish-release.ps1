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

# Anexa o exe avulso (sem instalador) à mesma release, pela API do GitHub.
$standalone = Join-Path $releases 'Gitruvian-win-x64.exe'
if (Test-Path $standalone) {
    $ownerRepo = ($RepoUrl -replace '^https://github\.com/', '') -replace '\.git$', ''
    $headers = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
    $release = Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/$ownerRepo/releases/tags/v$Version"
    $existing = $release.assets | Where-Object { $_.name -eq 'Gitruvian-win-x64.exe' }
    if ($existing) { Invoke-RestMethod -Method Delete -Headers $headers -Uri $existing.url | Out-Null }
    $uploadUrl = ($release.upload_url -replace '\{\?name,label\}$', '') + '?name=Gitruvian-win-x64.exe'
    Invoke-RestMethod -Method Post -Headers $headers -ContentType 'application/octet-stream' -InFile $standalone -Uri $uploadUrl | Out-Null
    Write-Host 'OK: Gitruvian-win-x64.exe anexado à release.'
}
Write-Host "OK: release v$Version publicada. Quem tem o app instalado recebe a atualização na próxima verificação."
