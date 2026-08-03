# Design — Microfone: gravar, encerrar, ouvir e enviar pro ERP

- **Data:** 2026-08-03
- **Status:** aprovado (brainstorm com o dono do produto)
- **Repos afetados:** `C:\notebook check` (app WPF) **e** `C:\site estoque` (ERP Notelet + Supabase)
- **Arquivos-âncora:** `src/NotebookCheck/Presentation/Views/MicTestControl.xaml{,.cs}`,
  `src/NotebookCheck/Presentation/Views/TestActionWindow.xaml{,.cs}`,
  `src/NotebookCheck/Infrastructure/Erp/ErpClient.cs`

## 1. Contexto e objetivo

Hoje o teste de microfone abre o modal, começa a captar sozinho e mostra só um VU
meter ao vivo com o percentual de captação. O técnico olha a barra reagir e marca
o resultado. O áudio até é gravado num buffer WAV, mas ele **só pode ser ouvido
depois de salvar**, por um botão "Reproduzir gravação" na tela principal — longe
do momento em que a decisão é tomada.

O dono pediu o ciclo completo dentro do modal:

> "queria tambem um botao de play que ai comeca o teste e depois um botao de
> encerrar e um botao de play depois pra ouvir o som que gravou pra ver como esta
> e ai depois so perguntar o resultado"

E, durante o brainstorm, acrescentou um requisito que sai do app:

> "apartir do momento que ele comecar a gravar o audio, quero que depois tenha um
> botao de enviar pro erp pra o audio do tecnico ficar armazenado junto do check
> da maquina"

Objetivo: o técnico **grava, ouve e julga** sem sair do modal, e pode **anexar a
gravação ao check da máquina no ERP** quando ela valer a pena guardar.

## 2. Decisões tomadas

| Pergunta | Decisão | Porquê |
|---|---|---|
| Medidor ao vivo antes de gravar | **Continua ligado ao abrir** | O técnico confirma na hora que há captação; é o diagnóstico rápido que já existe hoje. Gravar vira um passo a mais, não um pré-requisito. |
| Limite de gravação | **30 s, com contador visível** | Trava de segurança contra esquecer o botão. O técnico começa e para quando quiser. |
| Envio pro ERP | **Botão manual** | O técnico decide o que vale guardar. Evita encher o storage com gravação de teste. |
| Formato do arquivo | **16 kHz mono 16-bit** (hoje 44,1 kHz) | 30 s caem de 2,6 MB para ~940 KB. Voz continua clara, upload aguenta internet ruim de bancada. |
| Âncora do áudio no ERP | **`test_id` da sessão** | O `checklist_report` só nasce no "Emitir laudo" — depois da gravação. O `test_id` existe desde o início da sessão e serve de chave nos dois sentidos. |
| Dropdown de resultado | **Continua `ComboBox`, restilizado** | Pedido do dono ("manter mais ou menos daquela forma"). Ganha altura de toque, cantos do padrão `Card` e bolinha de status colorida. |
| Escopo | **App + endpoint + migration + player, num ciclo só** | O botão "Enviar pro ERP" nasce funcionando de ponta a ponta. |

## 3. O que NÃO entra (e por quê)

- **Fila offline para o áudio.** O `OfflineQueue` do app hoje cuida de relatório,
  não de binário. Se a bancada estiver sem internet, o botão avisa o erro e o
  técnico reenvia. Fazer fila de binário é projeto próprio.
- **Envio automático ao salvar.** Descartado no brainstorm: guardaria áudio
  inútil.
- **Análise automática do áudio** (detectar chiado, estimar qualidade). O
  julgamento continua sendo do técnico.
- **Trocar o `ComboBox` por botões grandes.** O dono quis manter o padrão dos
  outros testes.

## 4. Parte 1 — Painel do microfone (app WPF)

### 4.1 Máquina de estados

O painel passa a ter quatro estados. Só um botão muda de papel por vez, para não
confundir na bancada.

