# Relatório — Mudanças na API do ERP para o cadastro baseado em Pedido de Compra

> **STATUS (2026-07-03): API v2 IMPLEMENTADA NO ERP** (migration
> `20260703140000_inbound_api_pedido_v2.sql`) e o app já foi ajustado ao
> contrato final. Decisões que divergem deste relatório, vindas do doc v2:
>
> 1. **Modelo interno**: o pedido de compra **já cria as N máquinas em branco**
>    (com `codigo_interno` e NTB gerados) na produção técnica; o POST do app
>    **reivindica e preenche** a próxima máquina em branco — transparente para
>    o app, contrato mantido.
> 2. **NTB (§5 ajustado pelo negócio)**: sem piso de 100000/6–8 dígitos — a
>    sequência continua a numeração natural da organização (ex.: 11799→11800).
>    O app trata como **string de dígitos de comprimento variável**.
> 3. **`numero` do pedido**: formato `PC-<sequencial>` (ex.: `PC-12`).
> 4. **Resposta do POST** ganhou também `proximo_destino`, `pedido_compra_id`
>    e `pedido_numero` (o app usa os dois últimos no arquivo de identidade).
> 5. **API de status (§8): ainda pendente** — proposta do ERP: o
>    `checklist_concluido` executa/conclui a ordem de serviço de diagnóstico
>    que já nasce com cada máquina do pedido. Contrato a fechar.
>
> Permissão nova exigida no GET de pedidos: `inbound:read_orders` (relogar o
> usuário de serviço após a migration — o app já força login a cada conexão).
>
> Referência da API atual: doc v2 (`message.txt` / recebimento-via-app v2);
> v1 histórica: `recebimento-via-app.md`.

---

## 1. Resumo executivo

O cadastramento deixa de ser "avulso" e passa a ser **sempre vinculado a um
pedido de compra** criado no ERP. Consequências na API:

| # | Mudança | Tipo |
| --- | --- | --- |
| 1 | Novo endpoint `GET /api/integracao/pedidos-compra` (pedidos abertos + requisitos) | **Novo** |
| 2 | `POST /api/integracao/recebimentos` passa a exigir `pedido_compra_id` | **Alteração** |
| 3 | Servidor **gera o NTB** (6–8 dígitos, sequência crescente, único) e devolve na resposta | **Novo comportamento** |
| 4 | Campo `patrimonio` removido do recebimento | **Remoção** |
| 5 | Campos `ntb`, `fornecedor_id`, `documento_entrada`, `valor_aquisicao`, `custos_adicionais` **não são mais enviados pelo app** (derivam do pedido) | **Remoção/derivação** |
| 6 | Novos campos de sinalização: `condicao_abaixo_minimo` e `acessorios_faltantes` | **Novo** |
| 7 | Pedido de compra carrega requisitos: `condicao_minima` + `acessorios_obrigatorios` | **Novo (modelo de dados)** |
| 8 | Endpoint de **status da máquina** para o checklist atualizar o ERP | **Novo (contrato a confirmar — ver §8)** |
| 9 | `serial_number` deixa de ter unicidade obrigatória (máquinas com serial repetido existem) | **Alteração de validação** |

Endpoints que o app **deixou de usar** no cadastro (podem permanecer para
outros clientes): `GET/POST /api/integracao/fornecedores`. Continuam usados:
`GET /api/integracao/marcas` e `GET /api/integracao/localizacoes`.

---

## 2. Fluxo novo (visão geral)

