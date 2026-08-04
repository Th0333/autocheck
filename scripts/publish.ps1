# Publica o NotebookCheck como .exe portátil único.
# Toda a configuração (URL do painel, token de autenticação) vive em
# src/NotebookCheck/Bootstrap/AppDefaults.cs e fica embutida no executável.
# Não há mais config.json nem nada para distribuir além do .exe.
#
# Uso simples (sem mexer em versão):
#   pwsh -File scripts/publish.ps1
#
# Uso com nova versão + changelog (gera version.json para o auto-updater):
#   pwsh -File scripts/publish.ps1 -Version 1.1.0 -Changelog "Auto-updater","Correções de bateria"
#
# Parâmetros:
#   -Version    Nova versão semântica (ex: 1.1.0). Grava em AppDefaults.CurrentVersion
#               e no assembly. Se omitido, usa a versão atual do AppDefaults.cs.
#   -Changelog  Lista de linhas do changelog mostradas na tela inicial do app.
#   -RepoSlug   user/repo do GitHub para montar a URL do .exe no Release.
#               Default: Th0333/autocheck  (AJUSTE para o seu repositório)

param(
    [string]   $Version,
    [string[]] $Changelog,
    [string]   $ChangelogFile,
    [string]   $RepoSlug = 'Th0333/autocheck',
    [switch]   $CreateRelease,
    [string]   $ApiBaseUrl = 'https://notebook-gamma-seven.vercel.app',
    [string]   $IngestToken = 'segredaotop',
    [switch]   $SkipPublishApi
)

$ErrorActionPreference = 'Stop'

$root     = Resolve-Path (Join-Path $PSScriptRoot '..')
$proj     = Join-Path $root 'src/NotebookCheck/NotebookCheck.csproj'
$out      = Join-Path $root 'publish'
$defaults = Join-Path $root 'src/NotebookCheck/Bootstrap/AppDefaults.cs'
$verJson  = Join-Path $root 'web/public/version.json'

# ----------------------------------------------------------------------------
# 1. Resolver a versão (atualiza AppDefaults.cs se -Version foi informado)
# ----------------------------------------------------------------------------
$defaultsText = Get-Content $defaults -Raw

