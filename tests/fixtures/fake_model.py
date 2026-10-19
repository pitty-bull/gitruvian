"""Modelo determinístico SOMENTE PARA TESTES (usado via GITRUVIAN_PROVIDER=command).

Não representa a qualidade de nenhum modelo real: ele confere se o prompt trouxe o
contexto esperado (e nenhum segredo) e devolve sempre a mesma correção.
"""

import sys

prompt = sys.stdin.buffer.read().decode("utf-8")

if "supersecret" in prompt:
    print("ERRO: o prompt contém um segredo", file=sys.stderr)
    sys.exit(3)
if "### ARQUIVO: calc.py" not in prompt or "return subtotal - discount" not in prompt:
    print("ERRO: o contexto não trouxe calc.py", file=sys.stderr)
    sys.exit(4)

sys.stdout.write(
    "COMMIT: Aplica o desconto como percentual\n\n"
    "```diff\n"
    "diff --git a/calc.py b/calc.py\n"
    "--- a/calc.py\n"
    "+++ b/calc.py\n"
    "@@ -1,4 +1,4 @@\n"
    " def total(prices, discount=0.0):\n"
    '     """Soma os preços e aplica um desconto percentual (0.1 = 10%)."""\n'
    "     subtotal = sum(prices)\n"
    "-    return subtotal - discount\n"
    "+    return subtotal - subtotal * discount\n"
    "```\n"
)
