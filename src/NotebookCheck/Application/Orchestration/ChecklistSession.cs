using System;
using System.Collections.Generic;
using System.Linq;
using NotebookCheck.Domain.Enums;
using NotebookCheck.Domain.Models;

namespace NotebookCheck.Application.Orchestration;

/// <summary>
/// Estado mutável do checklist em memória durante a execução.
/// </summary>
public sealed class ChecklistSession
{
    public Guid TestId { get; private set; } = Guid.NewGuid();
    public DateTime StartedAt { get; } = DateTime.Now;

    public MachineInfo? Machine { get; set; }
    public IReadOnlyList<StorageInfo> Storage { get; set; } = Array.Empty<StorageInfo>();
    public BatteryInfo? Battery { get; set; }

    public Dictionary<string, TestResult> Tests { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ManualCheckItem> Manual { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fotos da inspeção física, chaveadas por <c>ItemKey</c>. Capturadas pelo
    /// celular via QR + servidor HTTP local. Vinculadas ao serial da máquina.
    /// </summary>
    public Dictionary<string, InspectionPhoto> InspectionPhotos { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Slug único desta sessão, usado na URL do QR de inspeção.</summary>
    public string InspectionSlug { get; private set; } = Guid.NewGuid().ToString("N").Substring(0, 10);

    /// <summary>
    /// Gera uma NOVA identidade (test_id + slug de inspeção) e limpa todo o
    /// estado coletado do checklist anterior. Chamado ao reiniciar o fluxo: a
    /// sessão é singleton, então sem isso um segundo checklist na mesma
    /// execução reusaria o mesmo test_id (sobrescrevendo o relatório anterior
    /// no painel), o mesmo slug (misturando fotos) e — principalmente —
    /// os resultados de <see cref="Tests"/> da sessão anterior, fazendo a
    /// grade de testes automáticos do novo checklist nascer como se já
    /// tivesse sido executada (ver SeedTestRowsAsPending).
    /// </summary>
    public void RenewIdentity()
    {
        TestId = Guid.NewGuid();
        InspectionSlug = Guid.NewGuid().ToString("N").Substring(0, 10);

        Tests.Clear();
        Manual.Clear();
        InspectionPhotos.Clear();
        Machine = null;
        Storage = Array.Empty<StorageInfo>();
        Battery = null;
        StressResult = null;
        FinalOverride = null;
        FinalOverrideReason = null;
    }

    /// <summary>
    /// Adota o test_id de um relatório EXISTENTE — usado pelo reteste "refazer
    /// por cima": o envio fará upsert sobre o relatório antigo no painel.
    /// </summary>
    public void AdoptTestId(Guid existing) => TestId = existing;

    /// <summary>
    /// Adota o token da sessão de inspeção criada no ERP como slug — assim o
    /// relatório carrega a referência certa e o ERP consegue ligar as fotos.
    /// </summary>
    public void AdoptInspectionSlug(string slug)
    {
        if (!string.IsNullOrWhiteSpace(slug)) InspectionSlug = slug;
    }

    public string TechnicianName { get; set; } = "";
    public string GeneralNotes { get; set; } = "";
    public string AssetTag { get; set; } = "";
    public ChecklistMode Mode { get; set; } = ChecklistMode.Padrao;
    public StressSnapshot? StressResult { get; set; }
    public string NtbCode { get; set; } = "";
    public string Location { get; set; } = "";
    public KeyboardBacklight KeyboardBacklight { get; set; } = KeyboardBacklight.Indisponivel;
    public KeyboardBacklight KeyboardBacklightDetected { get; set; } = KeyboardBacklight.Indisponivel;

    /// <summary>Se o equipamento possui teclado numérico (numpad). null = não informado.</summary>
    public bool? HasNumericKeypad { get; set; }

    /// <summary>Se a tela é sensível ao toque (touchscreen). null = não informado.</summary>
    public bool? HasTouchScreen { get; set; }

    public FinalClassification? FinalOverride { get; set; }
    public string? FinalOverrideReason { get; set; }

    public ChecklistReport BuildReport(FinalClassification finalClass)
    {
        var machine = (Machine ?? throw new InvalidOperationException("MachineInfo não coletado")) with
        {
            NtbCode = NtbCode,
            Location = Location,
            KeyboardBacklight = KeyboardBacklight,
            KeyboardBacklightDetected = KeyboardBacklightDetected,
        };

        return new ChecklistReport(
            TestId: TestId,
            TestedAt: DateTime.Now,
            TechnicianName: TechnicianName,
            Machine: machine,
            Storage: Storage,
            Battery: Battery,
            Tests: new Dictionary<string, TestResult>(Tests),
            ManualChecklist: new Dictionary<string, ManualCheckItem>(Manual),
            GeneralNotes: GeneralNotes,
            AssetTag: AssetTag,
            FinalClassification: finalClass,
            FinalClassificationOverrideReason: FinalOverrideReason,
            Mode: Mode,
            Stress: StressResult,
            InspectionPhotos: InspectionPhotos.Values.ToList(),
            InspectionSlug: InspectionSlug,
            HasNumericKeypad: HasNumericKeypad,
            HasTouchScreen: HasTouchScreen);
    }
}
