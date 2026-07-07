namespace NotebookCheck.Domain.Enums;

/// <summary>
/// Modo de execução do checklist, selecionado pelo técnico no início.
/// </summary>
public enum ChecklistMode
{
    /// <summary>Apenas testes essenciais. Sem stress, sem humanização.</summary>
    Basico = 0,

    /// <summary>Testes essenciais + humanização de 12h. Sem stress.</summary>
    Padrao = 1,

    /// <summary>Tudo: stress test e humanização de 12h.</summary>
    Detalhado = 2,

    /// <summary>
    /// Checklist de DESKTOP (sem bateria): hardware sem leitura de bateria,
    /// testes automáticos reduzidos (som, internet e portas USB) e inspeção
    /// física com 3 fotos da carcaça + 1 interna.
    /// </summary>
    Desktop = 3,
}
