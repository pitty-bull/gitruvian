# Execução local em estilo de produção: instala o wheel de dist\ num ambiente isolado
# (.venv-prod, sem código-fonte editável nem dependências de desenvolvimento) e executa.
# Rode a partir da pasta do repositório que quer usar. Use $env:GITRUVIAN_WITH_ANTHROPIC = '1' para incluir o SDK opcional da Anthropic.
. "$PSScriptRoot\_common.ps1"
Use-ProjectEnvFile
$prod = Join-Path $Root '.venv-prod'

$wheel = Get-ChildItem (Join-Path $Root 'dist\gitruvian-*.whl') -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $wheel) {
    & "$PSScriptRoot\build.ps1" | Out-Null
    $wheel = Get-ChildItem (Join-Path $Root 'dist\gitruvian-*.whl') | Sort-Object LastWriteTime -Descending | Select-Object -First 1
}
$extra = @()
if ($env:GITRUVIAN_WITH_ANTHROPIC -eq '1') { $extra = @('anthropic>=1.0') }
$stamp = Join-Path $prod '.installed'
$want = "$($wheel.Name) $((Get-FileHash $wheel.FullName).Hash) $($extra -join ' ')"

if (-not (Test-Path $stamp) -or (Get-Content $stamp -Raw).Trim() -ne $want.Trim()) {
    Write-Host "... instalando $($wheel.Name) em .venv-prod"
    if (Test-Uv) {
        Invoke-Checked { uv venv --python 3.12 --quiet --clear $prod }
        Invoke-Checked { uv pip install --python $prod --quiet $wheel.FullName @extra }
    } else {
        $py = Find-Python312
        if (-not $py) { Write-Error 'Python 3.12 não encontrado.' }
        if (Test-Path $prod) { Remove-Item -Recurse -Force $prod }
        Invoke-Checked { Invoke-Expression "$py -m venv `"$prod`"" }
        Invoke-Checked { & "$(Get-VenvBin $prod)\python" -m pip install --quiet $wheel.FullName @extra }
    }
    Set-Content -Path $stamp -Value $want -Encoding utf8
}
& "$(Get-VenvBin $prod)\gitruvian" @args
exit $LASTEXITCODE
