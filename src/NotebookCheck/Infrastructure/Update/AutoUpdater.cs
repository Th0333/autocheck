using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NotebookCheck.Bootstrap;

namespace NotebookCheck.Infrastructure.Update;

/// <summary>
/// Manifesto de versão lido do site (version.json).
/// </summary>
public sealed class VersionManifest
{
    public string Version { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Sha256 { get; set; }
    public string[] Changelog { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Auto-updater do .exe único portátil. Estratégia (Opção B — sem launcher):
///   1. Baixa o version.json do site.
///   2. Compara a versão remota com <see cref="AppDefaults.CurrentVersion"/>.
///   3. Se houver versão nova: baixa o novo .exe (do GitHub Release) para um
///      arquivo temporário ao lado do atual, valida o SHA256.
///   4. Escreve um .bat que: aguarda este processo fechar, troca o .exe antigo
///      pelo novo, e reabre o app. Depois fecha o app atual.
///
/// Como um .exe em execução fica travado no Windows, o .bat faz a troca
/// DEPOIS que o processo encerra. Funciona rodando de pendrive.
/// </summary>
public sealed class AutoUpdater
{
    private readonly ILogger<AutoUpdater> _logger;

    public AutoUpdater(ILogger<AutoUpdater> logger)
    {
        _logger = logger;
    }

    /// <summary>Changelog da versão atualmente instalada (preenchido após checar).</summary>
    public string[] CurrentChangelog { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Baixa o manifesto. Nunca lança — devolve null em falha (sem internet etc).
    /// Tenta primeiro a API (MongoDB) e cai no version.json estático (CDN) como
    /// fallback, para funcionar mesmo se o banco estiver fora.
    /// </summary>
    public async Task<VersionManifest?> FetchManifestAsync(CancellationToken ct)
    {
        // 1) Fonte primária: API que lê do Mongo.
        var fromApi = await TryFetchAsync(AppDefaults.VersionApiUrl, ct).ConfigureAwait(false);
        if (fromApi is not null) return fromApi;

        // 2) Fallback: arquivo estático na CDN.
        return await TryFetchAsync(AppDefaults.VersionManifestUrl, ct).ConfigureAwait(false);
    }

    private async Task<VersionManifest?> TryFetchAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NotebookCheck-Updater");
            // cache-buster para sempre pegar o mais recente
            var sep = baseUrl.Contains('?') ? "&" : "?";
            var url = baseUrl + sep + "t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var json = await http.GetStringAsync(url, ct).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize<VersionManifest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
                return null;
            if (manifest.Changelog.Length > 0)
                CurrentChangelog = manifest.Changelog;
            return manifest;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha ao buscar manifesto de versão em {Url}", baseUrl);
            return null;
        }
    }

    /// <summary>True se a versão do manifesto é mais nova que a instalada.</summary>
    public static bool IsNewer(string remote, string local)
    {
        if (Version.TryParse(remote, out var r) && Version.TryParse(local, out var l))
            return r > l;
        // Fallback: comparação textual diferente = considera nova.
        return !string.Equals(remote?.Trim(), local?.Trim(), StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(remote);
    }

    /// <summary>
    /// Baixa o novo .exe e devolve o caminho do arquivo temporário, validando
    /// o SHA256 se informado. <paramref name="progress"/> recebe % (0..100).
    /// Devolve null em qualquer falha.
    /// </summary>
    public async Task<string?> DownloadAsync(VersionManifest manifest, IProgress<int>? progress, CancellationToken ct)
    {
        try
        {
            var currentExe = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(currentExe)) return null;

            var dir = Path.GetDirectoryName(currentExe)!;
            var newExe = Path.Combine(dir, "NotebookCheck.new.exe");

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NotebookCheck-Updater");

            using (var resp = await http.GetAsync(manifest.Url,
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? 0;
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(newExe, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[1024 * 256];
                long read = 0;
                int n;
                var lastPct = -1;
                while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    read += n;
                    if (total > 0)
                    {
                        var pct = (int)(read * 100 / total);
                        if (pct != lastPct) { lastPct = pct; progress?.Report(pct); }
                    }
                }
            }

            // Valida SHA256, se fornecido.
            if (!string.IsNullOrWhiteSpace(manifest.Sha256))
            {
                var actual = ComputeSha256(newExe);
                if (!string.Equals(actual, manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("SHA256 do update não confere (esperado {Exp}, obtido {Act})",
                        manifest.Sha256, actual);
                    try { File.Delete(newExe); } catch { }
                    return null;
                }
            }

            return newExe;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao baixar atualização");
            return null;
        }
    }

    /// <summary>
    /// Aplica a atualização: escreve um .bat que troca o .exe e reabre o app,
    /// dispara o .bat e devolve true (o caller deve fechar o app em seguida).
    /// </summary>
    public bool ApplyAndRestart(string newExePath)
    {
        try
        {
            var currentExe = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(currentExe) || !File.Exists(newExePath)) return false;

            var dir = Path.GetDirectoryName(currentExe)!;
            var batPath = Path.Combine(dir, "nbc_update.bat");
            var pid = Environment.ProcessId;
            var exeName = Path.GetFileName(currentExe);

            // O .bat:
            //  1. Aguarda o PID atual encerrar (timeout/loop).
            //  2. Substitui o .exe antigo pelo novo.
            //  3. Reabre o app.
            //  4. Apaga a si mesmo.
            var bat = new StringBuilder();
            bat.AppendLine("@echo off");
            bat.AppendLine("setlocal");
            bat.AppendLine($"set \"TARGET={currentExe}\"");
            bat.AppendLine($"set \"NEWEXE={newExePath}\"");
            bat.AppendLine(": waitloop");
            // tasklist filtra pelo PID; se ainda existir, espera
            bat.AppendLine($"tasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL");
            bat.AppendLine("if not errorlevel 1 (");
            bat.AppendLine("  timeout /t 1 /nobreak >NUL");
            bat.AppendLine("  goto waitloop");
            bat.AppendLine(")");
            // Tenta substituir (com algumas tentativas, caso o handle demore a liberar)
            bat.AppendLine("set /a tries=0");
            bat.AppendLine(": replace");
            bat.AppendLine("del /f /q \"%TARGET%\" >NUL 2>&1");
            bat.AppendLine("if exist \"%TARGET%\" (");
            bat.AppendLine("  set /a tries+=1");
            bat.AppendLine("  if %tries% lss 10 ( timeout /t 1 /nobreak >NUL & goto replace )");
            bat.AppendLine(")");
            bat.AppendLine("move /y \"%NEWEXE%\" \"%TARGET%\" >NUL 2>&1");
            bat.AppendLine("start \"\" \"%TARGET%\"");
            bat.AppendLine("del /f /q \"%~f0\" >NUL 2>&1");

            File.WriteAllText(batPath, bat.ToString(), new UTF8Encoding(false));

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{batPath}\"",
                WorkingDirectory = dir,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao aplicar atualização");
            return false;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        var hash = sha.ComputeHash(fs);
        return Convert.ToHexString(hash);
    }
}
