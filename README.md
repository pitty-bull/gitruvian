# Gitruvian

Assistente Git **local, privado e de usuário único**, feito para substituir no dia a dia o
ciclo principal de um cliente como o GitKraken — direto no terminal:

```
indexar o código → pedir uma alteração → receber um diff → revisar → aplicar → rodar os testes → commitar ou reverter
                                       (cada passo fica registrado num banco SQLite local)
```

Stack: **Python 3.12 + Typer + SQLite** (Rich vem junto com o Typer). Sem servidor, sem conta,
sem analytics, sem telemetria, sem anúncios.

---

## Instalação

Pré-requisitos: **Python 3.12** e **git** no PATH. O [uv](https://docs.astral.sh/uv/) é opcional
(os scripts usam o uv se ele existir; senão, `venv` + `pip`).

```bash
# Linux / macOS / Git Bash
scripts/install.sh

# Windows PowerShell
scripts\install.ps1
```

O instalador cria `.venv/`, instala o pacote em modo editável com as dependências de
desenvolvimento e copia `.env.example` para `.env` (se ainda não existir). O modo padrão do
`.env` é **sem modelo** — nada sai da sua máquina.

### Scripts

| Tarefa | sh | PowerShell | O que faz |
|---|---|---|---|
| Instalação | `scripts/install.sh` | `scripts\install.ps1` | cria `.venv`, instala, gera `.env` |
| Desenvolvimento | `scripts/dev.sh <args>` | `scripts\dev.ps1 <args>` | roda a CLI a partir do código-fonte |
| Testes | `scripts/test.sh` | `scripts\test.ps1` | unitários + smoke test de ponta a ponta |
| Build | `scripts/build.sh` | `scripts\build.ps1` | gera wheel e sdist em `dist/` |
| Produção local | `scripts/run.sh <args>` | `scripts\run.ps1 <args>` | instala o wheel num ambiente isolado (`.venv-prod`) e executa |

`dev` e `run` podem ser chamados **de dentro do repositório em que você quer trabalhar** (ex.:
`~/gitruvian/scripts/run.sh status`); eles leem o `.env` da pasta do Gitruvian, a menos que
`GITRUVIAN_ENV_FILE` já esteja definido. Para incluir o SDK opcional da Anthropic na execução
de produção: `GITRUVIAN_WITH_ANTHROPIC=1 scripts/run.sh ...`.

Se preferir instalar como ferramenta global: `uv tool install dist/gitruvian-0.1.0-py3-none-any.whl`
(ou `pipx install ...`).

---

## Primeiros passos (com dados de exemplo)

```bash
gitruvian sample create ./loja-exemplo   # repositório FICTÍCIO com um bug e uma proposta de correção
cd loja-exemplo
gitruvian                                # painel: branch, índice, modelo, propostas
gitruvian test                           # 1 teste falha (o bug)
gitruvian proposals                      # a proposta aparece marcada como [EXEMPLO]
gitruvian show <id>                      # detalhes + diff
gitruvian apply <id>                     # aplica e roda os testes → passam
gitruvian accept <id>                    # commita só os arquivos da proposta
gitruvian history                        # trilha de auditoria
```

Os dados de exemplo são claramente marcados (`[EXEMPLO]`, arquivo `DADOS_DE_EXEMPLO.md`,
coluna `is_sample` no banco) e são fáceis de apagar:

```bash
gitruvian sample clear                    # apaga os registros de exemplo do banco do repositório atual
gitruvian sample remove ./loja-exemplo    # apaga a pasta (só se tiver o marcador de exemplo)
```

## Uso num repositório seu

```bash
cd meu-projeto
gitruvian init                       # cria .gitruvian/ (ignorado pelo git) e detecta o comando de testes
gitruvian config set test_command "python -m pytest -q"   # se a detecção não acertou
gitruvian index                      # índice local do código (incremental)
gitruvian go "validar e-mail vazio no cadastro"           # ciclo completo, com confirmações
```

`go` executa as 5 etapas e pergunta antes de aplicar e antes de commitar. Se os testes falharem,
oferece reverter. Use `--yes` para não perguntar (aplica; se falhar, reverte) e `--commit` para
commitar automaticamente quando os testes passarem.

### Comandos

| Grupo | Comandos |
|---|---|
| Painel / ajuda | `gitruvian` (painel), `gitruvian ui` (menu por teclado), `gitruvian doctor`, `--help` em qualquer comando |
| Git do dia a dia | `status`, `log [-g]`, `diff [--staged]`, `stage`, `unstage`, `commit -m [-a]`, `branches`, `switch [-c]` |
| Código | `index [--full]`, `search "termos"` |
| Ciclo com IA | `propose "pedido" [-f arquivo] [--patch-file x.diff]`, `proposals`, `show <id> [--raw]`, `apply <id> [-b branch] [--commit]`, `test [<id>]`, `accept <id>`, `revert <id>`, `reject <id>`, `go "pedido"` |
| Registro | `history [--proposal <id>]` |
| Dados | `export arquivo.json`, `import arquivo.json`, `config list/get/set/unset`, `sample create/remove/clear` |

IDs aceitam prefixo (mínimo 4 caracteres). Estados de uma proposta:
`proposta → aplicada → testes ok | testes falharam → commitada → revertida`, além de `rejeitada`.

Códigos de saída: `0` sucesso · `1` falha (testes, git, modelo) · `2` entrada inválida ·
`3` modelo indisponível (modo degradado).

---

## Modelos e modo degradado

O modelo é **opcional**. Configure no `.env` (veja `.env.example`):

| `GITRUVIAN_PROVIDER` | O que usa | Sai da máquina? |
|---|---|---|
| `none` (padrão) | nenhum modelo | não |
| `openai_compat` | servidor compatível com OpenAI em `GITRUVIAN_OPENAI_BASE_URL` (Ollama, LM Studio, llama.cpp, vLLM) | não, se for `localhost` |
| `command` | qualquer programa local que lê o prompt no stdin (ex.: `ollama run qwen2.5-coder:7b`) | depende do programa |
| `anthropic` | API da Anthropic, modelo padrão `claude-opus-5-5`; requer `pip install "gitruvian[anthropic]"` e `ANTHROPIC_API_KEY` | **sim** |

Provedores que enviam código para fora (`anthropic`, ou `openai_compat` com host que não seja
localhost) **só funcionam com `GITRUVIAN_ALLOW_REMOTE=1`** — consentimento explícito.

**Modo degradado (sem modelo):** tudo continua funcionando, exceto a geração automática do diff.
Você traz o diff (de outro editor, de um colega, de `git diff`, de qualquer ferramenta) com
`gitruvian propose "descrição" --patch-file mudanca.diff` ou `gitruvian go "descrição" -p mudanca.diff`,
e o Gitruvian valida, aplica, testa, commita/reverte e registra. `gitruvian doctor` mostra o modo
atual e explica o que falta. Se o servidor local estiver desligado ou a API fora do ar, a CLI
retorna o código `3` com essa mesma orientação.

O que é enviado ao modelo: o pedido, uma lista parcial de caminhos do repositório e o conteúdo
dos arquivos mais relevantes segundo a busca local (limitado por `GITRUVIAN_MAX_CONTEXT_CHARS`,
padrão 24 000 caracteres). Depois de cada proposta, a CLI informa quantos arquivos e caracteres
foram enviados, e `gitruvian show <id>` lista os arquivos/linhas usados.

---

## Permissões

O Gitruvian roda com as permissões do seu usuário e só faz o seguinte:

- **Lê** os arquivos rastreados/não ignorados do repositório (via `git ls-files`) para indexar.
- **Escreve** em `<repo>/.gitruvian/` (banco e log) e adiciona `/.gitruvian/` a `.git/info/exclude`
  (não altera o seu `.gitignore`). Em Linux/macOS a pasta fica com modo `700` e o banco com `600`.
- **Altera arquivos do repositório** somente quando você aplica/reverte uma proposta, e somente
  os arquivos listados no diff validado.
- **Executa** `git` e o comando de testes configurado (sem shell; o comando é dividido em argumentos).
- **Rede**: nenhuma, a menos que você configure um provedor de modelo. Nada é enviado a terceiros
  por padrão.

---

## Arquitetura

```
src/gitruvian/
  cli.py         Typer: comandos, estados vazios/carregando/erro/sucesso, menu `ui`
  workflow.py    ciclo propor → aplicar → testar → commitar/reverter, com eventos de auditoria
  models.py      modelo de dados + Store (SQLite, consultas parametrizadas, máquina de estados)
  indexer.py     indexação incremental (FTS5/BM25) e montagem do contexto para o modelo
  llm.py         provedores (anthropic, openai_compat, command) e explicação do modo degradado
  diffs.py       extração e validação de diffs não confiáveis
  gitops.py      wrapper do git via subprocess (lista de argumentos, timeouts)
  testrunner.py  detecção e execução do comando de testes
  transfer.py    exportação/importação JSON com validação estrita
  safety.py      caminhos sensíveis, redação de segredos, validação de entradas
  config.py      leitura de variáveis de ambiente e .env (somente chaves conhecidas)
  logs.py        log técnico só com metadados
  sample.py      repositório de exemplo fictício
  app.py         localização do repositório e da pasta de dados
```

Banco (`.gitruvian/gitruvian.db`, SQLite com WAL; versão do schema em `PRAGMA user_version`):

| Tabela | Conteúdo |
|---|---|
| `settings` | configurações do repositório (ex.: `test_command`) |
| `files` | caminho, hash SHA-256, tamanho e linguagem dos arquivos indexados |
| `chunks` | índice FTS5 com trechos de até 60 linhas (usado só para busca local) |
| `proposals` | pedido, origem/modelo, diff, arquivos, estado, commit base, branch, commit gerado |
| `test_runs` | comando, duração, código de saída e o final (redigido) da saída dos testes |
| `events` | trilha de auditoria: uma linha por alteração (proposta, aplicação, teste, commit, reversão, importação...) |

Commits feitos por `accept`/`go` incluem o rodapé `Gitruvian-Proposal: <id>`, ligando o histórico
do git ao registro local.

---

## Local dos dados e backup

- **Dados por repositório:** `<repo>/.gitruvian/gitruvian.db` (+ `gitruvian.log`). Defina
  `GITRUVIAN_DATA_DIR` para guardar fora dos repositórios (uma subpasta por repositório).
- **Configuração e segredos:** `.env` (veja a ordem de busca em `.env.example`). Nunca no banco.

Backup:

1. **Exportação aberta (recomendada):** `gitruvian export backup.json` gera um JSON legível com
   propostas (incluindo os diffs), execuções de teste e eventos. Não inclui o índice (é
   reconstruível com `gitruvian index`) nem segredos. `gitruvian import backup.json` restaura em
   qualquer repositório, de forma idempotente. Por segurança, `test_command` não é importado
   (executaria um comando) — reconfigure com `gitruvian config set`.
2. **Cópia do banco:** com o Gitruvian fechado, copie a pasta `.gitruvian/` inteira.
3. **Patches avulsos:** `gitruvian show <id> --raw > mudanca.patch` gera um patch padrão do git.

Para apagar tudo: remova `.gitruvian/` (e, se quiser, a linha `/.gitruvian/` de `.git/info/exclude`).

---

## Privacidade e segurança

- Arquivos sensíveis (`.env*` exceto `.env.example`, `*.pem`, `*.key`, `id_rsa*`, `*secret*`,
  `credentials*`, `.npmrc`, `*.tfstate`, bancos de dados, entre outros) **nunca** são indexados,
  enviados ao modelo nem podem ser alterados por um diff. Use `.gitruvianignore` (padrões estilo
  glob) para excluir mais coisas.
- Trechos que parecem segredos (chaves de API, tokens, chaves privadas, `password=...`) são
  substituídos por `[REDIGIDO]` antes de ir ao modelo e na saída de testes guardada no banco.
- Diffs vindos do modelo, de arquivos ou de importações são tratados como **não confiáveis**:
  limite de tamanho, sem caminhos absolutos ou com `..`, sem `.git/`/`.gitruvian/`, sem binários
  ou links simbólicos, e `git apply --check` antes de aplicar. Aplicar é recusado se os arquivos
  alvo tiverem alterações locais não commitadas.
- O log (`gitruvian.log`) contém apenas metadados (ids, contagens, códigos de saída, durações);
  conteúdo de arquivos, diffs, prompts e respostas nunca vão para o log. O banco, por ser o
  registro das alterações, guarda os diffs — trate-o como trata o próprio código.
- Chaves só são lidas do ambiente/.env, nunca aparecem em `repr`, logs ou `doctor` (que mostra
  apenas "presente"/"ausente").

## Acessibilidade

- Tudo é operável só pelo teclado; `gitruvian ui` oferece um menu numerado (digite o número e
  Enter; `0` ou Ctrl+C sai).
- Estados nunca dependem só de cor: toda mensagem tem rótulo textual (`OK:`, `ERRO:`, `AVISO:`,
  `INFO:`, "Próximo passo") e erros vêm com "Como resolver".
- Cores em negrito com bom contraste em fundos claros e escuros; `--no-color` ou a variável
  `NO_COLOR` desligam as cores. Fora de um terminal interativo, o indicador de carregamento vira
  uma linha de texto simples (amigável a leitores de tela e logs de CI).
- Todo comando e opção tem texto de ajuda (`--help`).

---

## Testes

```bash
scripts/test.sh        # ou scripts\test.ps1
```

- **Unitários:** modelo de dados e máquina de estados (`test_models.py`), segurança e validação
  de diffs, indexação/busca, configuração e provedores (inclui um servidor HTTP local real para o
  provedor `openai_compat`), importação/exportação com entradas maliciosas, e o fluxo principal
  (`test_workflow.py`).
- **Smoke test de ponta a ponta** (`test_smoke_e2e.py`): executa a CLI real como subprocesso num
  repositório temporário — `init → test (falha) → index → search → go --yes --commit → history →
  export → import` — usando `GITRUVIAN_PROVIDER=command` com um "modelo" determinístico de teste
  (`tests/fixtures/fake_model.py`). Ele prova que o ciclo funciona; **não** mede a qualidade de
  nenhum modelo real.

---

## Limitações (honestas)

Excluídas de propósito — vantagens de produtos pagos que este projeto **não** tenta igualar:

- **Qualidade de modelo de código de ponta:** a qualidade dos diffs depende inteiramente do modelo
  que você configurar. Modelos locais pequenos erram com frequência; o Gitruvian valida e testa,
  mas não corrige o raciocínio do modelo.
- **Infraestrutura de contexto amplo:** o contexto é uma busca por palavras-chave (BM25/FTS5) com
  limite de caracteres. Não há embeddings, grafo de dependências, nem análise semântica do projeto
  inteiro; mudanças que atravessam muitos arquivos tendem a falhar. Use `--file` para indicar
  arquivos.
- **Refinamento e baixa latência em todo o IDE:** é uma CLI. Não há integração com editor,
  autocompletar, diff lado a lado interativo ou respostas em streaming.

Outras limitações:

- A chamada real à API da Anthropic **não é coberta por testes automatizados** (exigiria chave e
  rede); o formato da requisição é testado, a integração só funciona com uma chave válida. O
  mesmo vale para um Ollama/LM Studio reais — os testes usam um servidor HTTP local de teste.
- Sem operações remotas (push/pull/fetch), sem resolução de conflitos, sem rebase interativo nem
  visualização gráfica rica do histórico (há `log --graph` em texto).
- Diffs são aplicados com `git apply` (tolerante a contagem de linhas e espaços). Diffs de
  arquivos binários, links simbólicos e renomeações com nomes especiais não são suportados.
- Trechos redigidos (segredos) enviados ao modelo podem fazer o diff não aplicar naquela região.
- Um usuário, uma máquina: não há sincronização, contas ou controle de acesso — por projeto.
- Não há alegações de conformidade (LGPD, SOC 2 etc.) nem de segurança além do que está descrito
  acima.