```
                    ┌──────────────┐
   abre o modal ───▶│   OUVINDO    │  medidor ao vivo, nada gravado ainda
                    └──────┬───────┘
                    ● Gravar │
                    ┌──────▼───────┐
                    │   GRAVANDO   │  medidor ao vivo + contador 0:07 / 0:30
                    └──────┬───────┘
       ■ Encerrar / 30 s  │
                    ┌──────▼───────┐
              ┌────▶│    PRONTO    │  "Gravação de 12s pronta"
              │     └──────┬───────┘
              │     ▶ Ouvir │            ● Gravar ──▶ volta a GRAVANDO
              │     ┌──────▼───────┐     (descarta a gravação anterior)
              └─────│  REPRODUZINDO│  captação PAUSADA
        ■ Parar /   └──────────────┘
        fim do áudio
```

### 4.2 Regras de cada estado

| Estado | `● Gravar` | `▶ Ouvir` | `☁ Enviar pro ERP` | Captação |
|---|---|---|---|---|
| OUVINDO | ativo | desligado | desligado | ligada |
| GRAVANDO | vira `■ Encerrar` | desligado | desligado | ligada + gravando |
| PRONTO | ativo (regrava) | ativo | ativo | ligada |
| REPRODUZINDO | desligado | vira `■ Parar` | desligado | **pausada** |

**A pausa durante a reprodução é obrigatória**, não enfeite: sem ela o
alto-falante realimenta o microfone, a barra dispara e pode virar microfonia.

### 4.3 Contador e limite

Ao lado do medidor, durante a gravação: **`Gravando 0:07 / 0:30`**, atualizado a
cada meio segundo por um `DispatcherTimer`. Ao atingir 0:30 a gravação encerra
sozinha e a legenda passa a `Gravação de 30s (limite atingido)`.

Abaixo dos botões, fixo: *"Limite de 30 segundos por gravação. Gravar de novo
descarta a anterior."*

### 4.4 Formato

`WaveFormat` de `new(44100, 16, 1)` para `new(16000, 16, 1)`. O cálculo do pico
em `OnData` percorre amostras de 16 bits e não depende da taxa — não muda. O
`Summary` (pico %, dBFS, "som detectado") também não muda.

### 4.5 Fronteiras

`MicTestControl` continua sendo o dono de: captação, medidor, buffer WAV e
reprodução. O que ele **expõe para fora** cresce em um item:

```csharp
public byte[]? Wav { get; }              // já existe
public string Summary { get; }           // já existe (ITestInlineControl)
public void StopTest();                  // já existe (ITestInlineControl)
public Func<byte[], Task>? UploadAsync { get; set; }  // NOVO — injetado de fora
```

O controle **não conhece o `ErpClient`**. Quem sabe enviar é a `MainViewModel`,
que já tem `_erp` e o `test_id` da sessão; ela injeta o `UploadAsync` ao criar o
controle (`MainViewModel.cs:1339`). Se `UploadAsync` for nulo, o botão
`☁ Enviar pro ERP` nem aparece — é o que mantém o painel testável e reutilizável.

O botão mostra três estados de envio: `☁ Enviar pro ERP` → `Enviando...`
(desabilitado) → `✓ Enviado` (desabilitado). Em erro, volta a habilitar e mostra
a mensagem em vermelho abaixo.

## 5. Parte 2 — Resultado mais bonito (app WPF)

O `ComboBox` de `TestActionWindow.xaml:42` ganha um `Style` novo:

- altura 40 px (hoje é o padrão do WPF, apertado para touch);
- cantos arredondados e borda no tom do `Card`, para casar com o painel;
- **bolinha de status à esquerda do texto**, colorida pelo item selecionado:
  verde `OK`, âmbar `Atenção`, vermelho `Falha`, cinza `Não testado` e
  `Não aplicável`.

