using System;
using System.Collections.Generic;
using System.Linq;
using NotebookCheck.Domain.Models;

namespace NotebookCheck.Infrastructure.Erp;

/// <summary>
/// Monta os campos de placa de vídeo enviados ao ERP.
///
/// O coletor já descobre TODOS os adaptadores físicos (iGPU + dGPU, incluindo a
/// dedicada desligada pelo Optimus, que só aparece via PnPEntity) e já filtra os
/// virtuais. O que se perdia era daqui para a frente: o payload do ERP mandava
/// um único nome, escolhido por prioridade, então notebook com Intel UHD + GeForce
/// chegava no ERP como se tivesse só a dedicada.
///
/// Agora vão os dois campos:
///   - <c>gpu</c>  : texto legível com TODAS as placas, dedicada primeiro
///                   ("NVIDIA GeForce MX150 + Intel UHD Graphics 620"). Continua
///                   sendo um campo só, então quem já lia isso não quebra.
///   - <c>gpus</c> : a lista estruturada, com VRAM e driver de cada uma.
/// </summary>
public static class ErpGpuMapper
{
    /// <summary>Limite do campo texto no ERP.</summary>
    private const int GpuTextoMax = 120;

    /// <summary>
    /// Ordem de exibição: dedicada antes de integrada. Espelha o
    /// <c>GpuPriorityForDisplay</c> do coletor de propósito — se as duas listas
    /// discordassem, a placa "principal" da tela do app e a do ERP seriam
    /// diferentes para a mesma máquina.
    /// </summary>
    private static int Prioridade(string nome)
    {
        if (nome.IndexOf("RTX", StringComparison.OrdinalIgnoreCase) >= 0) return 3;
        if (nome.IndexOf("GTX", StringComparison.OrdinalIgnoreCase) >= 0) return 3;
        if (nome.IndexOf("Radeon RX", StringComparison.OrdinalIgnoreCase) >= 0) return 3;
        if (nome.IndexOf("Radeon Pro", StringComparison.OrdinalIgnoreCase) >= 0) return 3;
        if (nome.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase)) return 3;
        if (nome.IndexOf("Arc A", StringComparison.OrdinalIgnoreCase) >= 0
            && !nome.Contains("Arc Graphics", StringComparison.OrdinalIgnoreCase)) return 2;
        if (nome.IndexOf("UHD Graphics", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
        if (nome.IndexOf("Iris", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
        if (nome.IndexOf("HD Graphics", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
        if (nome.IndexOf("Vega ", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
        if (nome.IndexOf("Radeon Graphics", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
        return 0;
    }

    /// <summary>É placa dedicada? Usado para marcar cada linha no ERP.</summary>
    private static bool EhDedicada(string nome, int? vramMb)
    {
        if (Prioridade(nome) >= 2) return true;
        // integrada não tem VRAM dedicada de verdade; acima de 512 MB é dGPU
        return vramMb is > 512;
    }

    /// <summary>
    /// Todas as placas, dedicada primeiro, sem repetir nome. Junta o que veio de
    /// <c>GraphicsDetails</c> (com VRAM/driver) com o que só apareceu em
    /// <c>GraphicsAdapters</c> (nome solto).
    /// </summary>
    public static List<ErpGpu> Listar(MachineInfo m)
    {
        var porNome = new Dictionary<string, ErpGpu>(StringComparer.OrdinalIgnoreCase);

        foreach (var g in m.GraphicsDetails ?? Array.Empty<GraphicsInfo>())
        {
            if (string.IsNullOrWhiteSpace(g.Name)) continue;
            porNome[g.Name] = new ErpGpu
            {
                Nome = g.Name,
                VramMb = g.VramMb,
                DriverVersao = g.DriverVersion,
                DriverData = g.DriverDate,
                Dedicada = EhDedicada(g.Name, g.VramMb),
            };
        }

        // nome que só o PnPEntity viu entra sem VRAM — melhor listar sem detalhe
        // do que sumir com a placa
        foreach (var nome in m.GraphicsAdapters ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(nome) || porNome.ContainsKey(nome)) continue;
            porNome[nome] = new ErpGpu
            {
                Nome = nome,
                Dedicada = EhDedicada(nome, null),
            };
        }

        return porNome.Values
            .OrderByDescending(g => Prioridade(g.Nome))
            .ThenByDescending(g => g.VramMb ?? 0)
            .ThenBy(g => g.Nome, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Texto com todas as placas separadas por " + ". Se não couber no limite do
    /// campo, corta as últimas (as integradas, já que a ordem põe a dedicada na
    /// frente) em vez de truncar no meio de um nome.
    /// </summary>
    public static string? Texto(IReadOnlyList<ErpGpu> gpus)
    {
        if (gpus.Count == 0) return null;

        var nomes = gpus.Select(g => g.Nome).ToList();
        while (nomes.Count > 1 && string.Join(" + ", nomes).Length > GpuTextoMax)
        {
            nomes.RemoveAt(nomes.Count - 1);
        }

        var texto = string.Join(" + ", nomes);
        return texto.Length > GpuTextoMax ? texto[..GpuTextoMax] : texto;
    }
}
