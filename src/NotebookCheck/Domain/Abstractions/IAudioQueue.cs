using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NotebookCheck.Domain.Abstractions;

/// <summary>
/// Uma gravação esperando para subir. Traz o carimbo da máquina junto — é o que
/// permite a fila mandar cada áudio para o check certo, mesmo muito depois.
/// </summary>
/// <param name="Id">Identificador do item na fila (nome dos arquivos).</param>
/// <param name="TestId">Sessão de check a que a gravação pertence.</param>
/// <param name="Ntb">NTB da máquina, quando conhecido.</param>
/// <param name="Serial">Serial da máquina, quando conhecido.</param>
/// <param name="Mime">MIME do binário (<c>audio/mp4</c> ou <c>audio/wav</c>).</param>
/// <param name="Extension">Extensão sem ponto (<c>m4a</c> ou <c>wav</c>).</param>
/// <param name="DuracaoSeg">Duração da gravação em segundos.</param>
/// <param name="EnqueuedAt">Quando entrou na fila (UTC).</param>
/// <param name="Attempts">Quantas vezes já tentou subir.</param>
public sealed record PendingAudio(
    string Id,
    string TestId,
    string? Ntb,
    string? Serial,
    string Mime,
    string Extension,
    double DuracaoSeg,
    DateTime EnqueuedAt,
    int Attempts);

/// <summary>
/// Fila das gravações de microfone pendentes de envio ao ERP.
/// </summary>
public interface IAudioQueue
{
    /// <summary>Quantas gravações estão esperando.</summary>
    int Count { get; }

    /// <summary>Guarda a gravação e o carimbo da máquina.</summary>
    Task EnqueueAsync(PendingAudio item, byte[] bytes, CancellationToken ct);

    /// <summary>
    /// Pendentes em ordem FIFO. Aproveita a varredura para descartar itens
    /// expirados ou sem binário.
    /// </summary>
    Task<IReadOnlyList<PendingAudio>> ListAsync(CancellationToken ct);

    /// <summary>Bytes do áudio de um item, ou null se o arquivo sumiu.</summary>
    Task<byte[]?> ReadBytesAsync(string id, CancellationToken ct);

    /// <summary>Tira o item da fila (subiu, ou foi recusado sem retorno).</summary>
    Task RemoveAsync(string id, CancellationToken ct);

    /// <summary>Conta mais uma tentativa falha.</summary>
    Task IncrementAttemptsAsync(string id, CancellationToken ct);
}
