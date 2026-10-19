"""Dados de exemplo: um repositório fictício, claramente marcado e fácil de apagar."""

from __future__ import annotations

import os
import shutil
import stat
from pathlib import Path

from gitruvian import gitops
from gitruvian.app import App, open_app
from gitruvian.config import Settings
from gitruvian.diffs import validate_diff
from gitruvian.indexer import index_repo
from gitruvian.models import Proposal
from gitruvian.workflow import TEST_COMMAND_KEY

MARKER_FILE = "DADOS_DE_EXEMPLO.md"
MARKER_TEXT = "GITRUVIAN-SAMPLE-REPO"
SAMPLE_TEST_COMMAND = "{python} -m unittest discover -s tests"
SAMPLE_INSTRUCTION = "[EXEMPLO] Frete grátis deve valer também para pedidos de exatamente R$ 200,00"

_FILES: dict[str, str] = {
    MARKER_FILE: f"""# Dados de exemplo do Gitruvian

{MARKER_TEXT}

Este repositório é FICTÍCIO e foi gerado por `gitruvian sample create`.
A "Loja Exemplo", seus produtos e preços não existem.

Para apagar tudo: `gitruvian sample remove <esta pasta>` (ou apague a pasta).
""",
    "README.md": """# Loja Exemplo (dados fictícios)

Pequeno carrinho de compras usado para demonstrar o ciclo do Gitruvian.
Há um bug conhecido: o frete grátis não vale para pedidos de exatamente R$ 200,00.

Rodar os testes: `python -m unittest discover -s tests`
""",
    ".gitignore": "__pycache__/\n*.pyc\n.venv/\n",
    "loja/__init__.py": '"""[EXEMPLO] Pacote fictício da Loja Exemplo."""\n',
    "loja/carrinho.py": '''"""[EXEMPLO] Carrinho de compras da Loja Exemplo (dados fictícios)."""

from dataclasses import dataclass, field
from decimal import ROUND_HALF_UP, Decimal

FRETE_PADRAO = Decimal("19.90")
FRETE_GRATIS_A_PARTIR_DE = Decimal("200.00")


@dataclass
class Item:
    sku: str
    descricao: str
    preco_unitario: Decimal
    quantidade: int = 1

    def subtotal(self) -> Decimal:
        return self.preco_unitario * self.quantidade


@dataclass
class Carrinho:
    itens: list[Item] = field(default_factory=list)
    cupom_percentual: Decimal = Decimal("0")

    def adicionar(self, item: Item) -> None:
        if item.quantidade <= 0:
            raise ValueError("a quantidade deve ser positiva")
        self.itens.append(item)

    def subtotal(self) -> Decimal:
        return sum((item.subtotal() for item in self.itens), Decimal("0"))

    def desconto(self) -> Decimal:
        bruto = self.subtotal() * self.cupom_percentual / 100
        return bruto.quantize(Decimal("0.01"), rounding=ROUND_HALF_UP)

    def frete(self) -> Decimal:
        if self.subtotal() - self.desconto() > FRETE_GRATIS_A_PARTIR_DE:
            return Decimal("0.00")
        return FRETE_PADRAO

    def total(self) -> Decimal:
        return self.subtotal() - self.desconto() + self.frete()
''',
    "tests/__init__.py": "",
    "tests/test_carrinho.py": '''"""[EXEMPLO] Testes do carrinho fictício."""

import unittest
from decimal import Decimal

from loja.carrinho import FRETE_PADRAO, Carrinho, Item


class TestCarrinho(unittest.TestCase):
    def test_subtotal_soma_itens(self):
        carrinho = Carrinho()
        carrinho.adicionar(Item("CAN-001", "Caneca de cerâmica", Decimal("34.90"), 2))
        carrinho.adicionar(Item("CAD-010", "Caderno pautado", Decimal("18.50")))
        self.assertEqual(carrinho.subtotal(), Decimal("88.30"))

    def test_cupom_percentual(self):
        carrinho = Carrinho(cupom_percentual=Decimal("10"))
        carrinho.adicionar(Item("LUM-200", "Luminária de mesa", Decimal("129.90")))
        self.assertEqual(carrinho.desconto(), Decimal("12.99"))

    def test_frete_padrao_abaixo_do_minimo(self):
        carrinho = Carrinho()
        carrinho.adicionar(Item("CAN-001", "Caneca de cerâmica", Decimal("34.90")))
        self.assertEqual(carrinho.frete(), FRETE_PADRAO)

    def test_frete_gratis_no_valor_minimo_exato(self):
        carrinho = Carrinho()
        carrinho.adicionar(Item("CAD-300", "Cadeira dobrável", Decimal("100.00"), 2))
        self.assertEqual(carrinho.frete(), Decimal("0.00"))

    def test_quantidade_invalida(self):
        with self.assertRaises(ValueError):
            Carrinho().adicionar(Item("CAN-001", "Caneca de cerâmica", Decimal("34.90"), 0))


if __name__ == "__main__":
    unittest.main()
''',
}

