namespace NotebookCheck.Domain.Models;

/// <summary>
/// Item da inspeção física que requer foto, capturada pelo celular via QR code.
/// Cada item tem uma chave estável, um rótulo amigável e uma instrução do que
/// fotografar. A foto é armazenada como base64 (JPEG) no checklist atual,
/// vinculada ao serial da máquina.
/// </summary>
/// <param name="Key">Chave estável em snake_case (ex.: "carcaca_superior").</param>
/// <param name="Label">Rótulo curto exibido na UI.</param>
/// <param name="Instruction">Instrução do que o técnico deve fotografar.</param>
/// <param name="Optional">true = slot extra de defeito (enviado só se houver).</param>
public record InspectionPhotoItem(string Key, string Label, string Instruction, bool Optional = false);

/// <summary>
/// Foto capturada para um item de inspeção. <see cref="ImageBase64"/> contém o
/// JPEG em base64 (sem o prefixo data URI). <see cref="Note"/> é opcional.
/// </summary>
public record InspectionPhoto(
    string ItemKey,
    string ImageBase64,
    string? Note,
    DateTime CapturedAt);

/// <summary>
/// Catálogo fixo dos itens de inspeção física fotográfica: 4 fotos principais
/// (visão geral) + 5 slots OPCIONAIS para registrar defeitos. Nenhuma foto é
/// obrigatória — o técnico envia as que fizerem sentido. Mantenha em sincronia
/// com web/lib/inspection-items.ts.
/// </summary>
public static class InspectionCatalog
{
    private static readonly InspectionPhotoItem[] Defects =
    {
        new("defeito_1", "Defeito 1", "Foto de um defeito encontrado (risco, trinca, mancha...). Envie só se houver.", Optional: true),
        new("defeito_2", "Defeito 2", "Foto de outro defeito encontrado. Envie só se houver.", Optional: true),
        new("defeito_3", "Defeito 3", "Foto de outro defeito encontrado. Envie só se houver.", Optional: true),
        new("defeito_4", "Defeito 4", "Foto de outro defeito encontrado. Envie só se houver.", Optional: true),
        new("defeito_5", "Defeito 5", "Foto de outro defeito encontrado. Envie só se houver.", Optional: true),
    };

    /// <summary>Catálogo de NOTEBOOK: 4 fotos principais + slots de defeito.</summary>
    public static readonly IReadOnlyList<InspectionPhotoItem> Items = new[]
    {
        new InspectionPhotoItem("carcaca_superior", "Tampa superior", "Tampa superior do notebook (logo/acabamento). Mostre arranhões ou trincas, se houver."),
        new InspectionPhotoItem("carcaca_inferior", "Tampa inferior", "Base do notebook, com parafusos e etiquetas visíveis."),
        new InspectionPhotoItem("tela", "Tela", "Tela ligada, de frente, mostrando o estado do painel (manchas, riscos, pixels)."),
        new InspectionPhotoItem("palmrest", "Palmrest (teclado e touchpad)", "Parte interna aberta: teclado, touchpad e descanso de mãos."),
    }.Concat(Defects).ToList();

    /// <summary>Catálogo de DESKTOP: 3 fotos da carcaça + slots de defeito.</summary>
    public static readonly IReadOnlyList<InspectionPhotoItem> DesktopItems = new[]
    {
        new InspectionPhotoItem("carcaca_frente", "Carcaça — frente", "Frente do gabinete (painel frontal, portas e botões)."),
        new InspectionPhotoItem("carcaca_traseira", "Carcaça — traseira", "Traseira do gabinete, mostrando as portas e conexões."),
        new InspectionPhotoItem("carcaca_lateral", "Carcaça — lateral", "Lateral do gabinete (tampa de acesso)."),
    }.Concat(Defects).ToList();

    /// <summary>Itens principais do notebook (sem os slots opcionais de defeito).</summary>
    public static IReadOnlyList<InspectionPhotoItem> MainItems { get; } =
        Items.Where(i => !i.Optional).ToList();

    /// <summary>Catálogo conforme o modo do checklist.</summary>
    public static IReadOnlyList<InspectionPhotoItem> ForMode(Enums.ChecklistMode mode) =>
        mode == Enums.ChecklistMode.Desktop ? DesktopItems : Items;

    /// <summary>Itens principais conforme o modo.</summary>
    public static IReadOnlyList<InspectionPhotoItem> MainItemsForMode(Enums.ChecklistMode mode)
    {
        var list = new List<InspectionPhotoItem>();
        foreach (var i in ForMode(mode)) if (!i.Optional) list.Add(i);
        return list;
    }

    /// <summary>Procura uma chave em AMBOS os catálogos (relatórios podem ser de qualquer modo).</summary>
    public static InspectionPhotoItem? Find(string key)
    {
        foreach (var i in Items) if (i.Key == key) return i;
        foreach (var i in DesktopItems) if (i.Key == key) return i;
        return null;
    }
}
