# Relatório — Kanban completo do app × API v2

> **STATUS (2026-07-04): CONTRATO FECHADO.** A API v2 (recebimento v2 +
> `message(2).txt`) implementou o kanban proposto aqui. O app foi ajustado ao
> contrato final. Divergências entre a proposta original deste relatório e o
> que o ERP decidiu:
>
> 1. **`aguardando_aprovacao → concluido` é RECUSADO com 409** (regra #15 — a
>    aprovação é humana, em `/aprovacoes`). Por isso o app **removeu o botão OK
>    da coluna "Aguardando aprovação"**; ela agora só exibe um aviso de que a
>    aprovação é feita no painel e o cartão avança sozinho no próximo refresh.
>    Isso substitui o pedido original de "OK para pular a aprovação".
> 2. **`assumido_por`** passou a vir no GET de máquinas — o app usa o valor do
>    servidor e cai no registro local (`assumidos.json`) só como fallback.
> 3. **Pós-cadastro** com `produção técnica` + sem alertas → `em_andamento`
>    (técnico na bancada). O app agora usa `producao_tecnica` como destino
>    padrão do cadastro para casar com esse fluxo.
> 4. `POST /kanban/avancar` com `{ asset_id, etapa_atual, tecnico? }` e as
>    transições `check_entrada→aguardando_tecnico` (Assumir),
>    `em_andamento→aguardando_componente`,
>    `aguardando_componente→aguardando_aprovacao` — **todas confirmadas**.
>
> O restante deste documento é o histórico da proposta (mantido para
> rastreabilidade).

---

## 0. ACHADOS DO TESTE EM CAMPO (2026-07-04) — precisam de ação no ERP

Ao testar o kanban com o ERP real, dois problemas apareceram. O primeiro tem
mitigação no app; ambos precisam de ajuste no servidor para funcionar direito.

### 0.1 Máquina some do quadro ao sair de "Check de entrada" — ✅ RESOLVIDO (2026-07-05)

**Resolvido pelo ERP:** o `GET /pedidos-compra?status=aberto` agora mantém o
pedido na lista enquanto tiver máquina em qualquer etapa do kanban (sai só ao
concluir/cancelar). O app voltou a ler direto da lista e a mitigação local
(`KanbanPedidosStore`) foi **removida** — ela faria pedidos concluídos
acumularem no quadro para sempre. Texto original abaixo (histórico).

**Sintoma (era):** técnico clica em **Assumir**; no site a máquina aparece em
"aguardando técnico", mas **no app ela some de todas as colunas**.

**Causa:** o app monta o quadro percorrendo os pedidos de
`GET /pedidos-compra?status=aberto` e, para cada um, chamando
`/{id}/maquinas`. Esse endpoint (pela sua própria definição) só lista pedidos
**"abertos com máquina em branco restante"** — na prática, com máquina ainda em
`check_entrada`. Quando a (única) máquina do pedido avança para
`aguardando_tecnico`, o **pedido sai da lista**, o app nunca chama `/maquinas`
dele, e a máquina desaparece do quadro (embora continue no ERP).

**Mitigação já no app:** o app agora **memoriza os pedidos já vistos**
(`kanban-pedidos.json`) e continua consultando `/maquinas` deles mesmo que
saiam da lista (esquece só quando `/maquinas` responde 404). Isso resolve o
caso do dia a dia neste dispositivo.

**Limite da mitigação / o que o ERP precisa prover:** uma máquina que nunca foi
vista por este app (criada/avançada direto na web, ou em outro pendrive) e cujo
pedido não está na lista de abertos **não aparece**. O ideal é um dos dois:

- **(a)** `GET /pedidos-compra` aceitar um modo que inclua pedidos com máquina
  em **qualquer etapa ativa** do kanban (não só `check_entrada`), ex.:
  `?status=aberto&incluir=kanban`; **ou**
- **(b)** um endpoint dedicado do kanban, ex.
  `GET /api/integracao/kanban?limit=...`, devolvendo **todas as máquinas em
  etapas ativas** (check_entrada … aguardando_aprovacao, e opcionalmente
  concluido recentes) já com pedido, etapa e `assumido_por` — o app troca a
  varredura por pedido por uma chamada só.

### 0.2 `tecnico` do Assumir não aparece no site (`assumido_por` vazio)

**Sintoma:** no site a máquina está em "aguardando técnico" **sem técnico
assumido**, mesmo o app tendo enviado o nome.

**O que o app envia:** `POST /kanban/avancar` com
`{ asset_id, etapa_atual, tecnico: "<nome digitado>" }` na transição
`check_entrada → aguardando_tecnico`. O `tecnico` vai como **texto livre** (o
nome que o técnico digitou).

