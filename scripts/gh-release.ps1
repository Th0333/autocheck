# Publica (ou atualiza) o GitHub Release a partir do web/public/version.json.
# Chamado automaticamente pelo git hook post-commit do repo web/ quando o
# version.json muda — mas também pode ser rodado à mão.
#
# Uso: pwsh -File scripts/gh-release.ps1 [-RepoSlug user/repo]
#
# Requisitos:
#   - gh CLI instalado e autenticado (gh auth login) uma vez.
#   - O NotebookCheck.exe já publicado em publish/ (rode scripts/publish.ps1 antes).

param(
    [string] $RepoSlug = 'CEBOLAGG/notebook'
)

$ErrorActionPreference = 'Stop'

$root    = Resolve-Path (Join-Path $PSScriptRoot '..')
$verJson = Join-Path $root 'web/public/version.json'
$exe     = Join-Path $root 'publish/NotebookCheck.exe'

if (-not (Test-Path $verJson)) { throw "version.json não encontrado em $verJson" }

$manifest = Get-Content $verJson -Raw | ConvertFrom-Json
$version  = $manifest.version
$tag      = "v$version"
$notes    = ($manifest.changelog | ForEach-Object { "- $_" }) -join "`n"

$gh = Get-Command gh -ErrorAction SilentlyContinue
if (-not $gh) {
    # Fallback: local padrão de instalação quando o PATH ainda não recarregou.
    $ghDefault = Join-Path $env:ProgramFiles 'GitHub CLI/gh.exe'
    if (Test-Path $ghDefault) { $gh = $ghDefault } else { $gh = $null }
}
if (-not $gh) { throw "gh CLI não encontrado no PATH. Instale e rode 'gh auth login'." }
$ghExe = if ($gh -is [string]) { $gh } else { $gh.Source }

if (-not (Test-Path $exe)) {
    Write-Host "!! $exe não existe. Rode scripts/publish.ps1 -Version $version antes." -ForegroundColor Red
    exit 1
}

# Confere se a release já existe; cria ou atualiza o asset.
& $ghExe release view $tag --repo $RepoSlug *> $null
if ($LASTEXITCODE -eq 0) {
    Write-Host "==> Release $tag já existe — atualizando asset" -ForegroundColor Cyan
    & $ghExe release upload $tag $exe --repo $RepoSlug --clobber
} else {
    Write-Host "==> Criando release $tag" -ForegroundColor Cyan
    & $ghExe release create $tag $exe --repo $RepoSlug --title $tag --notes $notes
}

if ($LASTEXITCODE -eq 0) {
    Write-Host "==> Release $tag publicado com NotebookCheck.exe anexado." -ForegroundColor Green
} else {
    Write-Host "!! Falha ao publicar o release." -ForegroundColor Red
    exit 1
}
