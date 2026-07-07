using NotebookCheck.Infrastructure.Api;

namespace NotebookCheck.Domain.Abstractions;

/// <summary>
/// Fila offline FIFO de payloads que falharam ao ser enviados para a API REST.
/// Cada item é persistido como <c>./queue/pending_&lt;test_id&gt;.json</c>
/// envelopado com <c>enqueued_at</c> e <c>attempts</c> (Requirements 25.1–25.4).
/// </summary>
public interface IOfflineQueue
{
    /// <summary>Quantidade de payloads pendentes na fila.</summary>
    int Count { get; }

    /// <summary>Adiciona um <paramref name="payload"/> ao final da fila.</summary>
    Task EnqueueAsync(ApiPayload payload, CancellationToken ct);

    /// <summary>
    /// Retorna todos os payloads pendentes em ordem FIFO (por
    /// <c>enqueued_at</c>) sem removê-los; o consumidor decide entre
    /// <see cref="RemoveAsync"/> em sucesso ou <see cref="IncrementAttemptsAsync"/>
    /// em falha.
    /// </summary>
    Task<IReadOnlyList<QueuedPayload>> DequeueAsync(CancellationToken ct);

    /// <summary>
    /// Remove o item identificado por <paramref name="testId"/> da fila;
    /// silenciosamente ignorado quando o item não existe.
    /// </summary>
    Task RemoveAsync(string testId, CancellationToken ct);

    /// <summary>
    /// Incrementa o contador <c>attempts</c> do item identificado por
    /// <paramref name="testId"/> sem removê-lo da fila.
    /// </summary>
    Task IncrementAttemptsAsync(string testId, CancellationToken ct);
}

/// <summary>
/// Envelope persistido em disco contendo o <see cref="ApiPayload"/> original e
/// metadados de gerenciamento da fila.
/// </summary>
/// <param name="Payload">Payload original enfileirado.</param>
/// <param name="EnqueuedAt">
/// Carimbo de tempo (UTC) em que o payload foi adicionado à fila; usado para
/// ordenação FIFO.
/// </param>
/// <param name="Attempts">Quantidade de tentativas de envio já realizadas.</param>
public record QueuedPayload(
    ApiPayload Payload,
    DateTime EnqueuedAt,
    int Attempts);
