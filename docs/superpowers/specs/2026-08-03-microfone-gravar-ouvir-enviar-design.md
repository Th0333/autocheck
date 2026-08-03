# Design — Microfone: gravar, encerrar, ouvir e enviar pro ERP

- **Data:** 2026-08-03
- **Status:** aprovado (brainstorm com o dono do produto), revisado na mesma data
  após o dono pedir **fila offline por máquina** e avisar que o **storage do
  Supabase estourou**
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
| Captura | **16 kHz mono 16-bit** (hoje 44,1 kHz) | Voz continua clara e o buffer em memória cai para 1/3. |
| Formato enviado | **AAC/`.m4a`, com WAV de reserva** | O storage do Supabase estourou. 30 s viram 182 KB em vez de 938 KB — **5× menos**, medido. Ver §4.5. |
| Falha no envio | **Fila offline em disco, por máquina** | Pedido do dono. Cada gravação carrega o `test_id`/NTB da sua própria máquina, então a fila sabe para qual check mandar. Ver §6.7. |
| Âncora do áudio no ERP | **`test_id` da sessão** | O `checklist_report` só nasce no "Emitir laudo" — depois da gravação. O `test_id` existe desde o início da sessão e serve de chave nos dois sentidos. |
| Dropdown de resultado | **Continua `ComboBox`, restilizado** | Pedido do dono ("manter mais ou menos daquela forma"). Ganha altura de toque, cantos do padrão `Card` e bolinha de status colorida. |
| Escopo | **App + endpoint + migration + player, num ciclo só** | O botão "Enviar pro ERP" nasce funcionando de ponta a ponta. |

## 3. O que NÃO entra (e por quê)

- **Envio automático ao salvar.** Descartado no brainstorm: guardaria áudio
  inútil.
- **Reaproveitar o `OfflineQueue` existente.** Ele é keyed por `test_id` e guarda
  `ApiPayload` (JSON do relatório) — um item por máquina. Áudio é binário e pode
  haver mais de um por máquina. Vira uma fila própria (§6.7), não um remendo na
  que já existe.
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

### 4.5 Compressão antes do envio

O dono avisou que **o storage do Supabase acabou** e que ainda está decidindo o
que fazer. Mandar WAV cru agrava exatamente o problema dele, então o áudio é
comprimido no momento do envio:

| Formato | 10 s | 30 s |
|---|---|---|
| WAV 44,1 kHz (hoje) | 880 KB | 2,6 MB |
| WAV 16 kHz (captura nova) | 313 KB | 938 KB |
| **AAC `.m4a` (enviado)** | **~61 KB** | **182 KB** |

Os números do AAC são **medidos**, não estimados: 30 s sintéticos a 16 kHz
passaram de 938 KB para 182 KB nesta máquina (−81%). O encoder do Windows não
desce de 48 kbps em mono, então 182 KB é o piso real — a estimativa inicial de
120 KB era otimista.

A conversão fica num helper isolado, `Infrastructure/Audio/AudioCompressor.cs`:

```csharp
public static (byte[] bytes, string mime, string ext) ForUpload(byte[] wav);
```

Usa `MediaFoundationEncoder` do NAudio — o codificador AAC **vem no Windows**,
sem pacote NuGet novo. O encoder da Media Foundation só aceita entrada em 44,1/48
kHz, então o WAV de 16 kHz passa por um `MediaFoundationResampler` antes.

**Reserva:** qualquer falha na conversão (codec ausente, resampler recusando,
saída vazia ou maior que a entrada) devolve o **WAV original** com
`audio/wav`. O envio nunca quebra por causa da compressão — no pior caso ele só
fica maior. Por isso o helper é isolado: se a Media Foundation se mostrar
instável na bancada, trocar por `NAudio.Lame` (MP3) mexe em um arquivo só.

O `.m4a` toca nativamente no `<audio controls>` de Chrome, Edge e Firefox.

### 4.6 Fronteiras

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
A restilização muda o visual de câmera, teclado, touchpad, brilho etc. O dono
aprovou explicitamente:

> "o dropdown tudo bem alterar a aparencia de todos oq ta escrito em cada um tem
> que continuar escrito mas a aparencia se for ficar mais bonita tudo bem"

Ou seja: **só aparência**. Os cinco itens continuam com o texto idêntico — `OK`,
`Atenção`, `Falha`, `Não testado`, `Não aplicável` — porque
`TestActionWindow.SelectStatus` e `OnSave` casam status **por string**
(`(string)item.Content == status`). Mudar uma letra quebraria a leitura do
resultado em todos os testes.

## 6. Parte 3 — Armazenamento no ERP

> ⚠️ **A migration é entregue, não aplicada.** O dono avisou que não sabe se dá
> para rodar migration no Supabase agora. O arquivo fica pronto em
> `supabase/migrations/`, e o resumo final da entrega traz o aviso explícito de
> que **nada do lado do ERP funciona até ela ser aplicada**.

### 6.1 Por que `test_id` e não `checklist_report.id`