**A esclarecer no ERP:**
- O `tecnico` do avancar está sendo **persistido** e devolvido como
  `assumido_por` no `GET /maquinas`? (Hoje não aparece.)
- `tecnico` deve ser **texto livre** (nome) ou o ERP espera uma **referência de
  usuário** (UUID/e-mail)? Se espera UUID, o app precisa saber de onde tirar —
  hoje ele só tem o nome digitado pelo técnico na máquina sob teste, que não
  tem login no ERP. Sugestão: aceitar texto livre e gravar como rótulo em
  `assumido_por`, já que quem roda o app normalmente não é usuário do ERP.

---

## 1. O fluxo implementado no app

```
┌─ Check de entrada ─┐   ┌─ Aguardando técnico ─┐   ┌─ Em andamento ─┐
│ botão: ASSUMIR     │──▶│ botão: OK            │──▶│ botão: OK      │──▶ …
│ (pergunta o nome   │   │ (abre o CADASTRO     │   │ ("terminei")   │
│  do técnico e      │   │  reivindicando a     │   │                │
│  avança a etapa)   │   │  máquina; ao concluir│   │                │
└────────────────────┘   │  vai p/ Em andamento)│   └────────────────┘
                         └──────────────────────┘
      ┌─ Aguardando componente ─┐   ┌─ Aguardando aprovação ─┐   ┌─ Concluído ─┐
 …──▶ │ botão: OK (pular)       │──▶│ botão: OK (pular)      │──▶│ (sem ações) │
      │ ⚠ "Se não for necessário│   │ ⚠ mesmo aviso          │   └─────────────┘
      │  clique em OK."         │   └────────────────────────┘
      └─────────────────────────┘
```

Tudo deve ficar **sincronizado com o ERP** — cada ação acima dispara uma
chamada; o quadro é recarregado do servidor em seguida.

## 2. O que a API v2 já cobre (nenhuma mudança)

- `GET /pedidos-compra` e `GET /pedidos-compra/{id}/maquinas` — fonte do quadro.
- `POST /recebimentos` com `asset_id` — o OK do "Aguardando técnico" abre o
  wizard de cadastro reivindicando a máquina.
- `POST /autocheck` — continua existindo; ver §3.6.

## 3. O que FALTA na API

### 3.1 Etapas novas no `etapa_kanban`

O GET `/pedidos-compra/{id}/maquinas` precisa devolver também (se ainda não
existirem na máquina de estados):

| etapa_kanban | Coluna no app |
| --- | --- |
| `check_entrada` | Check de entrada *(já existe)* |
| `aguardando_tecnico` | Aguardando técnico *(já existe)* |
| `em_andamento` | Em andamento *(já existe)* |
| **`aguardando_componente`** | Aguardando componente **(nova?)** |
| `aguardando_aprovacao` | Aguardando aprovação |
| **`concluido`** | Concluído **(nova? — máquinas concluídas precisam continuar aparecendo no GET enquanto o pedido estiver aberto)** |

O app mostra as 6 colunas sempre; etapas desconhecidas viram coluna extra no
fim (não quebra).

### 3.2 `POST /api/integracao/kanban/avancar` — endpoint novo (o app JÁ chama)

Move a máquina para a **próxima** etapa do fluxo. Headers iguais aos demais
POSTs (`Authorization` + `Idempotency-Key` UUID). Corpo enviado pelo app:

```json
{
  "asset_id": "uuid",
  "etapa_atual": "check_entrada",   // etapa que o app está vendo (proteção contra corrida)
  "tecnico": "Fulano"               // só no Assumir; null nas demais
}
```

Transições que o app dispara:

| De (etapa_atual) | Para | Gatilho no app |
| --- | --- | --- |
| `check_entrada` | `aguardando_tecnico` | **Assumir** (leva `tecnico`) |
| `em_andamento` | `aguardando_componente` | OK — "terminei" |
| `aguardando_componente` | `aguardando_aprovacao` | OK — pular ("se não for necessário") |
| `aguardando_aprovacao` | `concluido` | OK — pular |

Resposta esperada (`201`, ou `200` no replay idempotente):

```json
{ "ok": true, "asset_id": "uuid", "etapa_anterior": "check_entrada", "etapa_nova": "aguardando_tecnico" }
```