⚠️ **Esse dropdown é compartilhado por todos os testes** do `TestActionWindow`.
A restilização muda o visual de câmera, teclado, touchpad, brilho etc. Isso é
intencional (consistência), e foi comunicado ao dono no brainstorm.

## 6. Parte 3 — Armazenamento no ERP

### 6.1 Por que `test_id` e não `checklist_report.id`

O relatório completo só é criado quando o técnico emite o laudo, via
`fn_report_checklist_via_api`. A gravação acontece **antes**. Ancorar no `id` do
relatório exigiria segurar o áudio até o fim da sessão. O `test_id` (UUID gerado
no início da sessão, único por `(organization_id, test_id)` em
`checklist_reports`) resolve nas duas ordens: o áudio pode chegar antes ou depois
do laudo, e a tela junta pelo `test_id`.

### 6.2 Migration nova

```sql
create table public.checklist_audios (
  id              uuid primary key default gen_random_uuid(),
  organization_id uuid not null references public.organizations(id) on delete cascade,
  test_id         uuid not null,
  attachment_id   uuid not null references public.attachments(id) on delete cascade,
  kind            text not null default 'microfone' check (kind in ('microfone')),
  duracao_seg     numeric(5,1),
  created_at      timestamptz not null default now(),
  created_by      uuid,
  deleted_at      timestamptz
);

create index on public.checklist_audios (organization_id, test_id) where deleted_at is null;
alter table public.checklist_audios enable row level security;
```

O arquivo em si reusa a tabela `attachments` (polimórfica: `owner_table`,
`owner_id`) e o bucket privado **`attachments`**, seguindo a convenção de path já
usada pelo financeiro (`server/actions/finance.ts:821`):

```
<organization_id>/checklist_audios/<test_id>/<uuid>.wav
```

### 6.3 Duas RPCs

| RPC | Papel |
|---|---|
| `fn_checklist_audio_target()` | Confere permissão e devolve o `organization_id` do chamador. Necessária **antes** do upload, porque a policy do bucket exige o `org_id` como primeiro segmento do path. **Não valida o `test_id`** — por desenho, o áudio chega antes de o `checklist_report` existir, então não há o que conferir contra. |
| `fn_register_checklist_audio(p_test_id, p_bucket, p_path, p_filename, p_mime, p_size, p_duracao_seg)` | Insere em `attachments` (`owner_table='checklist_audios'`) e em `checklist_audios`, na mesma transação. Devolve o id. |

Mesmo desenho do fluxo de fotos (`readSession` → upload → `fn_register_asset_photo`
em `app/api/foto/[token]/route.ts`), só que autenticado por Bearer em vez de token
de sessão.

### 6.4 Endpoint

`POST /api/integracao/checklists/audio`

- **Auth:** `Authorization: Bearer` via `authServiceToken` — igual aos outros
  endpoints de `/api/integracao`.
- **Corpo:** `multipart/form-data` com `test_id` (UUID), `audio` (arquivo) e
  `duracao_seg` (opcional).
- **Validação:** `audio/wav` ou `audio/x-wav`; máximo **5 MB** (30 s a 16 kHz dá
  ~940 KB, então 5 MB é folga larga contra corrupção/abuso).
- **Ordem:** `fn_checklist_audio_target` → upload no bucket com o client admin →
  `fn_register_checklist_audio`. Se o registro falhar, **remove o arquivo órfão**
  do bucket antes de responder — mesmo cuidado da rota de foto.
- **Erros:** `401` sem token, `403` sem permissão na org, `413` grande demais,
  `415` formato errado, `422` `test_id` ausente ou não-UUID. Não há `404`: um
  `test_id` que ainda não virou relatório é o caso normal, não erro.

### 6.5 Cliente C#

Em `ErpClient`, no molde de `UploadPhotoAsync`, mas autenticado:

```csharp
public async Task UploadChecklistAudioAsync(
    Guid testId, byte[] wav, double duracaoSeg, CancellationToken ct)
```