_BUGGY = "        if self.subtotal() - self.desconto() > FRETE_GRATIS_A_PARTIR_DE:"
_FIXED = "        if self.subtotal() - self.desconto() >= FRETE_GRATIS_A_PARTIR_DE:"


class SampleError(RuntimeError):
    pass


def _write(root: Path, rel: str, content: str) -> None:
    target = root / rel
    target.parent.mkdir(parents=True, exist_ok=True)
    with target.open("w", encoding="utf-8", newline="\n") as fh:
        fh.write(content)


def create_sample_repo(path: Path, settings: Settings | None = None) -> tuple[App, Proposal]:
    """Cria o repositório de exemplo, indexa e registra uma proposta de exemplo."""
    path = path.resolve()
    if path.exists() and any(path.iterdir()):
        raise SampleError(f"a pasta já existe e não está vazia: {path}")
    path.mkdir(parents=True, exist_ok=True)
    for rel, content in _FILES.items():
        _write(path, rel, content)

    gitops.run_git(path, "init", "-q")
    gitops.run_git(path, "config", "core.autocrlf", "false")
    gitops.run_git(path, "config", "user.name", "Gitruvian Exemplo")
    gitops.run_git(path, "config", "user.email", "exemplo@example.invalid")
    gitops.run_git(path, "add", "-A")
    gitops.commit(path, "[EXEMPLO] Commit inicial da Loja Exemplo (dados fictícios)\n")

    # Gera o diff de correção a partir do próprio git, garantindo que ele aplica.
    carrinho = path / "loja" / "carrinho.py"
    _write(path, "loja/carrinho.py", _FILES["loja/carrinho.py"].replace(_BUGGY, _FIXED))
    diff = gitops.diff_text(path, paths=["loja/carrinho.py"])
    _write(path, "loja/carrinho.py", _FILES["loja/carrinho.py"])
    assert carrinho.read_text(encoding="utf-8") == _FILES["loja/carrinho.py"]
    parsed = validate_diff(diff)

    app = open_app(path, create=True, settings=settings)
    app.store.set_setting(TEST_COMMAND_KEY, SAMPLE_TEST_COMMAND)
    index_repo(app.store, app.repo)
    proposal = app.store.add_proposal(
        instruction=SAMPLE_INSTRUCTION,
        provider="exemplo",
        model="-",
        diff=parsed.text,
        files=parsed.files,
        commit_message="[EXEMPLO] Corrige frete grátis no valor mínimo exato",
        is_sample=True,
    )
    app.store.add_event(
        "sample",
        "[EXEMPLO] Repositório e proposta de exemplo criados",
        proposal_id=proposal.id,
        data={"files": parsed.files},
        is_sample=True,
    )
    return app, proposal


def is_sample_repo(path: Path) -> bool:
    marker = path / MARKER_FILE
    try:
        return marker.is_file() and MARKER_TEXT in marker.read_text(encoding="utf-8")
    except OSError:
        return False


def _force_remove(func, target, _exc) -> None:  # noqa: ANN001
    os.chmod(target, stat.S_IWRITE)
    func(target)


def remove_sample_repo(path: Path) -> None:
    path = path.resolve()
    if not path.is_dir():
        raise SampleError(f"pasta não encontrada: {path}")
    if not is_sample_repo(path):
        raise SampleError(f"{path} não tem o marcador {MARKER_FILE}; por segurança, nada foi apagado")
    shutil.rmtree(path, onexc=_force_remove)
