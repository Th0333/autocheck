using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NotebookCheck.Domain.Abstractions;
using NotebookCheck.Domain.Enums;
using NotebookCheck.Domain.Models;
using NotebookCheck.Domain.Rules;

namespace NotebookCheck.Application.Orchestration;

/// <summary>
/// Implementação do <see cref="IRetestController"/> para o fluxo pós-reparo.
/// </summary>
public sealed class RetestController : IRetestController
{
    private readonly IHardwareCollector _collector;
    private readonly ITestEngine _engine;
    private readonly ILogger<RetestController> _logger;

    public RetestController(IHardwareCollector collector, ITestEngine engine, ILogger<RetestController> logger)
    {
        _collector = collector;
        _engine = engine;
        _logger = logger;
    }

    public async Task<RetestReport> RunAsync(
        IReadOnlyList<ComponentId> selected,
        string repairNotes,
        IProgress<RetestProgress> progress,
        CancellationToken ct)
    {
        if (selected is null || selected.Count == 0)
        {
            throw new ArgumentException("Selecione ao menos um componente para retestar", nameof(selected));
        }

        MachineInfo machine;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            machine = await _collector.CollectMachineAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Coleta silenciosa falhou no reteste — usando SEMSERIAL");
            machine = new MachineInfo(
                Manufacturer: null, Model: null, Serial: null, Hostname: Environment.MachineName,
                Cpu: null, RamGb: 0,
                Os: "Indisponível", OsVersion: "Indisponível", MacAddress: null,
                Tpm: AvailabilityFlag.Indisponivel, TpmVersion: null,
                SecureBoot: AvailabilityFlag.Indisponivel,
                Autopilot: AvailabilityFlag.Indisponivel,
                WindowsActivation: AvailabilityFlag.Indisponivel,
                ScreenResolution: "Indisponível", GraphicsAdapter: null, CpuTemperatureC: null,
                NtbCode: "", Location: "",
                KeyboardBacklight: KeyboardBacklight.Indisponivel,
                KeyboardBacklightDetected: KeyboardBacklight.Indisponivel,
                CollectedAt: DateTime.Now);
        }

        IReadOnlyList<StorageInfo> storage = Array.Empty<StorageInfo>();
        BatteryInfo? battery = null;
        try { storage = await _collector.CollectStorageAsync(ct).ConfigureAwait(false); } catch { }
        try { battery = await _collector.CollectBatteryAsync(ct).ConfigureAwait(false); } catch { }

        var results = new Dictionary<string, TestResult>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        foreach (var c in selected)
        {
            i++;
            progress?.Report(new RetestProgress(i, selected.Count, c));
            ct.ThrowIfCancellationRequested();

            var key = ComponentTestMap.GetTestKey(c);
            TestResult result = c switch
            {
                ComponentId.Ram => await _engine.RunRamAsync(machine, ct).ConfigureAwait(false),
                ComponentId.Armazenamento => await _engine.RunStorageAsync(storage, ct).ConfigureAwait(false),
                ComponentId.Bateria => await _engine.RunBatteryAsync(battery, ct).ConfigureAwait(false),
                ComponentId.Carregador => await _engine.RunChargerAsync(ct).ConfigureAwait(false),
                ComponentId.Hdmi => await _engine.RunHdmiAsync(ct).ConfigureAwait(false),
                ComponentId.Wifi => await _engine.RunWifiAsync(ct).ConfigureAwait(false),
                ComponentId.Bluetooth => await _engine.RunBluetoothAsync(ct).ConfigureAwait(false),
                ComponentId.Audio => await _engine.RunAudioAsync(ct).ConfigureAwait(false),
                ComponentId.Microfone => await _engine.RunMicrophoneAsync(5, ct).ConfigureAwait(false),
                _ => new TestResult(key, AutoStatus.NaoTestado, "Reteste manual neste componente", DateTime.Now),
            };
            results[key] = result;
        }

        progress?.Report(new RetestProgress(selected.Count, selected.Count, null));

        var finalClass = DomainRules.ClassifyFinal(
            results.Values.Select(t => t.Status),
            Array.Empty<ManualStatus>());

        return new RetestReport(
            TestId: Guid.NewGuid(),
            TestedAt: DateTime.Now,
            TechnicianName: "",
            Machine: machine,
            RetestedComponents: selected.ToList(),
            RepairNotes: repairNotes ?? "",
            Tests: results,
            FinalClassification: finalClass);
    }
}