Monta o `MultipartFormDataContent` e vai por `SendAuthedAsync`, que já cuida do
Bearer e do refresh do token.

### 6.6 Tela do ERP

Na página do relatório (`app/(app)/checklists/relatorio/[id]/page.tsx`), um card
**"Áudio do microfone"** com um `<audio controls>` por gravação, mostrando data e
duração.

- `getChecklistReport` (`server/queries/checklists.ts:196`) passa a trazer também
  o `test_id`.
- Uma query nova, `listChecklistAudios(testId)`, junta `checklist_audios` com
  `attachments` e gera **URL assinada** (o bucket é privado) — mesmo padrão de
  `server/queries/asset-photos.ts:45`.
- Se não houver gravação, o card não aparece.

## 7. Fluxo de ponta a ponta

```
Técnico abre o teste de microfone
  └─ medidor ao vivo (já existe hoje)
       └─ ● Gravar ──▶ fala ──▶ ■ Encerrar (ou 30 s)
            └─ ▶ Ouvir ──▶ julga a qualidade
                 ├─ ☁ Enviar pro ERP
                 │    POST /api/integracao/checklists/audio  (test_id + wav)
                 │      └─ bucket attachments + checklist_audios
                 └─ RESULTADO ▾ + comentário ──▶ Salvar

Depois, no "Emitir laudo":
  POST /api/integracao/checklists  (mesmo test_id)
    └─ checklist_reports

Na tela do ERP:
  /checklists/relatorio/{id}  ──▶ junta pelo test_id ──▶ player do áudio
```

## 8. Erros e casos de borda

| Situação | Comportamento |
|---|---|
| Sem microfone na máquina | Painel de "nenhum dispositivo" como hoje; os três botões ficam ocultos. |
| Trocar de dispositivo no meio da gravação | A gravação em andamento é descartada e o estado volta a OUVINDO, com aviso. |
| Fechar o modal gravando | `StopTest()` encerra e o WAV vira `Wav` normalmente (comportamento atual preservado). |
| Fechar o modal reproduzindo | A reprodução é parada e o `WaveOutEvent` descartado no `Unloaded`. |
| Sem internet ao enviar | Botão volta a habilitar com a mensagem do erro; o WAV segue em memória para reenviar. |
| ERP não configurado (`IsConfigured == false`) | O botão `☁ Enviar pro ERP` não aparece. |
| Enviar duas vezes o mesmo áudio | Cada envio gera um registro novo. Aceito: o técnico só reenvia se o primeiro falhou, e o botão trava em `✓ Enviado`. |
| Áudio enviado e laudo nunca emitido | O registro fica órfão em `checklist_audios`, sem tela que o mostre. Aceito nesta fase — o áudio sem check não tem leitura útil. |

## 9. Testes

**App (manual, na bancada):** gravar/encerrar/ouvir; limite de 30 s; regravar por
cima; verificar que a barra **não** reage durante a reprodução; enviar com e sem
internet; máquina sem microfone.

**ERP (automatizável):** o endpoint com `test_id` válido/inválido, arquivo grande
demais, MIME errado, sem Bearer; e o caso de o registro falhar após o upload —
o arquivo não pode ficar órfão no bucket.

**Ordem:** enviar o áudio **antes** do laudo e conferir que a tela do relatório
mostra o player depois que o laudo chega.

## 10. Ordem de implementação sugerida

1. **App, sem ERP:** estados, botões, contador, 30 s, 16 kHz, pausa na
   reprodução. Já dá para testar na bancada.
2. **Dropdown restilizado** (independente do resto).
3. **Migration + RPCs** no Supabase.
4. **Endpoint** `/api/integracao/checklists/audio`.
5. **`ErpClient.UploadChecklistAudioAsync` + fiação na `MainViewModel`**, ligando
   o botão.
6. **Player na tela do relatório.**
