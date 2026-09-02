using System;
using System.Collections.Generic;
using System.Linq;

namespace NotebookCheck.Infrastructure.Erp;

/// <summary>
/// Uma peça conferida entre a config acordada no pedido e a encontrada na
/// máquina. <see cref="Codigo"/> é o valor estável que vai para o ERP
/// (<c>config_pecas_divergentes</c>); <see cref="Texto"/> é a divergência
/// legível, no mesmo formato que o ERP já mostra na aprovação.
/// </summary>
public sealed record ConfigCheckItem(
    string Codigo,
    string Peca,
    string Acordado,
    string Encontrado,
    bool Diverge,
    string Texto);

/// <summary>
/// Compara a configuração ACORDADA no pedido de compra com a que foi realmente
/// encontrada na máquina. Usado pelo cadastro (check de entrada) e pelo teste
/// completo (autocheck) — a mesma regra nos dois, para o ERP não receber
/// divergências que dependem de qual tela mandou.
/// </summary>
public static class ErpConfigComparer
{
    /// <summary>Códigos das peças, como o ERP recebe em <c>config_pecas_divergentes</c>.</summary>
    public const string Processador = "processador";
    public const string Ram = "ram";
    public const string Armazenamento = "armazenamento";
    public const string PlacaVideo = "placa_video";
    public const string Tela = "tela";
    public const string Outro = "outro";

    /// <summary>
    /// Conferência peça a peça: uma linha para cada campo que o pedido fixou
    /// (RAM, armazenamento, processador). Lista vazia quando não há config
    /// acordada ou as specs ainda não foram lidas.
    /// </summary>
    public static List<ConfigCheckItem> Comparar(ErpConfigAcordada? acordada, ErpEspecificacoes? specs)
    {
        var itens = new List<ConfigCheckItem>();
        if (acordada is null || specs is null) return itens;

        if (acordada.RamGb is int ramAc)
        {
            var diverge = specs.RamGb is int ramEnc && ramAc != ramEnc;
            itens.Add(new ConfigCheckItem(Ram, "Memória RAM",
                $"{ramAc} GB", specs.RamGb is int r ? $"{r} GB" : "—", diverge,
                diverge ? $"RAM: acordado {ramAc} GB, encontrado {specs.RamGb} GB" : ""));
        }

        // Discos "512 GB" reportam ~477 GiB reais — tolerância para não gerar
        // alerta falso; fora de 88%–130% do acordado é divergência de verdade.
        if (acordada.StorageGb is int stAc && stAc > 0)
        {
            var diverge = specs.StorageGb is int stEnc && (stEnc < stAc * 0.88 || stEnc > stAc * 1.30);
            itens.Add(new ConfigCheckItem(Armazenamento, "Armazenamento",
                $"{stAc} GB", specs.StorageGb is int s ? $"{s} GB" : "—", diverge,
                diverge ? $"Armazenamento: acordado {stAc} GB, encontrado {specs.StorageGb} GB" : ""));
        }

        if (!string.IsNullOrWhiteSpace(acordada.Processador))
        {
            var diverge = !string.IsNullOrWhiteSpace(specs.Processador)
                          && !Norm(specs.Processador!).Contains(Norm(acordada.Processador!));
            itens.Add(new ConfigCheckItem(Processador, "Processador",
                acordada.Processador!, string.IsNullOrWhiteSpace(specs.Processador) ? "—" : specs.Processador!, diverge,
                diverge ? $"Processador: acordado {acordada.Processador}, encontrado {specs.Processador}" : ""));
        }

        return itens;
    }

    /// <summary>
    /// Diferenças legíveis ("RAM: acordado 16 GB, encontrado 8 GB"). Lista vazia
    /// quando não há config acordada, não há specs, ou tudo bate.
    /// </summary>
    public static List<string> Divergencias(ErpConfigAcordada? acordada, ErpEspecificacoes? specs) =>
        Comparar(acordada, specs).Where(i => i.Diverge).Select(i => i.Texto).ToList();

    /// <summary>Descrição curta da config acordada ("i5-8350U · 16 GB RAM · 256 GB").</summary>
    public static string Describe(ErpConfigAcordada? c)
    {
        if (c is null) return "";
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(c.Processador)) parts.Add(c.Processador!);
        if (c.RamGb is int r) parts.Add($"{r} GB RAM");
        if (c.StorageGb is int s) parts.Add($"{s} GB armazenamento");
        return string.Join(" · ", parts);
    }

    /// <summary>"i5-8350U" bate com "Intel(R) Core(TM) i5-8350U CPU @ 1.70GHz".</summary>
    private static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
