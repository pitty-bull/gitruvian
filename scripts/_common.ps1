# Funções compartilhadas pelos scripts PowerShell do Gitruvian (.NET). Não execute diretamente.
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Solution = Join-Path $Root 'Gitruvian.slnx'
$DesktopProject = Join-Path $Root 'src\Gitruvian.Desktop\Gitruvian.Desktop.csproj'
$Artifacts = Join-Path $Root 'artifacts'
$Runtime = 'win-x64'
# Sem telemetria da CLI do .NET nos scripts do projeto.
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Invoke-Checked([scriptblock]$Block) {
    & $Block
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Assert-Dotnet {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Error 'SDK do .NET não encontrado. Instale o .NET 10 SDK: https://dotnet.microsoft.com/download'
    }
    $sdks = & dotnet --list-sdks
    if (-not ($sdks | Where-Object { $_ -match '^10\.' })) {
        Write-Error "É preciso o .NET 10 SDK (encontrados: $($sdks -join '; ')). https://dotnet.microsoft.com/download"
    }
}

function Assert-Git {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Write-Error 'git não encontrado no PATH. Instale o Git: https://git-scm.com'
    }
}

function Get-AppVersion {
    [xml]$props = Get-Content (Join-Path $Root 'Directory.Build.props')
    return ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
}

function Use-ProjectEnvFile {
    # Em desenvolvimento, lê o .env deste projeto (salvo se GITRUVIAN_ENV_FILE já estiver definido).
    if (-not $env:GITRUVIAN_ENV_FILE -and (Test-Path (Join-Path $Root '.env'))) {
        $env:GITRUVIAN_ENV_FILE = Join-Path $Root '.env'
    }
}

function Read-DotEnvValue([string]$Key) {
    # Lê uma chave do .env do projeto sem imprimir o valor (usado para segredos de publicação).
    $file = Join-Path $Root '.env'
    if (-not (Test-Path $file)) { return $null }
    foreach ($line in Get-Content $file) {
        if ($line -match "^\s*$Key\s*=\s*(.*)$") {
            $value = $Matches[1].Trim().Trim('"').Trim("'")
            if ($value) { return $value }
        }
    }
    return $null
}