```
Compras cria o PEDIDO DE COMPRA no ERP
  (fornecedor, documento, qtd, valor, condição mínima, acessórios obrigatórios)
                       │
App no notebook ──GET──▶ /api/integracao/pedidos-compra   (lista pedidos abertos)
    técnico seleciona o pedido no wizard
                       │
App no notebook ──POST─▶ /api/integracao/recebimentos
    { pedido_compra_id, modelo, linha, serial, condição, acessórios… }
                       │  (valida JWT + Zod, idempotente)
                       ▼
    fn_create_inbound_via_api (atômico):
      • valida pedido aberto e com saldo (recebidas < total)
      • GERA O NTB  (sequência crescente, 6–8 dígitos, único na org)
      • cria asset  ── vincula pedido_compra_id ── status aguardando_aprovacao
      • cria asset_specifications (opcional)
      • cria approval (entrada_estoque, PENDENTE) com os alertas de requisito
      • incrementa quantidade_recebida do pedido
                       │
        resposta ──▶ { ok, asset_id, codigo_interno, ntb, approval_id, status }
                       │
    app grava na máquina: C:\ProgramData\Notelet\machine-identity.json
      { serial, ntb, asset_id, modelo, linha, pedido }  ◀── o checklist lê depois
                       │
    humano aprova em /aprovacoes  ──▶  status_comercial = disponivel
                       │
    checklist principal roda na máquina ──▶ atualiza status no ERP (ver §8)
```

Regras inegociáveis preservadas: **#15** (entrada oficial continua exigindo
aprovação humana — a API só abre a solicitação), **#19** (idempotência por
`Idempotency-Key`), **#18** (log em `inbound_api_events`), **#6** (custo nunca
em claro no log — e agora o app nem envia custo), **#1/#13** (estados separados
e histórico de transição).

---

## 3. `GET /api/integracao/pedidos-compra` (novo)

Lista pedidos de compra **abertos** (aguardando recebimento de máquinas) da
organização do token.

- **Auth:** Bearer JWT da conta de serviço (igual aos demais GETs).
- **Permissão sugerida:** `inbound:read_refs` (ou nova `inbound:read_orders`,
  adicionada ao papel `integracao_recebimento`).

### Query string

| Param | Default | Descrição |
| --- | --- | --- |
| `status` | `aberto` | Filtra por situação do pedido (`aberto` = ainda recebe máquinas). |
| `q` | — | Busca por número do pedido ou nome do fornecedor (contém, case-insensitive). |
| `limit` | 200 | 1..1000. |

> O app chama exatamente: `GET /api/integracao/pedidos-compra?status=aberto&limit=200`.

### Resposta `200`

```json
{
  "items": [
    {
      "id": "uuid",
      "numero": "PC-2026-0012",
      "fornecedor_id": "uuid",
      "fornecedor_nome": "Mega Distribuidora TI",
      "documento_entrada": "NF 12345",
      "data_pedido": "2026-06-20",
      "modelo_previsto": "ThinkPad T480",
      "brand_id": "uuid",
      "marca_nome": "Lenovo",
      "condicao_minima": "boa",
      "acessorios_obrigatorios": ["Carregador original", "Cabo de força"],
      "quantidade_total": 10,
      "quantidade_recebida": 3,
      "observacoes": "Lote corporativo, verificar BIOS"
    }
  ]
}
```

| Campo | Tipo | Obrigatório na resposta | Uso no app |
| --- | --- | --- | --- |
| `id` | uuid | **sim** | Enviado no recebimento (`pedido_compra_id`). |
| `numero` | string | **sim** | Exibido no dropdown e gravado no arquivo de identidade. |
| `fornecedor_id` / `fornecedor_nome` | uuid / string | não | Exibição (o vínculo real é feito no servidor). |
| `documento_entrada` | string | não | Exibição/revisão. |
| `data_pedido` | ISO date | não | Exibição. |
| `modelo_previsto` | string | não | Pré-preenche o campo Modelo se o WMI não trouxer nada. |
| `brand_id` / `marca_nome` | uuid / string | não | Pré-seleciona a marca no dropdown. |
| `condicao_minima` | enum `excelente·boa·regular·ruim·sucata` | não | **Requisito**: se o técnico marcar condição pior, o app sinaliza (aviso amarelo). |
| `acessorios_obrigatorios` | string[] | não (default `[]`) | **Requisito**: vira checklist de checkboxes na aba Condição. |
| `quantidade_total` / `quantidade_recebida` | int | não | Progresso "3 de 10 recebidas" no wizard. |
| `observacoes` | string | não | Exibição. |

### Erros

`401` (token) · `403` (sem permissão) · demais idem aos GETs atuais.