if ($Version) {
    Write-Host "==> Gravando versão $Version em AppDefaults.cs" -ForegroundColor Cyan
    $defaultsText = [regex]::Replace(
        $defaultsText,
        'CurrentVersion\s*=\s*"[^"]*"',
        "CurrentVersion = `"$Version`"")
    Set-Content -Path $defaults -Value $defaultsText -NoNewline -Encoding UTF8
} else {
    $m = [regex]::Match($defaultsText, 'CurrentVersion\s*=\s*"([^"]*)"')
    if (-not $m.Success) { throw "Não foi possível ler CurrentVersion de AppDefaults.cs" }
    $Version = $m.Groups[1].Value
    Write-Host "==> Usando versão atual: $Version" -ForegroundColor Cyan
}

# ----------------------------------------------------------------------------
# 2. Build / publish
# ----------------------------------------------------------------------------
if (Test-Path $out) {
    Get-ChildItem $out -Recurse -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $out -Force | Out-Null

Write-Host "==> Publicando $proj" -ForegroundColor Cyan

dotnet publish $proj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:DebugType=embedded `
    /p:_IsPublishing=true `
    "/p:Version=$Version" `
    "/p:AssemblyVersion=$Version.0" `
    "/p:FileVersion=$Version.0" `
    -o $out

if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou (exit $LASTEXITCODE)" }

$exe = Join-Path $out 'NotebookCheck.exe'
if (-not (Test-Path $exe)) { throw "Arquivo $exe não encontrado" }

$hash = Get-FileHash $exe -Algorithm SHA256
$size = (Get-Item $exe).Length / 1MB

# ----------------------------------------------------------------------------
# 3. Gerar version.json para o auto-updater (web/public/version.json)
#    Acumula o histórico: o topo guarda a versão ATUAL (lida pelo app) e o
#    array "history" mantém todas as versões anteriores com seus changelogs.
# ----------------------------------------------------------------------------
# A URL aponta para um asset do GitHub Release com a tag v<versão>.
$exeUrl = "https://github.com/$RepoSlug/releases/download/v$Version/NotebookCheck.exe"

if (-not $Changelog -or $Changelog.Count -eq 0) {
    if ($ChangelogFile -and (Test-Path $ChangelogFile)) {
        # Cada linha não-vazia do arquivo vira um item do changelog.
        $Changelog = @(Get-Content $ChangelogFile | Where-Object { $_.Trim() -ne '' })
    } else {
        $Changelog = @("Atualização $Version")
    }
}

# Lê o manifesto anterior para acumular o histórico.
$history = @()
if (Test-Path $verJson) {
    try {
        $prev = Get-Content $verJson -Raw | ConvertFrom-Json
        # Preserva o histórico anterior.
        if ($prev.history) { $history = @($prev.history) }
        # Empurra a versão anterior do topo para o histórico (se for diferente
        # da que estamos publicando agora — evita duplicar ao republicar).
        if ($prev.version -and $prev.version -ne $Version) {
            $alreadyInHistory = $history | Where-Object { $_.version -eq $prev.version }
            if (-not $alreadyInHistory) {
                $history = @([ordered]@{
                    version   = $prev.version
                    date      = $prev.date
                    changelog = @($prev.changelog)
                }) + $history
            }
        }
    } catch {
        Write-Host "    (aviso: version.json anterior ilegível, recriando)" -ForegroundColor Yellow
    }
}

$manifest = [ordered]@{
    version   = $Version
    date      = (Get-Date -Format 'yyyy-MM-dd')
    url       = $exeUrl
    sha256    = $hash.Hash
    changelog = @($Changelog)
    history   = @($history)
}

$verDir = Split-Path $verJson -Parent
if (-not (Test-Path $verDir)) { New-Item -ItemType Directory -Path $verDir -Force | Out-Null }
$manifest | ConvertTo-Json -Depth 6 | Set-Content -Path $verJson -Encoding UTF8

Write-Host ""
Write-Host "==> NotebookCheck.exe gerado" -ForegroundColor Green
Write-Host ("    Versão:  {0}" -f $Version)
Write-Host ("    Tamanho: {0:F2} MB" -f $size)
Write-Host ("    SHA256:  {0}" -f $hash.Hash)
Write-Host ""
Write-Host "==> version.json atualizado (com histórico acumulado)" -ForegroundColor Green
Write-Host ("    {0}" -f $verJson)
Write-Host ("    URL do .exe: {0}" -f $exeUrl)

# ----------------------------------------------------------------------------
# 4. (Opcional) Cria/atualiza o GitHub Release automaticamente via gh CLI.
#    Dispare com -CreateRelease. Requer 'gh auth login' feito uma vez.
# ----------------------------------------------------------------------------
$releaseOk = $false
if ($CreateRelease) {
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $gh) {
        $ghDefault = Join-Path $env:ProgramFiles 'GitHub CLI/gh.exe'
        if (Test-Path $ghDefault) { $gh = $ghDefault }
    }
    if (-not $gh) {
        Write-Host ""
        Write-Host "!! gh CLI não encontrado no PATH. Pulei a criação do release." -ForegroundColor Yellow
    } else {
        $ghExe = if ($gh -is [string]) { $gh } else { $gh.Source }
        $tag   = "v$Version"
        $notes = ($Changelog | ForEach-Object { "- $_" }) -join "`n"

        Write-Host ""
        Write-Host "==> Publicando release $tag no GitHub ($RepoSlug)" -ForegroundColor Cyan

        # Se a release já existe, sobe/atualiza o asset; senão, cria.
        & $ghExe release view $tag --repo $RepoSlug *> $null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "    Release já existe — atualizando o asset NotebookCheck.exe"
            & $ghExe release upload $tag $exe --repo $RepoSlug --clobber
        } else {
            & $ghExe release create $tag $exe `
                --repo $RepoSlug `
                --title $tag `
                --notes $notes
        }
        if ($LASTEXITCODE -ne 0) {
            Write-Host "!! Falha ao publicar o release via gh." -ForegroundColor Red
        } else {
            # Release em RASCUNHO tem asset "uploaded" e sai com codigo 0, mas a
            # URL publica de download responde 404 - a bancada ve "Falha ao
            # baixar a atualizacao". Aconteceu na v1.8.7 (04/08/2026). Tirar do
            # rascunho e obrigatorio, nao cosmetico.
            & $ghExe release edit $tag --repo $RepoSlug --draft=false *> $null

            # Prova real: baixa o cabecalho SEM autenticacao, que e exatamente o
            # que a bancada faz. Conferir com 'gh' nao vale - o gh usa token e
            # enxerga rascunho, entao daria OK num release que ninguem baixa.
            Write-Host "    Conferindo o download publico (sem token)..."
            $publicOk = $false
            foreach ($tentativa in 1..6) {
                try {
                    $probe = Invoke-WebRequest -Uri $exeUrl -Method Head -UseBasicParsing `
                        -MaximumRedirection 5 -TimeoutSec 30
                    if ($probe.StatusCode -eq 200) { $publicOk = $true; break }
                } catch {
                    # Recem-publicado leva alguns segundos para propagar no CDN.
                }
                Start-Sleep -Seconds 5
            }

            if ($publicOk) {
                $releaseOk = $true
                Write-Host "==> Release $tag publicado e baixavel publicamente." -ForegroundColor Green
            } else {
                Write-Host "!! Release $tag NAO esta baixavel publicamente ($exeUrl)." -ForegroundColor Red
                Write-Host "   Confira se o release saiu do rascunho e se o repo e publico." -ForegroundColor Yellow
            }
        }
    }
}

