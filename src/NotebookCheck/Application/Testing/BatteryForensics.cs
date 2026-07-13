using System;
using System.Collections.Generic;
using System.Linq;

namespace NotebookCheck.Application.Testing;

/// <summary>Um indício encontrado na perícia da bateria.</summary>
/// <param name="Titulo">Resumo curto do indício.</param>
/// <param name="Detalhe">Explicação com os números que sustentam o indício.</param>
/// <param name="Forte">True quando o indício sozinho já justifica suspeita.</param>
public sealed record BatterySignal(string Titulo, string Detalhe, bool Forte);

/// <summary>Resultado consolidado da perícia estática.</summary>
/// <param name="Nivel">"ok" | "atencao" | "suspeita".</param>
public sealed record BatteryForensicsResult(string Nivel, IReadOnlyList<BatterySignal> Sinais)
{
    public string NivelLabel => Nivel switch
    {
        "suspeita" => "SUSPEITA DE RECONDICIONADA",
        "atencao" => "Atenção — verificar",
        _ => "Sem indícios de adulteração",
    };
}

/// <summary>
/// Perícia de bateria recondicionada/resetada. O golpe clássico: o pack está
/// gasto, mas o técnico reseta o contador do controlador (cycle count e
/// full-charge voltam ao valor de fábrica) e a bateria "parece nova" em
/// qualquer leitor. A perícia cruza os registros do controlador entre si
/// (parte estática) e contra a física da descarga real (parte dinâmica —
/// registro pode mentir, consumo de energia não).
/// </summary>
public static class BatteryForensics
{
    /// <summary>
    /// Análise estática: cruza os valores reportados pelo controlador.
    /// Qualquer campo null é tolerado (nem todo pack expõe tudo).
    /// </summary>
    public static BatteryForensicsResult AnalyzeStatic(
        int? designMwh,
        int? fullMwh,
        int? cycleCount,
        string? serialNumber,
        DateTime? manufactureDate,
        DateTime? nowUtc = null)
    {
        var sinais = new List<BatterySignal>();
        var agora = nowUtc ?? DateTime.UtcNow;

        if (designMwh is > 0 && fullMwh is > 0)
        {
            var design = designMwh.Value;
            var full = fullMwh.Value;
            var wearPct = (1.0 - (double)full / design) * 100.0;

            if (full > design * 1.02)
            {
                sinais.Add(new BatterySignal(
                    "Capacidade acima do projeto",
                    $"Full-charge reporta {full} mWh, MAIOR que os {design} mWh de projeto — firmware adulterado ou célula trocada.",
                    Forte: true));
            }
            else if (Math.Abs(full - design) <= design * 0.005 && (cycleCount ?? 0) <= 3)
            {
                sinais.Add(new BatterySignal(
                    "Registro idêntico ao de fábrica",
                    $"Full-charge ({full} mWh) bate exatamente com o projeto ({design} mWh) e o contador marca {(cycleCount ?? 0)} ciclo(s). Bateria realmente nova OU controlador resetado — confirme com o teste de descarga.",
                    Forte: false));
            }

            if ((cycleCount is null || cycleCount == 0) && wearPct >= 8)
            {
                sinais.Add(new BatterySignal(
                    "Ciclos zerados com desgaste presente",
                    $"O contador marca zero ciclos, mas o pack já perdeu {wearPct:F1}% da capacidade — incoerente com bateria sem uso.",
                    Forte: true));
            }
        }

        if (IsGenericSerial(serialNumber))
        {
            sinais.Add(new BatterySignal(
                "Serial apagado ou genérico",
                $"Serial reportado: \"{serialNumber ?? "(vazio)"}\" — packs recondicionados costumam vir com o serial apagado.",
                Forte: false));
        }

        if (manufactureDate is { } data)
        {
            if (data > agora.AddDays(1))
            {
                sinais.Add(new BatterySignal(
                    "Data de fabricação no futuro",
                    $"O controlador reporta fabricação em {data:dd/MM/yyyy} — registro reescrito.",
                    Forte: true));
            }
            else
            {
                var idadeAnos = (agora - data).TotalDays / 365.25;
                if (idadeAnos >= 3 && (cycleCount ?? 0) <= 3)
                {
                    sinais.Add(new BatterySignal(
                        "Bateria antiga com ciclos zerados",
                        $"Fabricada em {data:dd/MM/yyyy} ({idadeAnos:F1} anos) e o contador marca {(cycleCount ?? 0)} ciclo(s) — bateria parada tanto tempo é improvável; indica reset.",
                        Forte: true));
                }
            }
        }

        var fortes = sinais.Count(s => s.Forte);
        var fracos = sinais.Count(s => !s.Forte);
        var nivel = fortes >= 1 || fracos >= 2 ? "suspeita"
            : fracos == 1 ? "atencao"
            : "ok";
        return new BatteryForensicsResult(nivel, sinais);
    }

    /// <summary>Resultado do confronto descarga real × registro do controlador.</summary>
    /// <param name="CapacidadeEfetivaMwh">Capacidade plena extrapolada da descarga medida.</param>
    /// <param name="RazaoVsReportada">Efetiva ÷ reportada (1.0 = registro honesto).</param>
    public sealed record DischargeVerdict(
        double CapacidadeEfetivaMwh,
        double RazaoVsReportada,
        bool Suspeita,
        string Resumo);

    /// <summary>
    /// Parte dinâmica: durante N minutos descarregando sob carga, o gauge caiu
    /// <paramref name="quedaPercentual"/> pontos percentuais enquanto consumia
    /// <paramref name="consumoMwh"/> mWh. Extrapola a capacidade plena efetiva
    /// e compara com a reportada — divergência grande = controlador mentindo.
    /// </summary>
    public static DischargeVerdict? EvaluateDischarge(
        int reportedFullMwh,
        double consumoMwh,
        double quedaPercentual)
    {
        if (reportedFullMwh <= 0 || consumoMwh <= 0 || quedaPercentual < 0.5)
        {
            return null; // amostra insuficiente para concluir
        }

        var efetiva = consumoMwh / (quedaPercentual / 100.0);
        var razao = efetiva / reportedFullMwh;
        // O gauge derrete mais rápido que a energia consumida → a capacidade
        // real é menor que a registrada. Tolerância de 25% cobre variação de
        // temperatura/carga.
        var suspeita = razao < 0.75;
        var resumo = suspeita
            ? $"Descarga real indica ~{efetiva / 1000.0:F1} Wh de capacidade plena efetiva, só {razao * 100:F0}% dos {reportedFullMwh / 1000.0:F1} Wh registrados — o controlador está superestimando (típico de pack resetado)."
            : $"Descarga real compatível com o registro: ~{efetiva / 1000.0:F1} Wh efetivos vs {reportedFullMwh / 1000.0:F1} Wh registrados ({razao * 100:F0}%).";
        return new DischargeVerdict(efetiva, razao, suspeita, resumo);
    }

    private static bool IsGenericSerial(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial)) return true;
        var s = serial.Trim();
        if (s.Length <= 2) return true;
        if (s.All(c => c == '0') || s.All(c => c == s[0])) return true;
        return s.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            || s.Equals("none", StringComparison.OrdinalIgnoreCase)
            || s.Equals("n/a", StringComparison.OrdinalIgnoreCase);
    }
}
