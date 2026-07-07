using NotebookCheck.Domain.Models;

namespace NotebookCheck.Domain.Abstractions;

/// <summary>
/// Repositório local responsável por serializar e gravar relatórios de
/// checklist e reteste exclusivamente dentro de <c>AppContext.BaseDirectory</c>
/// (Requirements 1.4, 1.5, 22.1).
/// </summary>
/// <remarks>
/// Antes de gravar, o repositório valida unicidade pelo trio
/// <c>(serial, tested_at, test_id)</c> e, em caso de duplicata, retorna o
/// caminho do arquivo já existente sem reescrever (Requirement 23.4).
/// Em falhas de IO no pendrive, dispara o evento <see cref="WriteFailed"/>
/// para que a UI possa bloquear novos inícios (Requirements 1.10, 1.11).
/// </remarks>
public interface IReportRepository
{
    /// <summary>
    /// Persiste um <see cref="ChecklistReport"/> como arquivo JSON e retorna o
    /// caminho absoluto gerado pelo <c>FilenameBuilder</c>.
    /// </summary>
    Task<string> SaveAsync(ChecklistReport report, CancellationToken ct);

    /// <summary>
    /// Persiste um <see cref="RetestReport"/> como arquivo JSON e retorna o
    /// caminho absoluto gerado pelo <c>FilenameBuilder</c>.
    /// </summary>
    Task<string> SaveAsync(RetestReport report, CancellationToken ct);

    /// <summary>
    /// Disparado quando uma operação de gravação falha (ex.:
    /// <see cref="UnauthorizedAccessException"/>, <see cref="IOException"/>,
    /// <see cref="DriveNotFoundException"/>).
    /// </summary>
    event EventHandler<ReportRepositoryWriteFailedEventArgs>? WriteFailed;
}

/// <summary>
/// Argumentos do evento <see cref="IReportRepository.WriteFailed"/> contendo o
/// nome do arquivo afetado e a exceção original.
/// </summary>
public sealed class ReportRepositoryWriteFailedEventArgs : EventArgs
{
    public ReportRepositoryWriteFailedEventArgs(string fileName, Exception exception)
    {
        FileName = fileName;
        Exception = exception;
    }

    /// <summary>Nome do arquivo cuja gravação falhou.</summary>
    public string FileName { get; }

    /// <summary>Exceção original que causou a falha.</summary>
    public Exception Exception { get; }
}
