using NotebookCheck.Infrastructure.Api;

namespace NotebookCheck.Domain.Abstractions;

/// <summary>
/// Repositório consolidado de todos os relatórios gerados localmente. Mantido
/// em um único arquivo <c>reports.json</c> ao lado do executável para que o
/// pendrive carregue um histórico completo. Cada entrada guarda o
/// <see cref="ApiPayload"/> original e o estado de sincronização com a API.
/// </summary>
public interface IReportArchive
{
    /// <summary>Adiciona um novo relatório ao arquivo, marcado como pendente.</summary>
    Task AppendAsync(ApiPayload payload, CancellationToken ct);

    /// <summary>
    /// Marca uma entrada como sincronizada com a API REST (carimbo do momento
    /// do envio bem-sucedido).
    /// </summary>
    Task MarkSyncedAsync(string testId, CancellationToken ct);

    /// <summary>Lista todas as entradas do arquivo, mais recentes primeiro.</summary>
    Task<IReadOnlyList<LocalReportEntry>> ListAsync(CancellationToken ct);

    /// <summary>Caminho absoluto onde o arquivo é mantido.</summary>
    string ArchivePath { get; }
}

/// <summary>
/// Representa uma entrada do arquivo local consolidado.
/// </summary>
/// <param name="TestId">Identificador único do relatório (UUID).</param>
/// <param name="ReceivedAt">Carimbo de quando foi adicionado ao arquivo (ISO 8601).</param>
/// <param name="SyncedAt">
/// Carimbo de envio bem-sucedido para a API, ou <c>null</c> quando ainda
/// pendente.
/// </param>
/// <param name="Payload">Payload original a ser enviado / re-enviado.</param>
public record LocalReportEntry(
    string TestId,
    string ReceivedAt,
    string? SyncedAt,
    ApiPayload Payload);
