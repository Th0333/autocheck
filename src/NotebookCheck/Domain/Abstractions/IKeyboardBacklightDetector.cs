using System.Threading;
using System.Threading.Tasks;
using NotebookCheck.Domain.Enums;

namespace NotebookCheck.Domain.Abstractions;

/// <summary>
/// Detector da presença de teclado retroiluminado (Requirements 33.1, 33.2, 33.6).
/// Em qualquer falha (exceção, ausência de privilégios, timeout), a
/// implementação retorna <see cref="KeyboardBacklight.Indisponivel"/>.
/// </summary>
public interface IKeyboardBacklightDetector
{
    Task<KeyboardBacklight> DetectAsync(CancellationToken ct);
}
