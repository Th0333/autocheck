using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NotebookCheck.Domain.Abstractions;
using NotebookCheck.Domain.Models;
using NotebookCheck.Domain.Privacy;
using NotebookCheck.Domain.Rules;

namespace NotebookCheck.Infrastructure.Persistence;

/// <summary>
/// Repositório local de relatórios em JSON, gravando em
/// <see cref="AppContext.BaseDirectory"/> e bloqueando duplicatas pelo trio
/// <c>(serial, tested_at, test_id)</c>.
/// </summary>
public sealed class JsonReportRepository : IReportRepository
{
    private readonly string _baseDir;
    private readonly ILogger<JsonReportRepository> _logger;
    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public event EventHandler<ReportRepositoryWriteFailedEventArgs>? WriteFailed;

    public JsonReportRepository(ILogger<JsonReportRepository> logger, string? baseDir = null)
    {
        _baseDir = baseDir ?? AppContext.BaseDirectory;
        _logger = logger;
        Directory.CreateDirectory(_baseDir);
    }

    public Task<string> SaveAsync(ChecklistReport report, CancellationToken ct)
    {
        var fileName = FilenameBuilder.BuildChecklistFileName(report.Machine.Serial, report.TestedAt);
        return SaveInternalAsync(report, fileName, ct);
    }

    public Task<string> SaveAsync(RetestReport report, CancellationToken ct)
    {
        var fileName = FilenameBuilder.BuildRetestFileName(report.Machine.Serial, report.RetestedComponents, report.TestedAt);
        return SaveInternalAsync(report, fileName, ct);
    }

    private async Task<string> SaveInternalAsync<T>(T report, string fileName, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(report, _options);
        json = LogSanitizer.SanitizeReport(json);

        // 1) Diretório preferido (pasta do exe / pendrive).
        try
        {
            return await WriteOnceAsync(_baseDir, fileName, json, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // O pendrive pode ter sido removido / trocado de letra / ficar
            // somente-leitura no meio da sessão (DirectoryNotFoundException é
            // subtipo de IOException). Não perdemos o relatório: caímos para a
            // pasta garantida do perfil do usuário.
            _logger.LogWarning(ex, "Falha gravando relatório em {Dir} — usando fallback local", _baseDir);
        }

        // 2) Fallback garantido (%LOCALAPPDATA%\Notelet).
        var fallbackDir = Bootstrap.AppPaths.FallbackDir;
        if (string.Equals(Path.GetFullPath(fallbackDir).TrimEnd('\\'),
                          Path.GetFullPath(_baseDir).TrimEnd('\\'),
                          StringComparison.OrdinalIgnoreCase))
        {
            // Já estávamos no fallback — não há para onde mais ir.
            var failName = fileName;
            WriteFailed?.Invoke(this, new ReportRepositoryWriteFailedEventArgs(failName,
                new IOException($"Não foi possível gravar o relatório em {_baseDir}")));
            throw new IOException($"Não foi possível gravar o relatório em {_baseDir}");
        }
        try
        {
            var path = await WriteOnceAsync(fallbackDir, fileName, json, ct).ConfigureAwait(false);
            _logger.LogWarning("Relatório salvo no fallback {File} (diretório preferido indisponível)", path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Falha gravando relatório no fallback {File}", fileName);
            WriteFailed?.Invoke(this, new ReportRepositoryWriteFailedEventArgs(fileName, ex));
            throw;
        }
    }

    private async Task<string> WriteOnceAsync(string dir, string fileName, string json, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);

        // Idempotência por nome (que carrega serial + timestamp).
        if (File.Exists(path))
        {
            _logger.LogInformation("Relatório duplicado detectado, retornando existente {File}", path);
            return path;
        }

        await File.WriteAllTextAsync(path, json, Encoding.UTF8, ct).ConfigureAwait(false);
        _logger.LogInformation("Relatório gravado em {File}", path);
        return path;
    }
}