Validações sugeridas: `409` quando `etapa_atual` não bate com a etapa corrente
(outro técnico moveu antes — o app recarrega e mostra a mensagem); `404` asset
inexistente na org; `422` corpo inválido; histórico de transição (#13) + log de
integração (#18). Permissão: `inbound:update_status` (já existe) ou uma nova.

> **⚠ Decisão de negócio (regra #15):** a transição
> `aguardando_aprovacao → concluido` via API **conclui sem aprovação humana**.
> Vocês decidem no ERP: (a) permitir só com uma permissão própria (ex.:
> `inbound:skip_approval`); (b) recusar com `409` quando existir approval
> pendente com alertas; (c) registrar como "aprovação automática via app" no
> histórico. O app só envia o pedido — a regra fica no servidor.

### 3.3 Técnico sincronizado (`assumido_por`)

Hoje o "Assumir" grava o técnico **localmente** no app (arquivo
`assumidos.json` do pendrive) — não aparece em outro computador. Para
sincronizar de verdade:

- O `tecnico` enviado no avancar do Assumir deve ser **gravado na
  máquina/ordem** no ERP;
- O GET `/pedidos-compra/{id}/maquinas` deve devolver o campo novo
  **`assumido_por`** (string | null) em cada máquina.

O app já mostra "👤 nome" no card; quando o campo vier do GET, ele passa a
valer para todos os dispositivos (o local vira fallback).

### 3.4 Cadastro a partir de "Aguardando técnico"

No fluxo novo o Assumir move a máquina para `aguardando_tecnico` **antes** do
cadastro. Hoje `pode_check_entrada=true` (e a reivindicação por `asset_id` no
POST /recebimentos) valem para máquinas em branco em `check_entrada`. É
preciso que a máquina em `aguardando_tecnico` **continue reivindicável**:
`pode_check_entrada` permanece `true` até o cadastro acontecer, e o POST
/recebimentos aceita `asset_id` de máquina nessa etapa.

### 3.5 Pós-cadastro → "Em andamento"

No fluxo novo, ao concluir o cadastro a máquina deve ir para **`em_andamento`**
(o técnico está com ela na bancada). Hoje a v2 manda para `aguardando_teste`
(fila) sem alertas, ou `aguardando_aprovacao` com alertas. Sugestão: cadastro
reivindicado por `asset_id` já assumido → `em_andamento` direto; os alertas
continuam indo para a approval (que o humano vê depois, na etapa de
aprovação). A decisão final é de vocês — só me digam qual etapa a máquina
ocupa depois do POST /recebimentos, que o app apenas reflete o GET.

### 3.6 E o `POST /autocheck`?

O app **parou de chamá-lo pelo kanban** (o OK de "Em andamento" agora usa o
avancar, sem perguntar resultado). O autocheck continua reservado para a
integração futura do **checklist completo** (o app roda os testes e reporta
`aprovado/reprovado` automaticamente com as especificações). Duas opções para
vocês: (a) manter os dois — avancar move etapa, autocheck conclui ordem com
resultado; ou (b) exigir autocheck antes de permitir `em_andamento →
aguardando_componente`. Me digam a regra que eu ajusto o app.

## 4. Comportamento do app enquanto a API não muda

- Toda chamada ao `/kanban/avancar` que responder **404** mostra: "O ERP ainda
  não aceita avanço de etapa (endpoint pendente) — registro mantido só no app".
- O **Assumir** já funciona localmente (nome do técnico + preenchimento
  automático no checklist); só a movimentação de coluna fica pendente.
- **Plano B**: no Check de entrada, uma máquina já assumida ganha também o
  botão OK (abre o cadastro direto) — assim o fluxo não trava enquanto o
  endpoint não existe. Quando o avancar entrar no ar, esse plano B deixa de
  aparecer naturalmente (a máquina muda de coluna).

## 5. Checklist — TUDO RESOLVIDO na v2 (message(2).txt)

- [x] Etapas `aguardando_componente` e `concluido` no GET (máquinas seguem no
      GET enquanto o pedido existir).
- [x] `POST /kanban/avancar` `{ asset_id, etapa_atual, tecnico? }`, permissão
      `inbound:update_status`, resposta `{ ok, asset_id, etapa_anterior, etapa_nova }`.
- [x] Pulo da aprovação: **recusado com 409** (regra #15) — app removeu o OK
      da coluna de aprovação; aprovação segue humana em /aprovacoes.
- [x] `assumido_por` no GET de máquinas — app usa server, local é fallback.
- [x] Reivindicação em `aguardando_tecnico` (`pode_check_entrada` continua true).
- [x] Pós-cadastro sem alertas + produção técnica → `em_andamento` (app usa
      `producao_tecnica` como destino padrão).
- [x] Autocheck e avancar convivem; autocheck opcional, sem obrigatoriedade
      antes do avanço (reservado à integração futura do checklist completo).