> **Importante:** enquanto este endpoint não existir, o app mostra
> "O ERP ainda não expõe pedidos de compra (endpoint pendente)" e **bloqueia o
> cadastro** (pedido é obrigatório). Ou seja: este endpoint precisa entrar no ar
> junto (ou antes) da v2 do recebimento.

---

## 4. `POST /api/integracao/recebimentos` (v2 — alterado)

Mesma rota, mesmos headers (`Authorization`, `Idempotency-Key` UUID,
`Content-Type`), mesma atomicidade e idempotência. Muda o corpo e o
comportamento.

### 4.1 Corpo que o app envia agora (exemplo real)

```json
{
  "pedido_compra_id": "8b1f74e0-…",
  "modelo": "ThinkPad T480",
  "linha": "ThinkPad",
  "brand_id": "uuid",
  "serial_number": "PF1ABCDE",
  "condicao_estetica": "regular",
  "condicao_abaixo_minimo": true,
  "defeitos_aparentes": ["Tela com risco"],
  "acessorios_incluidos": ["Carregador original", "Mouse (extra)"],
  "acessorios_faltantes": ["Cabo de força"],
  "observacoes": "…",
  "localizacao_inicial_id": "uuid",
  "proximo_destino": "aprovacao_direta",
  "especificacoes": { "processador": "i5-8350U", "ram_gb": 16, "…": "…" }
}
```

### 4.2 Tabela de campos (v2)

| Campo | Tipo | Obrigatório | Mudança vs v1 | Observação |
| --- | --- | --- | --- | --- |
| `pedido_compra_id` | uuid | **sim** | **NOVO** | Pedido precisa estar `aberto` e com saldo (`recebida < total`). |
| `modelo` | string 2–200 | **sim** | igual | — |
| `linha` | string ≤60 | não | igual | Agora sugerida/aprendida no app. |
| `brand_id` | uuid | não | igual | — |
| `serial_number` | string ≤60 | não | **unicidade removida** | Ver §7. |
| `condicao_estetica` | enum | não | igual | — |
| `condicao_abaixo_minimo` | boolean | não | **NOVO** | `true` = técnico confirmou condição pior que a mínima do pedido. Só vem quando o pedido tem `condicao_minima`. |
| `defeitos_aparentes` | string[] ≤50×200 | não | igual | — |
| `acessorios_incluidos` | string[] ≤50×200 | não | igual (semântica nova) | Itens do checklist marcados + extras digitados. |
| `acessorios_faltantes` | string[] | não | **NOVO** | Acessórios obrigatórios do pedido que **não** foram recebidos. |
| `observacoes` | string ≤2000 | não | igual | — |
| `localizacao_inicial_id` | uuid | não | igual | — |
| `proximo_destino` | enum | não (def. `aprovacao_direta`) | igual | — |
| `especificacoes` | objeto | não | igual | Mesma tabela 4.1 do doc antigo. |
| ~~`ntb`~~ | — | — | **REMOVIDO do request** | Gerado pelo servidor (§5). |
| ~~`patrimonio`~~ | — | — | **REMOVIDO** | Campo extinto no fluxo de cadastro. |
| ~~`fornecedor_id`~~ | — | — | **REMOVIDO** | Deriva de `pedido_compra.fornecedor_id`. |
| ~~`documento_entrada`~~ | — | — | **REMOVIDO** | Deriva do pedido. |
| ~~`valor_aquisicao`~~ / ~~`custos_adicionais`~~ | — | — | **REMOVIDOS** | Derivam do pedido (ex.: `valor_unitario`). Continua regra #6: custo restrito, nunca no log. |

### 4.3 Validações novas no servidor

