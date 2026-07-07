# NotebookCheck

Plataforma para a equipe técnica de uma loja de notebooks executar checklists
de hardware/inspeção e centralizar os relatórios. Tem duas partes:

- **`src/NotebookCheck`** — App desktop (.NET 8 + WPF) executado pelo técnico
  na máquina sob teste. Coleta hardware, roda testes automáticos, captura
  inspeção física, salva o relatório em JSON local e envia para o painel.
- **`web/`** — Painel web (Next.js + MongoDB) com lista, filtros, detalhe e
  comentários. Hospedado na Vercel.

## Fluxo do checklist no app

1. **Início** — escolher entre _Iniciar checklist_ ou _Reteste de componente_.
2. **Identificação** — apelido/código NTB (obrigatório), localização,
   etiqueta de patrimônio (opcional) e identificação do técnico.
3. **Coleta de hardware** — WMI/PowerShell coletam fabricante, modelo, RAM,
   storage, bateria, TPM/Secure Boot, etc.
4. **Testes automáticos** — RAM, armazenamento (SMART), bateria, carregador,
   HDMI, Wi-Fi, Bluetooth, internet. Áudio e microfone são acionados pelo
   técnico.
5. **Inspeção física** — itens manuais (carcaça, tela, teclado, etc.) com
   status _OK / Com defeito / Não testado / Observação_. Itens com defeito
   ou observação exigem descrição.
6. **Resumo** — classificação final (Aprovado, Aprovado com ressalvas,
   Reprovado) calculada automaticamente.
7. **Concluído** — relatório salvo em JSON e enviado ao painel; em caso de
   falha de rede vai para a fila offline e é reenviado em background.

## Build do app desktop

```pwsh
# Restaurar e compilar
dotnet build NotebookCheck.sln

# Publicar como .exe portátil único (resultado em ./publish)
pwsh -File scripts/publish.ps1
```

## Configuração

Não há mais `config.json` no lado do `.exe`. Os valores ficam embutidos em
`src/NotebookCheck/Bootstrap/AppDefaults.cs` e são compilados dentro do
executável. Para mudar algum deles, edite as constantes e rode
`pwsh scripts/publish.ps1` para gerar um novo .exe:

| Constante      | Descrição                                                                |
| -------------- | ------------------------------------------------------------------------ |
| `ApiBaseUrl`   | URL do painel na Vercel, ex.: `https://notebook-gamma-seven.vercel.app`  |
| `ApiEndpoint`  | `/api/reports`                                                           |
| `AuthToken`    | Mesmo valor do `INGEST_TOKEN` configurado no painel.                     |

## Painel web

Veja [`web/README.md`](web/README.md) para deploy na Vercel e configuração do
MongoDB. Toda interação com o banco está em `web/lib/repository.ts` — quando o
sistema migrar para o **Bling**, basta reescrever essa camada mantendo as
mesmas assinaturas.

## Estrutura

```
src/NotebookCheck/        App WPF (.NET 8)
tests/NotebookCheck.Tests Testes (xUnit + FsCheck)
web/                      Painel Next.js + MongoDB
scripts/publish.ps1       Empacota o .exe portátil
```
