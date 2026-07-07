using NotebookCheck.Domain.Enums;

namespace NotebookCheck.Domain.Models;

/// <summary>
/// Resultado de um teste automático individual produzido pelo Motor_de_Testes.
/// </summary>
/// <param name="TestKey">
/// Identificador textual em snake_case correspondente ao <c>ComponentId</c>
/// do teste (ex.: "ram", "storage", "battery").
/// </param>
/// <param name="Status">Status final atribuído ao item.</param>
/// <param name="Details">
/// Texto livre descritivo serializado no campo <c>details</c> do payload da API.
/// </param>
/// <param name="ExecutedAt">Data e hora local em que o teste foi executado.</param>
/// <param name="Comment">
/// Comentário opcional do técnico para este teste, adicionado pelo botão "+"
/// na grade. Serializado em <c>comment</c> no payload.
/// </param>
public record TestResult(
    string TestKey,
    AutoStatus Status,
    string Details,
    DateTime ExecutedAt,
    string Comment = "");
