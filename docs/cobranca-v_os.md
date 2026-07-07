# Cobrança — bug `record "v_os" is not assigned yet` (cadastro via app)

**Para:** responsável pelo ERP (`estoque-erp-web` / banco Supabase)
**De:** equipe do app NotebookCheck
**Prioridade:** 🔴 ALTA — bloqueia o fluxo "correto" de cadastro
**Status:** aberto há 2 versões da API (não citado em `message(3)` nem `message(5)`)

---

## Resumo (1 parágrafo)

Ao cadastrar uma máquina pelo app com `proximo_destino: "producao_tecnica"` (o
destino que, segundo a própria doc §3.5, deveria mandar a máquina para a bancada
/ `em_andamento`), o `POST /api/integracao/recebimentos` responde **HTTP 500**
com a mensagem do banco **`record "v_os" is not assigned yet`**, e a máquina
**não é criada** (rollback). É um bug de PL/pgSQL numa função do ERP. Com
`proximo_destino: "aprovacao_direta"` **não** ocorre. Estamos usando "aprovação
direta" como paliativo, mas isso joga a máquina para "aguardando aprovação"
**antes de ser testada**, fora da ordem do kanban.

## Reprodução

1. Ter um pedido de compra aberto com máquina em branco.
2. `POST /api/integracao/recebimentos` com `proximo_destino: "producao_tecnica"`
   e **sem** alertas (condição/acessórios/config dentro do acordado).
3. Resultado: **500** `record "v_os" is not assigned yet`. Nenhuma máquina criada.

Trocar só o `proximo_destino` para `aprovacao_direta` no mesmo corpo → **funciona**.

## Causa (diagnóstico do lado do app)

`v_os` é uma variável de **record** (aparenta ser a ordem de serviço /
diagnóstico) **usada antes de ter sido atribuída**. Padrão clássico em PL/pgSQL:

```plpgsql
SELECT * INTO v_os FROM <ordens_servico> WHERE <filtro>;   -- não achou linha
-- ...
IF v_os.status = ... THEN        -- ERRO: "record v_os is not assigned yet"
```

Ou seja: um `SELECT ... INTO v_os` que voltou **vazio** e o código segue
acessando `v_os.<campo>` sem checar `IF FOUND` / `IF v_os IS NULL`.

## Onde olhar

- A ramificação **`producao_tecnica`** da função de recebimento — é a que
  movimenta a ordem para `em_andamento` (§3.5). É aí que o `v_os` é usado.
  Como `aprovacao_direta` não quebra, o problema está nessa ramificação.
- Vale conferir também as transições do `avancar` que mexem na OS
  (`em_andamento→aguardando_componente` e
  `aguardando_componente→aguardando_aprovacao`), pelo mesmo motivo — podem ter o
  mesmo `v_os` sem guarda.

## Correção sugerida

- Conferir se o `SELECT ... INTO v_os` está com o **filtro certo** (as máquinas
  em branco nascem "na fila de produção técnica", então a OS deveria existir — o
  mais provável é o filtro/So join não bater e voltar zero linha).
- Tratar o `NOT FOUND` explicitamente: `IF NOT FOUND THEN RAISE ... / criar a OS`
  antes de referenciar `v_os`.

## Impacto enquanto não corrigido

- O app está preso ao paliativo `aprovacao_direta`: cadastra, mas a máquina cai
  em **aguardando aprovação** em vez de ir para a **bancada (em_andamento)**.
- Assim que o `v_os` for corrigido, o app volta o padrão para `producao_tecnica`
  (é uma linha) e o fluxo do kanban fica correto: cadastrar → em_andamento.

## O que pedimos

1. Corrigir o `v_os` na função de recebimento (ramificação `producao_tecnica`).
2. Confirmar que, depois, `POST /recebimentos` com `proximo_destino:
   "producao_tecnica"` **sem alertas** devolve `status`/etapa coerente com
   `em_andamento` (§3.5) — para a gente reverter o paliativo com segurança.

Qualquer log/stack da função (nome da function + linha do `v_os`) ajuda a
fechar isso rápido.
