# Gera wheel e sdist em dist\.
. "$PSScriptRoot\_common.ps1"
Set-Location $Root
if (Test-Path 'dist') { Remove-Item -Recurse -Force 'dist' }
if (Test-Uv) {
    Invoke-Checked { uv build }
} else {
    Assert-DevEnv
    Invoke-Checked { & "$(Get-VenvBin '.venv')\python" -m build }
}
Get-ChildItem 'dist' | ForEach-Object { $_.Name }
