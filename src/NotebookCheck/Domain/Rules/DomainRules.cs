using System;
using System.Collections.Generic;
using NotebookCheck.Domain.Enums;

namespace NotebookCheck.Domain.Rules;

/// <summary>
/// Regras puras de domínio do checklist: cálculo de desgaste de bateria, classificação
/// por faixas, classificação final consolidada e seleção do pior <see cref="AutoStatus"/>.
/// </summary>
/// <remarks>
/// <para>
/// Todos os métodos desta classe são puros (sem efeitos colaterais), determinísticos e
/// totais sobre seus domínios de entrada documentados. Servem de base para os testes
/// baseados em propriedades (FsCheck) descritos no design.
/// </para>
/// <para>
/// Implementa: Requirements 6.2, 6.3, 6.4, 6.5, 6.8, 7.3, 21.1, 21.2, 21.3 e 32.10.
/// </para>
/// </remarks>
public static class DomainRules
{
    /// <summary>
    /// Calcula o desgaste percentual da bateria a partir das capacidades de design e atual,
    /// arredondado para duas casas decimais (Requirement 6.2).
    /// </summary>
    /// <param name="designMwh">Capacidade de design da bateria, em mWh. Deve ser maior que zero.</param>
    /// <param name="currentMwh">Capacidade atual da bateria, em mWh. Deve ser maior ou igual a zero.</param>
    /// <returns>
    /// Desgaste percentual no intervalo <c>[0, 100]</c>, calculado como
    /// <c>(1 - currentMwh / designMwh) * 100</c> e arredondado a duas casas decimais.
    /// Quando <paramref name="currentMwh"/> é maior que <paramref name="designMwh"/>,
    /// trata-se de uma anomalia controlada e o método retorna <c>0</c> (Requirement 6.8).
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Quando <paramref name="designMwh"/> é menor ou igual a zero, ou quando
    /// <paramref name="currentMwh"/> é negativo.
    /// </exception>
    public static decimal ComputeWear(int designMwh, int currentMwh)
    {
        if (designMwh <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(designMwh),
                designMwh,
                "A capacidade de design deve ser maior que zero.");
        }
        if (currentMwh < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentMwh),
                currentMwh,
                "A capacidade atual não pode ser negativa.");
        }

        // Anomalia controlada (Requirement 6.8): capacidade atual maior que a de design
        // resulta em desgaste zero, e a anomalia em si é tratada na camada de teste.
        if (currentMwh > designMwh)
        {
            return 0m;
        }

        var wear = (1m - ((decimal)currentMwh / designMwh)) * 100m;
        return Math.Round(wear, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Classifica o status do item de bateria em função do desgaste percentual,
    /// conforme Requirements 6.3, 6.4 e 6.5.
    /// </summary>
    /// <param name="wearPercent">Desgaste percentual da bateria (tipicamente em <c>[0, 100]</c>).</param>
    /// <returns>
    /// <see cref="AutoStatus.OK"/> quando o desgaste é menor ou igual a 20%;
    /// <see cref="AutoStatus.Atencao"/> quando é maior que 20% e menor ou igual a 40%;
    /// <see cref="AutoStatus.Falha"/> quando é maior que 40%.
    /// </returns>
    public static AutoStatus ClassifyBatteryWear(decimal wearPercent)
    {
        if (wearPercent <= 20m)
        {
            return AutoStatus.OK;
        }
        if (wearPercent <= 40m)
        {
            return AutoStatus.Atencao;
        }
        return AutoStatus.Falha;
    }

    /// <summary>
    /// Consolida a classificação final do equipamento a partir dos status dos itens
    /// automáticos e manuais, conforme Requirements 21.1, 21.2, 21.3 e 32.10.
    /// </summary>
    /// <param name="auto">Status dos itens automáticos. Pode ser vazio.</param>
    /// <param name="manual">Status dos itens manuais. Pode ser vazio.</param>
    /// <returns>
    /// <see cref="FinalClassification.Reprovado"/> quando existe pelo menos um item
    /// automático com status <see cref="AutoStatus.Falha"/> ou um item manual com status
    /// <see cref="ManualStatus.ComDefeito"/>; <see cref="FinalClassification.AprovadoComRessalvas"/>
    /// quando, na ausência de falhas/defeitos, existe pelo menos um item automático com
    /// status <see cref="AutoStatus.Atencao"/> ou um item manual com status
    /// <see cref="ManualStatus.Observacao"/>; caso contrário,
    /// <see cref="FinalClassification.Aprovado"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Quando <paramref name="auto"/> ou <paramref name="manual"/> é <c>null</c>.
    /// </exception>
    public static FinalClassification ClassifyFinal(
        IEnumerable<AutoStatus> auto,
        IEnumerable<ManualStatus> manual)
    {
        if (auto is null)
        {
            throw new ArgumentNullException(nameof(auto));
        }
        if (manual is null)
        {
            throw new ArgumentNullException(nameof(manual));
        }

        var hasWarning = false;

        foreach (var status in auto)
        {
            if (status == AutoStatus.Falha)
            {
                return FinalClassification.Reprovado;
            }
            if (status == AutoStatus.Atencao)
            {
                hasWarning = true;
            }
        }

        foreach (var status in manual)
        {
            if (status == ManualStatus.ComDefeito)
            {
                return FinalClassification.Reprovado;
            }
            if (status == ManualStatus.Observacao)
            {
                hasWarning = true;
            }
        }

        return hasWarning
            ? FinalClassification.AprovadoComRessalvas
            : FinalClassification.Aprovado;
    }

    /// <summary>
    /// Retorna o pior <see cref="AutoStatus"/> entre os fornecidos, conforme a hierarquia
    /// de severidade descrita no Requirement 7.3 estendida para todos os valores do enum:
    /// <c>NaoAplicavel &lt; NaoTestado &lt; OK &lt; Atencao &lt; Falha</c>.
    /// </summary>
    /// <param name="statuses">Vetor não vazio de status a serem comparados.</param>
    /// <returns>O status de maior severidade entre os fornecidos.</returns>
    /// <exception cref="ArgumentNullException">Quando <paramref name="statuses"/> é <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Quando <paramref name="statuses"/> está vazio.</exception>
    public static AutoStatus WorstOf(params AutoStatus[] statuses)
    {
        if (statuses is null)
        {
            throw new ArgumentNullException(nameof(statuses));
        }
        if (statuses.Length == 0)
        {
            throw new ArgumentException(
                "É necessário informar pelo menos um status para determinar o pior.",
                nameof(statuses));
        }

        var worst = statuses[0];
        var worstSeverity = SeverityOf(worst);

        for (var i = 1; i < statuses.Length; i++)
        {
            var current = statuses[i];
            var currentSeverity = SeverityOf(current);
            if (currentSeverity > worstSeverity)
            {
                worst = current;
                worstSeverity = currentSeverity;
            }
        }

        return worst;
    }

    /// <summary>
    /// Mapeia cada valor de <see cref="AutoStatus"/> para um índice de severidade não
    /// negativo. Quanto maior o índice, mais severo o status.
    /// </summary>
    private static int SeverityOf(AutoStatus status) => status switch
    {
        AutoStatus.NaoAplicavel => 0,
        AutoStatus.NaoTestado => 1,
        AutoStatus.OK => 2,
        AutoStatus.Atencao => 3,
        AutoStatus.Falha => 4,
        _ => throw new ArgumentOutOfRangeException(
            nameof(status),
            status,
            "Valor de AutoStatus desconhecido."),
    };
}
