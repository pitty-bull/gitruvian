# Modelo determinístico SOMENTE PARA TESTES (usado via GITRUVIAN_PROVIDER=command).
# Não representa a qualidade de nenhum modelo real: confere se o prompt trouxe o contexto esperado
# (e nenhum segredo) e devolve sempre a mesma correção.
$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $utf8)
$prompt = $reader.ReadToEnd()

if ($prompt.Contains('supersecret')) {
    [Console]::Error.WriteLine('ERRO: o prompt contém um segredo')
    exit 3
}
if (-not $prompt.Contains('### ARQUIVO: calc.ps1') -or -not $prompt.Contains('return $subtotal - $Discount')) {
    [Console]::Error.WriteLine('ERRO: o contexto não trouxe calc.ps1')
    exit 4
}

$reply = @(
    'COMMIT: Aplica o desconto como percentual'
    ''
    '```diff'
    'diff --git a/calc.ps1 b/calc.ps1'
    '--- a/calc.ps1'
    '+++ b/calc.ps1'
    '@@ -1,5 +1,5 @@'
    ' function Get-Total([double[]]$Prices, [double]$Discount = 0.0) {'
    '     # Soma os preços e aplica um desconto percentual (0.1 = 10%).'
    '     $subtotal = ($Prices | Measure-Object -Sum).Sum'
    '-    return $subtotal - $Discount'
    '+    return $subtotal - $subtotal * $Discount'
    ' }'
    '```'
    ''
) -join "`n"
$bytes = $utf8.GetBytes($reply)
$out = [Console]::OpenStandardOutput()
$out.Write($bytes, 0, $bytes.Length)
$out.Flush()
exit 0
