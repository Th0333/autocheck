# Relatório para o time do ERP — kanban / recebimento via app

**Data:** 2026-07-05
**De:** equipe do app NotebookCheck (recebimento/checklist na máquina)
**Assunto:** status dos dois problemas reportados no kanban.

> **STATUS:** O **Problema 1 foi RESOLVIDO** na última atualização da API — o
> `GET /pedidos-compra?status=aberto` agora mantém o pedido na lista enquanto
> ele tiver máquina em qualquer etapa do kanban. Obrigado! O app foi ajustado e
> voltou a ler direto da lista (removemos a gambiarra local). **O Problema 2
> (técnico não aparece) continua em aberto** — detalhes abaixo.

---

## Problema 1 — Pedido some ao avançar a máquina — ✅ RESOLVIDO

**Era:** o pedido saía do `GET /pedidos-compra?status=aberto` assim que sua
última máquina deixava o `check_entrada`, sumindo do app (kanban e dropdown do
cadastro). Reproduzia com pedido de 1 máquina; com 2+ funcionava.

**Resolvido:** a API passou a manter o pedido na lista com máquina em **qualquer
etapa ativa** (sai só ao concluir/cancelar). Nada mais pendente aqui.

<details><summary>Descrição original (histórico)</summary>

### O pedido de compra some da API assim que a máquina sai do "check de entrada"

### Reprodução (com a prova da causa)

- **Pedido com 1 máquina em branco:** o técnico clica em **Assumir** → a máquina
  vai para `aguardando_tecnico`. A partir daí o pedido **desaparece** para o app.
  Ao abrir o cadastro daquela máquina, **o pedido não aparece na lista** e não dá
  para cadastrar. No site do ERP a máquina continua lá (em "aguardando técnico"),
  então o dado existe — some só na API que o app consome.
- **Pedido com 2 ou mais máquinas em branco:** **funciona normalmente.** Como
  ainda sobra máquina no check de entrada, o pedido continua na lista.

Essa diferença (1 máquina falha, 2+ funciona) mostra exatamente a causa.

### Causa

O app usa `GET /api/integracao/pedidos-compra?status=aberto` como **única fonte**
tanto do kanban quanto do dropdown de pedido do cadastro. Esse endpoint, por
definição, só devolve pedidos **"abertos com máquina em branco restante"** — na
prática, com máquina ainda em `check_entrada`.

Quando a **última** máquina em branco do pedido sai do check de entrada (ao
assumir ou avançar), o pedido deixa de casar com o filtro e o ERP **para de
devolvê-lo na lista**. Como o app só enxerga o que está nessa lista, o pedido e
suas máquinas somem — embora continuem existindo no ERP.

### Impacto no app

1. **Kanban:** máquinas em etapas depois do check de entrada somem do quadro.
   (O app tem uma mitigação local — memoriza pedidos já vistos — mas ela não
   cobre máquinas que este dispositivo nunca viu, ou criadas/avançadas direto na
   web.)
2. **Cadastro:** uma máquina já assumida (`aguardando_tecnico`) que ainda
   precisa ser cadastrada **não pode ser**, porque o pedido dela não aparece no
   dropdown. Este é o caso "com 1 máquina não dá certo".

### O que precisamos do ERP (uma destas opções, em ordem de preferência)

- **(a) Preferida —** `GET /api/integracao/pedidos-compra` passar a incluir
  pedidos com máquina em **qualquer etapa ativa** do kanban (não só
  `check_entrada`). Ex.: um parâmetro novo `?status=aberto&incluir=kanban`, ou
  aceitar `status=em_andamento`. Assim o pedido continua visível enquanto tiver
  máquina em processo.
- **(b) Alternativa robusta —** um endpoint dedicado do kanban, ex.
  `GET /api/integracao/kanban?limit=...`, devolvendo **todas as máquinas em
  etapas ativas** (check_entrada … aguardando_aprovacao, e opcionalmente
  concluído recente) já com pedido, etapa e `assumido_por`, numa chamada só.
- **(c) Mínimo para desbloquear o cadastro —** um `GET /api/integracao/pedidos-compra/{id}`
  (pedido único, **sem** o filtro de "máquina em branco restante"), para o app
  buscar o pedido específico que a máquina referencia, mesmo fora da lista.
  Resolve o cadastro, mas **não** resolve o kanban geral.

> Ideal seria (a) ou (b). Se der só para fazer o rápido primeiro, (c) já
> destrava o cadastro (o problema do "1 máquina não dá certo").

</details>

---

## Problema 2 — O técnico do "Assumir" não aparecia no site — ✅ RESOLVIDO

> **Resolvido (2026-07-07):** o `tecnico` enviado no `avancar` agora é gravado e
> aparece como `assumido_por`. O app já enviava e já lê esse campo — nada mais a
> fazer dos dois lados. Descrição original abaixo (histórico).

### Sintoma

O técnico clica em **Assumir** e digita o nome. No site, a máquina vai para
"aguardando técnico" **sem técnico atribuído**.

### O que o app envia

Na transição `check_entrada → aguardando_tecnico`, o app chama:

```
POST /api/integracao/kanban/avancar
{ "asset_id": "...", "etapa_atual": "check_entrada", "tecnico": "<nome digitado pelo técnico>" }
```

O `tecnico` vai como **texto livre** — o nome que a pessoa digitou na máquina
sob teste.