1. `pedido_compra_id` obrigatório, existente na org do token, `status = aberto`.
2. Saldo: `quantidade_recebida < quantidade_total` → senão `409` ("pedido já
   totalmente recebido") ou `422`, a definir.
3. Se `acessorios_faltantes` ≠ vazio ou `condicao_abaixo_minimo = true`:
   **não bloquear** — criar a aprovação normalmente e **destacar os alertas no
   item em `/aprovacoes`** (a decisão de aceitar fora do requisito é humana,
   coerente com a regra #15). Sugestão: gravar num jsonb `alertas_recebimento`
   da approval e exibir badge "⚠ fora do requisito".
4. Na transação: incrementar `quantidade_recebida` do pedido. No caso
   idempotente (mesma `Idempotency-Key`), **não** incrementar de novo.
5. Vincular `assets.pedido_compra_id` para rastreio.

### 4.4 Resposta (alterada)

```json
// 201 Created (novo)  |  200 OK (idempotent: true)
{
  "ok": true,
  "idempotent": false,
  "asset_id": "uuid",
  "codigo_interno": "NTL-000123",
  "ntb": "100234",            // ◀── NOVO: NTB gerado pelo servidor
  "approval_id": "uuid",
  "status": "aguardando_aprovacao"
}
```

O campo **`ntb` é o único acréscimo obrigatório na resposta**. No caso
idempotente, devolver o mesmo `ntb` da criação original.

### 4.5 Erros (v2)

| HTTP | Causa |
| --- | --- |
| `400` | `Idempotency-Key` ausente/não-UUID, JSON inválido |
| `401` | token inválido/expirado |
| `403` | sem permissão `inbound:create_via_api` |
| `422` | corpo reprovado no Zod (inclui `pedido_compra_id` ausente) + `issues[]` |
| `409` | pedido fechado/sem saldo · corrida na geração do NTB (retry interno recomendado) |
| `404` | `pedido_compra_id` inexistente na org |
| `500` | erro interno |

> O app mostra a mensagem de `error` diretamente ao técnico — mensagens em
> português e específicas ajudam ("Pedido PC-2026-0012 já recebeu as 10
> máquinas previstas.").

---

## 5. Geração do NTB no servidor (regras)

Requisitos definidos pelo negócio:

1. **6 a 8 dígitos numéricos** (string de dígitos, sem prefixo no banco).
2. **Não pode existir no banco** (único por organização).
3. **Ordem crescente** de cadastro (cada máquina nova recebe um número maior
   que o anterior).

### Implementação sugerida (Postgres/Supabase)

```sql
-- Sequência por organização, começando em 100000 (primeiro valor de 6 dígitos)
create table if not exists public.ntb_counters (
  organization_id uuid primary key references organizations(id),
  last_ntb        bigint not null default 99999
);

-- Dentro de fn_create_inbound_via_api (mesma transação do asset):
--   1. upsert do contador da org
--   2. UPDATE ... SET last_ntb = last_ntb + 1 RETURNING last_ntb  (lock de linha
--      garante atomicidade sob concorrência; nunca repete nem volta atrás)
--   3. Se last_ntb > 99999999 (8 dígitos estourados) → erro 500 explícito
--      (na prática inalcançável: 99.9 milhões de máquinas)
update public.ntb_counters
   set last_ntb = last_ntb + 1
 where organization_id = :org
returning last_ntb::text as ntb;
```

- Gravar em `assets.ntb` com **constraint `UNIQUE (organization_id, ntb)`** —
  proteção extra caso existam escritas fora da função.
- **Migração dos NTBs existentes:** inicializar `last_ntb` com
  `GREATEST(99999, max(ntb::bigint))` dos assets atuais cujo `ntb` seja
  numérico. NTBs antigos no formato "NTB123" podem permanecer como estão
  (históricos) — a sequência nova nunca colide porque começa acima de 100000 e
  a unicidade é validada.
- O campo `ntb` **deixa de ser aceito no request**; se vier, ignorar (ou `422`,
  a gosto — o app não envia).

### Exibição no app

O ERP armazena e devolve só os dígitos (ex.: `100234`). O app exibe e grava
localmente no formato canônico do checklist, `NTB100234` (normalização
`NtbCode.Normalize` já existente). Se preferirem que o ERP devolva já com
prefixo, o app também funciona — a normalização é idempotente.

---

## 6. Requisitos do pedido: condição mínima e acessórios obrigatórios

Comportamento implementado no app (para o ERP espelhar na aprovação):

- **Condição estética mínima** (`condicao_minima` do pedido): a escala é
  `excelente > boa > regular > ruim > sucata`. Se o técnico marcar condição
  **pior** que a mínima, o app exibe aviso amarelo na aba Condição e na
  Revisão, e envia `condicao_abaixo_minimo: true`. **Não bloqueia** o envio.
- **Acessórios obrigatórios** (`acessorios_obrigatorios` do pedido): viram um
  **checklist de checkboxes** (substituiu o campo de texto livre). Cada item
  desmarcado gera aviso e entra em `acessorios_faltantes`. Acessórios extras
  (não previstos) continuam possíveis num campo de texto separado e entram em
  `acessorios_incluidos`.
- No ERP, esses dois sinais devem ficar **visíveis para quem aprova** em
  `/aprovacoes` (badge/alerta no item), pois a decisão de aceitar fora do
  requisito é do aprovador (regra #15).

---

## 7. Número de série repetido

Existem lotes com máquinas de **mesmo serial** (placas trocadas, OEM genérico).
Por isso:

- O app agora permite **editar** o serial lido da máquina (para diferenciar,
  ex.: sufixo "-2").
- O servidor deve **remover a unicidade dura** de `serial_number` (o `409` de
  conflito de serial deixa de existir) — ou trocá-la por aviso não-bloqueante
  na aprovação. `patrimonio` saiu do fluxo, então a unicidade dele é
  irrelevante para o app.
- A identidade forte da máquina passa a ser o **NTB** (único e gerado pelo
  servidor) + `asset_id`.

---

## 8. Status da máquina × checklist (contrato a confirmar)

O ERP mantém o status de cada máquina; o cadastro e o checklist devem
movimentá-lo. O app **já grava na máquina** (em
`C:\ProgramData\Notelet\machine-identity.json`) tudo que precisa para isso:

```json
{
  "serial": "PF1ABCDE",
  "ntb": "NTB100234",
  "asset_id": "uuid",            // ◀── chave para atualizar status no ERP
  "codigo_interno": "NTL-000123",
  "modelo": "ThinkPad T480",
  "linha": "ThinkPad",
  "marca": "Lenovo",
  "pedido_compra_id": "uuid",
  "pedido_compra_numero": "PC-2026-0012",
  "cadastrado_em_utc": "2026-07-03T14:22:00Z"
}
```

O checklist principal lê esse arquivo (prefill do NTB já implementado) e, quando
a API de status existir, usará o `asset_id` para reportar. **Proposta de
contrato** (ajusto quando você mandar a API nova):

```
PATCH /api/integracao/maquinas/{asset_id}/status
Authorization: Bearer <token>   ·   Idempotency-Key: <uuid>

{ "evento": "checklist_concluido",          // ou "checklist_iniciado"
  "resultado": "aprovado" | "aprovado_com_ressalvas" | "reprovado",
  "ntb": "100234",                          // redundância p/ conferência
  "relatorio_url": "https://notebook-…/reports/…",  // opcional
  "resumo": { "testes_ok": 12, "testes_falha": 1 } } // opcional
```

Transições sugeridas no ERP (nomes dos status conforme os que existirem aí):

| Evento | Status operacional sugerido |
| --- | --- |
| Recebimento via app (POST recebimentos) | `aguardando_aprovacao` (já é assim) |
| Aprovação em /aprovacoes | `disponivel` / em estoque (já é assim) |
| `checklist_iniciado` | `em_teste` |
| `checklist_concluido` + `aprovado` | `testado_aprovado` (ou volta a `disponivel` com selo) |
| `checklist_concluido` + `reprovado` | `producao_tecnica` / `quarentena` |

Requisitos: idempotência por `Idempotency-Key`, log de evento (padrão #18),
histórico em `asset_status_history` (#13), permissão nova
`inbound:update_status` no papel `integracao_recebimento`.

---

## 9. Modelo de dados sugerido no ERP (resumo das migrations)

```sql
-- 1. Pedidos de compra (se ainda não existir tabela equivalente)
create table public.purchase_orders (
  id                        uuid primary key default gen_random_uuid(),
  organization_id           uuid not null references organizations(id),
  numero                    text not null,                -- "PC-2026-0012"
  status                    text not null default 'aberto', -- aberto·fechado·cancelado
  fornecedor_id             uuid references contacts(id),
  documento_entrada         text,
  data_pedido               date,
  modelo_previsto           text,
  brand_id                  uuid references brands(id),
  condicao_minima           text,                          -- enum das condições
  acessorios_obrigatorios   jsonb not null default '[]',   -- ["Carregador", …]
  quantidade_total          int,
  quantidade_recebida       int not null default 0,
  valor_unitario            numeric,                       -- custo (#6: restrito)
  observacoes               text,
  unique (organization_id, numero)
);

-- 2. Vínculo e NTB no asset
alter table public.assets
  add column pedido_compra_id uuid references purchase_orders(id);
-- ntb: garantir UNIQUE (organization_id, ntb)

-- 3. Contador do NTB (ver §5)
-- 4. Alertas de requisito na approval (jsonb) — condicao_abaixo_minimo,
--    acessorios_faltantes — exibidos em /aprovacoes
-- 5. Permissões: inbound:read_orders (GET pedidos), inbound:update_status (§8)
--    adicionadas ao papel integracao_recebimento
```

---

## 10. O que o app já implementa (deste lado, pronto)

1. **Wizard reduzido a 4 etapas** (Identificação · Condição · Destino ·
   Revisão) — a antiga etapa "Origem" (fornecedor/documento/valores) foi
   removida; a **seleção do pedido de compra** abre a etapa de Identificação e
   é obrigatória.
2. **Patrimônio removido** da UI e do payload.
3. **NTB sem digitação**: o campo sumiu; o app informa que o NTB é gerado no
   cadastro, exibe o número devolvido pelo servidor em destaque na tela de
   sucesso e o grava localmente (`serial-ntb.json` + arquivo de identidade).
4. **Linha automática com aprendizado**: sugere a partir do
   fabricante/modelo WMI (heurística) ou do que já foi aprendido
   (`linha-map.json`); o técnico confirma/edita; ao cadastrar, o valor é
   aprendido para o modelo exato **e** para a família (ex.: aprender em
   "ThinkPad T480" já sugere para "ThinkPad X1").
5. **Serial editável** (com aviso explicando o porquê) — ainda é lido
   automaticamente da máquina.
6. **Checklist de acessórios obrigatórios** do pedido com aviso de faltantes +
   campo separado para extras; **aviso de condição abaixo do mínimo**. Ambos
   sinalizam sem bloquear e seguem no payload (`acessorios_faltantes`,
   `condicao_abaixo_minimo`).
7. **Arquivo de identidade da máquina** gravado em
   `C:\ProgramData\Notelet\machine-identity.json` (fallback: pasta do app), com
   serial, NTB, `asset_id`, modelo, linha, marca e pedido — o checklist
   principal já o lê para preencher o NTB automaticamente.
8. **Falha suave**: se `GET /pedidos-compra` responder 404 (API antiga), o app
   avisa que o endpoint está pendente e bloqueia o cadastro sem quebrar.

## 11. Pendências que dependem da API nova

- [ ] Confirmar **nomes/formato** dos campos do `GET /pedidos-compra` (§3).
- [ ] Confirmar acréscimo do **`ntb` na resposta** do POST (§4.4).
- [ ] Definir a **API de status** (§8) — o app passará a chamá-la ao concluir o
      checklist (e opcionalmente ao iniciar).
- [ ] Definir se `valor_unitario` do pedido vira `valor_aquisicao` do asset
      automaticamente (recomendado; mantém #6 sem tráfego de custo pelo app).
- [ ] Migração dos NTBs legados no formato "NTB123" (ver §5 — sem colisão).

Quando a documentação da API nova chegar, os pontos de ajuste no app estão
concentrados em `src/NotebookCheck/Infrastructure/Erp/ErpModels.cs` (nomes dos
campos JSON) e `ErpClient.cs` (rotas) — o resto do fluxo já está pronto.