O relatório completo só é criado quando o técnico emite o laudo, via
`fn_report_checklist_via_api`. A gravação acontece **antes**. Ancorar no `id` do
relatório exigiria segurar o áudio até o fim da sessão. O `test_id` (UUID gerado
no início da sessão, único por `(organization_id, test_id)` em
`checklist_reports`) resolve nas duas ordens: o áudio pode chegar antes ou depois
do laudo, e a tela junta pelo `test_id`.

**Mas o `test_id` sozinho não amarra o áudio à máquina.** O dono foi explícito:

> "cada audio tem que ficar linkado com seu propio pc la no erp no check"

Se o técnico gravar e nunca emitir o laudo, um áudio preso só ao `test_id` fica
invisível. Por isso o app manda também **NTB e serial**, e a RPC resolve o
`asset_id` a partir do NTB quando a máquina já existe no ERP. Resultado: o áudio
aparece na tela do relatório **e** na da máquina, e continua achável mesmo sem
laudo. Os três campos são gravados como vieram — o `asset_id` é um bônus, não um
requisito.

### 6.2 Migration nova

```sql
create table public.checklist_audios (
  id              uuid primary key default gen_random_uuid(),
  organization_id uuid not null references public.organizations(id) on delete cascade,
  test_id         uuid not null,
  asset_id        uuid references public.assets(id) on delete set null,
  ntb             text,
  serial          text,
  attachment_id   uuid not null references public.attachments(id) on delete cascade,
  kind            text not null default 'microfone' check (kind in ('microfone')),
  duracao_seg     numeric(5,1),
  created_at      timestamptz not null default now(),
  created_by      uuid,
  deleted_at      timestamptz
);

create index on public.checklist_audios (organization_id, test_id) where deleted_at is null;
create index on public.checklist_audios (organization_id, asset_id) where deleted_at is null;
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

### 6.7 Fila offline de áudio

Pedido do dono:

> "se a fila conseguir identificar isso e ir enviando para cada lugar"

Ela consegue, porque **cada gravação já nasce carimbada com a máquina dela**: o
`test_id` é único por sessão, e sessão é uma máquina só. A fila não precisa
adivinhar nada — só reenviar o que está guardado, com o carimbo que veio junto.

**Onde mora:** subpasta `audio-queue/` do mesmo diretório gravável que a fila de
relatórios já usa (`AppPaths.ResolveWritable` — pasta do `.exe` se der para
escrever, senão `%LOCALAPPDATA%\Notelet`). Um par de arquivos por gravação:

```
audio_<id>.json  → { test_id, ntb, serial, mime, extension, duracao_seg, enqueued_at, attempts }
audio_<id>.bin   → o áudio já comprimido
```

Arquivo em disco, não banco: o áudio já é um blob, e assim uma gravação
corrompida não derruba a fila inteira.

**Quando drena:** no laço de 60 s do `OfflineSyncService`, que já existe e já
testa a conexão antes de tentar. O áudio entra na mesma verificação do relatório
— se houver internet, os dois sobem na mesma passada.

**Regras:** FIFO por `enqueued_at`; cada item tenta no máximo **5 vezes** e
depois fica parado (não some — some só quando sobe); itens com mais de **7 dias**
ou sem o `.bin` do lado são descartados na varredura; um erro `4xx` que não seja
`401`/`408`/`429` descarta na hora, porque repetir não conserta arquivo inválido
ou formato errado.

**Na tela:** o botão vira `☁ Na fila` (não `✓ Enviado`) e a mensagem em âmbar diz
`Sem conexão com o ERP — a gravação ficou na fila (3) e sobe sozinha depois`. O
botão trava mesmo assim: reenviar duplicaria o áudio, já que a cópia enfileirada
sobe por conta própria.

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
| Sem internet ao enviar | Vai para a fila (§6.7) carimbado com o `test_id`/NTB da máquina; o botão mostra `☁ Na fila`. |
| ERP não configurado (`IsConfigured == false`) | O botão `☁ Enviar pro ERP` não aparece. |
| Enviar duas vezes o mesmo áudio | Cada envio gera um registro novo. Aceito: o técnico só reenvia se o primeiro falhou, e o botão trava em `✓ Enviado`. |
| Áudio enviado e laudo nunca emitido | Continua achável pelo NTB/`asset_id` na tela da máquina (§6.1). |
| Compressão falha na bancada | Sobe o WAV original (§4.5). Fica maior, mas sobe. |
| Migration ainda não aplicada | O endpoint responde erro e o áudio **fica na fila**, sem perder nada. Ao aplicar a migration, a fila drena sozinha. |

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
3. **`AudioCompressor`** (§4.5) — isolado, testável sozinho.
4. **Fila de áudio** (§6.7) — também isolada, não depende do endpoint existir.
5. **Migration + RPCs** — arquivo entregue, **aplicação fica com o dono**.
6. **Endpoint** `/api/integracao/checklists/audio`.
7. **`ErpClient.UploadChecklistAudioAsync` + fiação na `MainViewModel`**, ligando
   o botão.
8. **Player na tela do relatório.**

Os passos 1–4 valem por si e não dependem do Supabase. Se a migration demorar a
ser aplicada, o técnico já grava e ouve, e o que ele enviar fica na fila
esperando.