> **Nota:** a doc da API (§3.3) diz que `assumido_por` "agora vem em cada
> máquina do GET (nome do técnico do Assumir...)". Mas no teste em campo a
> máquina fica em "aguardando técnico" **sem técnico** no site. Ou seja: ou o
> `tecnico` do `avancar` não está sendo gravado como `assumido_por`, ou está
> sendo gravado mas a **tela** não o exibe. Vale verificar os dois.

### O que precisamos do ERP

1. **Confirmar** se o `tecnico` recebido no `avancar` está sendo **persistido** e
   devolvido como **`assumido_por`** no `GET /api/integracao/pedidos-compra/{id}/maquinas`.
   Hoje ele não aparece no site.
2. **Esclarecer o tipo esperado de `tecnico`:** texto livre (nome) ou uma
   **referência de usuário** do ERP (UUID / e-mail)?
   - Importante: quem roda o app na máquina sob teste **normalmente não tem
     login no ERP**. O app só tem o nome digitado. Se o campo exigir um usuário
     do ERP, não teremos de onde tirar.
   - **Sugestão:** aceitar texto livre e gravar como rótulo em `assumido_por`
     (o "assumido por" é mais um carimbo de quem pegou a máquina na bancada do
     que um vínculo de usuário).

---

## Problema 3 — Erro `record "v_os" is not assigned yet` ao cadastrar com "produção técnica" — 🔴 NOVO / BLOQUEIA

### Sintoma

Ao cadastrar a máquina (`POST /api/integracao/recebimentos`) com
`proximo_destino: "producao_tecnica"`, o ERP responde **500** com a mensagem
do banco:

```
record "v_os" is not assigned yet
```

A máquina **não é criada** (a transação faz rollback). Com
`proximo_destino: "aprovacao_direta"` o erro **não** acontece.

### Causa provável (é um bug de PL/pgSQL, no banco do ERP)

`v_os` é uma variável de **record** (aparenta ser a ordem de serviço /
diagnóstico) usada **antes de ser atribuída**. O padrão clássico:

```plpgsql
SELECT * INTO v_os FROM ... WHERE ...;   -- não encontrou nenhuma linha
-- ... e logo abaixo:
IF v_os.status = ... THEN   -- ERRO: "record v_os is not assigned yet"
```

Ou seja, um `SELECT ... INTO v_os` que voltou vazio (nenhuma OS encontrada) e o
código segue acessando `v_os.<campo>` sem checar `IF FOUND` / `IF v_os IS NULL`.

### Onde olhar

- O caminho **`producao_tecnica`** é o que manipula a ordem (§3.5: "ordem vai
  para `em_andamento`"). É aí que o `v_os` é mexido. Como com `aprovacao_direta`
  não dá erro, o problema está nessa ramificação.
- Vale checar também as transições do `avancar` que mexem na OS
  (`em_andamento→aguardando_componente` e
  `aguardando_componente→aguardando_aprovacao`, que "deriva componentes dos
  movimentos"), pelo mesmo motivo.

### Correção sugerida

Garantir que a OS da máquina exista/seja encontrada **antes** de referenciar
`v_os` (as máquinas em branco nascem "na fila de produção técnica", então a OS
deveria existir — vale conferir se o `SELECT ... INTO v_os` está com o filtro
certo), ou tratar o `NOT FOUND` explicitamente (`RAISE`/criar a OS).

> **Observação:** do lado do app, mudamos o destino padrão do cadastro para
> `producao_tecnica` justamente para casar com a §3.5 (máquina vai para a
> bancada). Foi isso que passou a expor este bug em todo cadastro. Enquanto não
> corrigido, podemos voltar o padrão para `aprovacao_direta` (cadastro funciona,
> mas a máquina entra em "aguardando aprovação" em vez de ir para a bancada).

---

## 4. Retroceder etapa no kanban — ✅ RESOLVIDO

O ERP implementou o `POST /api/integracao/kanban/retroceder` (espelho do avancar,
`tecnico` ignorado). O app já consome. Transições confirmadas:
`aguardando_tecnico→check_entrada`, `em_andamento→aguardando_tecnico`,
`aguardando_componente→em_andamento`, `aguardando_aprovacao→em_andamento`.
O app desabilita a seta ◀ no check de entrada e no concluído (sem etapa
anterior). Ao voltar para fila/check, o app limpa o técnico do cache local
(o ERP zera o `assumido_por`). `409` de corrida/etapa divergente é tratado.

### Sobre o "✕ remover do kanban"

O app também ganhou um **✕** no cartão para "remover do kanban". Hoje isso é um
**ocultamento local** (some do quadro só naquele computador; a máquina continua
no ERP). O ERP confirmou: **manter local por ora**; se um dia quiserem efeito no
servidor, o candidato é cancelar a ordem (fica para um próximo ciclo).

---

## Resumo do que pedimos

| # | O que | Status / Ação no ERP |
| --- | --- | --- |
| 1 | Pedido some ao avançar a máquina | ✅ **Resolvido** — pedido fica na lista em qualquer etapa ativa |
| 2 | Técnico não aparecia no site | ✅ **Resolvido** — `tecnico` do `avancar` gravado/exibido como `assumido_por` |
| 3 | `record "v_os" is not assigned yet` no cadastro com produção técnica | 🔴 **Bloqueia** — corrigir o `v_os` (record não atribuído) na função de recebimento/OS; guardar `IF FOUND` antes de usar |
| 4 | Retroceder etapa (seta ◀) | ✅ **Resolvido** — `POST /kanban/retroceder` implementado; app já consome |

Qualquer dúvida sobre o que o app envia/espera em cada chamada, é só falar que a
gente detalha.
