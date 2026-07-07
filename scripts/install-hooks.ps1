# Instala o git hook post-commit no repo web/ que publica o GitHub Release
# automaticamente quando você commitar uma mudança em public/version.json.
#
# Rode uma vez: pwsh -File scripts/install-hooks.ps1

$ErrorActionPreference = 'Stop'

$root      = Resolve-Path (Join-Path $PSScriptRoot '..')
$hooksDir  = Join-Path $root 'web/.git/hooks'
$target    = Join-Path $hooksDir 'post-commit'

if (-not (Test-Path $hooksDir)) {
    throw "Pasta de hooks não encontrada: $hooksDir (o web/ é um repositório git?)"
}

$hook = @'
#!/bin/sh
# post-commit: se o commit mexeu em public/version.json, publica o GitHub
# Release automaticamente (via gh CLI) a partir do manifesto.

changed=$(git diff-tree --no-commit-id --name-only -r HEAD)
echo "$changed" | grep -q "public/version.json"
if [ $? -ne 0 ]; then
  exit 0
fi

echo "[post-commit] version.json mudou — publicando GitHub Release..."
repo_root=$(git rev-parse --show-toplevel)
release_script="$repo_root/../scripts/gh-release.ps1"

if command -v pwsh >/dev/null 2>&1; then
  pwsh -ExecutionPolicy Bypass -File "$release_script"
elif command -v powershell >/dev/null 2>&1; then
  powershell -ExecutionPolicy Bypass -File "$release_script"
else
  echo "[post-commit] pwsh/powershell não encontrado — pulei a publicação."
fi
exit 0
'@

# Grava com LF (hooks shell exigem) e sem BOM.
$hook = $hook -replace "`r`n", "`n"
[System.IO.File]::WriteAllText($target, $hook, [System.Text.UTF8Encoding]::new($false))

Write-Host "==> Hook post-commit instalado em $target" -ForegroundColor Green
Write-Host "    A partir de agora, ao commitar uma mudança em public/version.json"
Write-Host "    dentro do repo web/, o GitHub Release é publicado automaticamente."