# ----------------------------------------------------------------------------
# 5. Publica o manifesto no MongoDB via API (POST /api/version).
#    Isso elimina o deploy manual da Vercel: o app passa a ler do banco.
#    O version.json estático continua sendo gerado como fallback.
# ----------------------------------------------------------------------------
if (-not $SkipPublishApi -and $CreateRelease -and -not $releaseOk) {
    # Trava de seguranca: se o release falhou, publicar o manifesto faria as
    # bancadas apontarem para um .exe que nao existe (download 404 em todas).
    Write-Host ""
    Write-Host "!! Release NAO foi publicado - manifesto NAO enviado a API." -ForegroundColor Red
    Write-Host "   Corrija o gh (auth) e rode de novo, ou crie o release manualmente" -ForegroundColor Yellow
    Write-Host "   com o MESMO exe de ./publish e depois envie o manifesto." -ForegroundColor Yellow
} elseif (-not $SkipPublishApi) {
    $apiUrl = $ApiBaseUrl.TrimEnd('/') + '/api/version'
    $payload = [ordered]@{
        version   = $Version
        url       = $exeUrl
        sha256    = $hash.Hash
        changelog = @($Changelog)
        date      = (Get-Date -Format 'yyyy-MM-dd')
    } | ConvertTo-Json -Depth 5

    Write-Host ""
    Write-Host "==> Publicando manifesto na API ($apiUrl)" -ForegroundColor Cyan
    try {
        $resp = Invoke-RestMethod -Uri $apiUrl -Method Post `
            -Headers @{ Authorization = "Bearer $IngestToken" } `
            -ContentType 'application/json; charset=utf-8' `
            -Body ([System.Text.Encoding]::UTF8.GetBytes($payload))
        Write-Host "==> Manifesto publicado no MongoDB. O app já vê a versão $Version (sem deploy)." -ForegroundColor Green
    } catch {
        Write-Host "!! Falha ao publicar na API: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "   (o version.json estático foi gerado — você pode cair no fallback via deploy da Vercel)" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Checklist de publicação:" -ForegroundColor Yellow
if (-not $CreateRelease) {
    Write-Host "  1. Rode com -CreateRelease para criar o GitHub Release automaticamente"
    Write-Host "     (ou crie manualmente a tag v$Version e anexe o .exe)"
} else {
    Write-Host "  1. Release no GitHub: OK (feito acima)"
}
Write-Host "  2. Manifesto no MongoDB via API: feito acima (o app já enxerga a nova versão)"
Write-Host "     Deploy da Vercel só é necessário se você mexeu no CÓDIGO do site"
Write-Host "     (o fallback version.json estático precisa de deploy; o Mongo não)."
