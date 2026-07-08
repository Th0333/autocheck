using System;
using System.Collections.Generic;
using System.Linq;

namespace NotebookCheck.Infrastructure.Erp;

/// <summary>
/// Compara a configuração ACORDADA no pedido de compra com a que foi realmente
/// encontrada na máquina. Usado pelo cadastro (check de entrada) e pelo teste
/// completo (autocheck) — a mesma regra nos dois, para o ERP não receber
/// divergências que dependem de qual tela mandou.
/// </summary>
public static class ErpConfigComparer
{
    /// <summary>
    /// Diferenças legíveis ("RAM: acordado 16 GB, encontrado 8 GB"). Lista vazia
    /// quando não há config acordada, não há specs, ou tudo bate.
    /// </summary>
    public static List<string> Divergencias(ErpConfigAcordada? acordada, ErpEspecificacoes? specs)
    {
        var divs = new List<string>();
        if (acordada is null || specs is null) return divs;

        if (acordada.RamGb is int ramAc && specs.RamGb is int ramEnc && ramAc != ramEnc)
            divs.Add($"RAM: acordado {ramAc} GB, encontrado {ramEnc} GB");

        // Discos "512 GB" reportam ~477 GiB reais — tolerância para não gerar
        // alerta falso; fora de 88%–130% do acordado é divergência de verdade.
        if (acordada.StorageGb is int stAc && stAc > 0 && specs.StorageGb is int stEnc &&
            (stEnc < stAc * 0.88 || stEnc > stAc * 1.30))
            divs.Add($"Armazenamento: acordado {stAc} GB, encontrado {stEnc} GB");

        if (!string.IsNullOrWhiteSpace(acordada.Processador) && !string.IsNullOrWhiteSpace(specs.Processador))
        {
            static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            if (!Norm(specs.Processador!).Contains(Norm(acordada.Processador!)))
                divs.Add($"Processador: acordado {acordada.Processador}, encontrado {specs.Processador}");
        }

        return divs;
    }

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
}
