using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NotebookCheck.Application.Testing;
using NotebookCheck.Bootstrap;
using NotebookCheck.Infrastructure.Abstractions;
using NotebookCheck.Infrastructure.Hardware;

namespace NotebookCheck.Presentation;

/// <summary>Um pente de memória instalado (WMI Win32_PhysicalMemory).</summary>
public sealed record MemoriaModulo(
    string Slot, string Capacidade, string Velocidade,
    string Fabricante, string PartNumber, string Serial)
{
    public string Display => $"{Slot} · {Capacidade} · {Velocidade} · {Fabricante} {PartNumber}";
}

/// <summary>Linha de um disco no painel de SSD (CrystalDiskInfo).</summary>
public sealed record DiscoLinha(
    string Modelo, string Tamanho, string Saude, string Detalhe, string Tone);

/// <summary>
/// ViewModel dos testes avulsos de componentes — memória (QuickMemoryTestOK),
/// SSD (CrystalDiskInfo/SMART) e bateria com perícia de recondicionada
/// (<see cref="BatteryForensics"/>). Independente do checklist e da fila do
/// ERP: serve para testar peças na bancada e gerar um laudo local em JSON.
/// </summary>
public sealed partial class TesteComponentesViewModel : ObservableObject
{
    private readonly QuickMemoryTestRunner _qmt;
    private readonly CrystalDiskInfoRunner _cdi;
    private readonly IWmiQueryRunner _wmi;
    private readonly Infrastructure.Erp.ErpClient _erp;
    private readonly ILogger<TesteComponentesViewModel> _logger;

    private CancellationTokenSource? _descargaCts;

    public TesteComponentesViewModel(
        QuickMemoryTestRunner qmt,
        CrystalDiskInfoRunner cdi,
        IWmiQueryRunner wmi,
        Infrastructure.Erp.ErpClient erp,
        ILogger<TesteComponentesViewModel> logger)
    {
        _qmt = qmt;
        _cdi = cdi;
        _wmi = wmi;
        _erp = erp;
        _logger = logger;
    }

    /// <summary>Carga inicial: lista os pentes de memória instalados.</summary>
    public async Task InitializeAsync()
    {
        await CarregarMemoriaAsync();
    }

    public void Cleanup()
    {
        _descargaCts?.Cancel();
    }

    // ------------------------------------------------------------- memória ---

    public ObservableCollection<MemoriaModulo> Modulos { get; } = new();

    [ObservableProperty] private string memoriaInfo = "";
    [ObservableProperty] private string memoriaStatus = "";
    [ObservableProperty] private bool memoriaAprovada;
    [ObservableProperty] private bool memoriaReprovada;
    [ObservableProperty] private string memoriaObs = "";

    private async Task CarregarMemoriaAsync()
    {
        try
        {
            var rows = await _wmi.QueryAsync(
                @"root\cimv2",
                "SELECT DeviceLocator, Capacity, ConfiguredClockSpeed, Speed, Manufacturer, PartNumber, SerialNumber FROM Win32_PhysicalMemory",
                TimeSpan.FromSeconds(10),
                CancellationToken.None);

            Modulos.Clear();
            long totalBytes = 0;
            foreach (var r in rows)
            {
                var capacity = ToLong(r.GetValueOrDefault("Capacity"));
                totalBytes += capacity ?? 0;
                var clock = ToLong(r.GetValueOrDefault("ConfiguredClockSpeed"))
                    ?? ToLong(r.GetValueOrDefault("Speed"));
                Modulos.Add(new MemoriaModulo(
                    Slot: AsText(r.GetValueOrDefault("DeviceLocator")) ?? "Slot ?",
                    Capacidade: capacity is > 0 ? $"{capacity / 1024.0 / 1024 / 1024:F0} GB" : "?",
                    Velocidade: clock is > 0 ? $"{clock} MHz" : "?",
                    Fabricante: AsText(r.GetValueOrDefault("Manufacturer")) ?? "?",
                    PartNumber: AsText(r.GetValueOrDefault("PartNumber")) ?? "",
                    Serial: AsText(r.GetValueOrDefault("SerialNumber")) ?? ""));
            }
            MemoriaInfo = Modulos.Count == 0
                ? "Nenhum pente detectado via WMI."
                : $"{Modulos.Count} pente(s), {totalBytes / 1024.0 / 1024 / 1024:F0} GB no total.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha lendo Win32_PhysicalMemory");
            MemoriaInfo = "Não foi possível listar os pentes (WMI indisponível).";
        }
    }

    [RelayCommand]
    private async Task AbrirQuickMemoryTestAsync()
    {
        try
        {
            MemoriaStatus = "Extraindo e abrindo o QuickMemoryTestOK…";
            await Task.Run(() => _qmt.LaunchGui());
            MemoriaStatus = "QuickMemoryTestOK aberto — rode o teste na ferramenta (recomendado: todos os padrões) e marque o resultado aqui quando terminar.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha abrindo QuickMemoryTestOK");
            MemoriaStatus = $"Erro ao abrir: {ex.Message}";
        }
    }

    // ----------------------------------------------------------------- SSD ---

    public ObservableCollection<DiscoLinha> Discos { get; } = new();

    [ObservableProperty] private bool lendoSmart;
    [ObservableProperty] private string ssdStatus = "";

    [RelayCommand]
    private async Task LerSmartAsync()
    {
        if (LendoSmart) return;
        LendoSmart = true;
        SsdStatus = "Lendo SMART via CrystalDiskInfo (até 40s)…";
        try
        {
            var disks = await _cdi.CollectAsync(CancellationToken.None);
            Discos.Clear();
            foreach (var d in disks)
            {
                var (saude, tone) = ClassifyDisk(d);
                var detalhes = new List<string>();
                if (d.PowerOnHours is { } h) detalhes.Add($"{h} h ligadas");
                if (d.PowerOnCount is { } c) detalhes.Add($"{c} ciclos de energia");
                if (d.HostWritesGb is { } w) detalhes.Add($"{w / 1024.0:F1} TB gravados");
                if (d.TemperatureC is { } t) detalhes.Add($"{t} °C");
                Discos.Add(new DiscoLinha(
                    Modelo: d.Model,
                    Tamanho: d.DiskSize ?? "?",
                    Saude: saude,
                    Detalhe: detalhes.Count > 0 ? string.Join(" · ", detalhes) : "—",
                    Tone: tone));
            }
            SsdStatus = disks.Count == 0
                ? "Nenhum disco retornado — tente abrir a GUI do CrystalDiskInfo."
                : $"{disks.Count} disco(s) lido(s) em {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha lendo SMART");
            SsdStatus = $"Erro na leitura: {ex.Message}";
        }
        finally
        {
            LendoSmart = false;
        }
    }

    [RelayCommand]
    private async Task AbrirCrystalDiskInfoAsync()
    {
        try
        {
            await Task.Run(() => _cdi.LaunchGui());
            SsdStatus = "CrystalDiskInfo aberto em janela própria.";
        }
        catch (Exception ex)
        {
            SsdStatus = $"Erro ao abrir: {ex.Message}";
        }
    }

    private static (string Saude, string Tone) ClassifyDisk(CrystalDiskInfoDisk d)
    {
        var label = d.HealthLabel ?? "";
        var pct = d.HealthPercent.HasValue ? $" ({d.HealthPercent}%)" : "";
        if (label.Contains("good", StringComparison.OrdinalIgnoreCase))
            return ($"Boa{pct}", "ok");
        if (label.Contains("caution", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("atenção", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("atencao", StringComparison.OrdinalIgnoreCase))
            return ($"Atenção{pct}", "warn");
        if (label.Contains("bad", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("ruim", StringComparison.OrdinalIgnoreCase))
            return ($"RUIM{pct}", "bad");
        return (string.IsNullOrWhiteSpace(label) ? "Desconhecida" : $"{label}{pct}", "warn");
    }

    // ------------------------------------------------------------- bateria ---

    public ObservableCollection<BatterySignal> BateriaSinais { get; } = new();

    [ObservableProperty] private string bateriaResumo = "";
    [ObservableProperty] private string bateriaNivel = "";
    [ObservableProperty] private string bateriaTone = "ok";
    [ObservableProperty] private bool bateriaAnalisada;

    // teste de descarga
    [ObservableProperty] private bool descargaRodando;
    [ObservableProperty] private double descargaProgresso;
    [ObservableProperty] private string descargaStatus = "";
    [ObservableProperty] private string descargaResultado = "";

    private BatteryForensicsResult? _forense;
    private BatteryForensics.DischargeVerdict? _descargaVerdict;
    private int _fullReportadoMwh;

    [RelayCommand]
    private void AnalisarBateria()
    {
        try
        {
            var baterias = BatteryDeviceReader.ReadAll();
            BateriaSinais.Clear();
            if (baterias.Count == 0)
            {
                BateriaResumo = "Nenhuma bateria detectada (desktop ou pack sem comunicação).";
                BateriaNivel = "";
                BateriaAnalisada = false;
                return;
            }

            // múltiplos packs: soma capacidades, usa strings do primeiro
            var b = baterias[0];
            var design = baterias.Sum(x => x.DesignCapacityMwh ?? 0);
            var full = baterias.Sum(x => x.FullChargeCapacityMwh ?? 0);
            var cycles = baterias.Max(x => x.CycleCount ?? 0);
            _fullReportadoMwh = full;

            var linhas = new List<string>
            {
                $"Pack: {b.Name ?? "?"} · {b.Manufacturer ?? "fabricante ?"} · {b.Chemistry ?? "química ?"}",
                $"Projeto: {design / 1000.0:F1} Wh · Plena registrada: {full / 1000.0:F1} Wh · Desgaste registrado: {(design > 0 ? (1 - (double)full / design) * 100 : 0):F1}%",
                $"Ciclos: {cycles} · Serial: {b.SerialNumber ?? "(vazio)"}",
            };
            if (b.ManufactureDate is { } md) linhas.Add($"Fabricada em: {md:dd/MM/yyyy}");
            BateriaResumo = string.Join(Environment.NewLine, linhas);

            _forense = BatteryForensics.AnalyzeStatic(
                design > 0 ? design : null,
                full > 0 ? full : null,
                cycles > 0 ? cycles : (int?)null,
                b.SerialNumber,
                b.ManufactureDate);

            foreach (var s in _forense.Sinais) BateriaSinais.Add(s);
            BateriaNivel = _forense.NivelLabel;
            BateriaTone = _forense.Nivel switch
            {
                "suspeita" => "bad",
                "atencao" => "warn",
                _ => "ok",
            };
            BateriaAnalisada = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha analisando bateria");
            BateriaResumo = $"Erro na leitura da bateria: {ex.Message}";
        }
    }

    /// <summary>
    /// Teste de descarga real (5 min sob carga de CPU): o contador de coulomb
    /// mede a energia realmente consumida; se o percentual derreter muito mais
    /// rápido que a energia medida, o registro de capacidade está inflado —
    /// assinatura de pack resetado. Também mede afundamento de tensão sob
    /// carga (célula gasta afunda mais).
    /// </summary>
    [RelayCommand]
    private async Task IniciarDescargaAsync()
    {
        if (DescargaRodando) return;
        _descargaCts = new CancellationTokenSource();
        var ct = _descargaCts.Token;
        DescargaRodando = true;
        DescargaProgresso = 0;
        DescargaResultado = "";
        var loadCts = new CancellationTokenSource();
        try
        {
            // precisa estar NA BATERIA
            var amostra = LerAgregado();
            if (amostra is null)
            {
                DescargaStatus = "Sem bateria legível — teste indisponível.";
                return;
            }
            var espera = DateTime.UtcNow;
            while (amostra!.Value.OnLine)
            {
                DescargaStatus = "Desconecte o carregador para começar (aguardando até 90s)…";
                if ((DateTime.UtcNow - espera).TotalSeconds > 90)
                {
                    DescargaStatus = "Carregador continua conectado — teste cancelado.";
                    return;
                }
                await Task.Delay(2000, ct);
                amostra = LerAgregado();
                if (amostra is null) return;
            }

            // tensão em repouso antes da carga
            var voltIdle = amostra.Value.VoltageMv;

            // carga de CPU (metade dos núcleos em spin — esquenta sem travar a UI)
            var nucleos = Math.Max(1, Environment.ProcessorCount / 2);
            for (var i = 0; i < nucleos; i++)
            {
                _ = Task.Run(() =>
                {
                    var x = 0.0;
                    while (!loadCts.Token.IsCancellationRequested)
                    {
                        x = Math.Sqrt(x + 1.2345) * 1.0001;
                        if (double.IsInfinity(x)) x = 0;
                    }
                }, CancellationToken.None);
            }

            const int duracaoSeg = 300; // 5 minutos
            const int passoSeg = 5;
            var inicio = amostra.Value;
            int? voltMinCarga = null;
            double maiorSaltoPp = 0;
            var pctAnterior = Percent(inicio);

            for (var t = passoSeg; t <= duracaoSeg; t += passoSeg)
            {
                await Task.Delay(TimeSpan.FromSeconds(passoSeg), ct);
                var atual = LerAgregado();
                if (atual is null) continue;
                if (atual.Value.OnLine)
                {
                    DescargaStatus = "Carregador reconectado — teste interrompido.";
                    return;
                }
                if (atual.Value.VoltageMv is { } v)
                {
                    voltMinCarga = voltMinCarga is null ? v : Math.Min(voltMinCarga.Value, v);
                }
                var pctAtual = Percent(atual.Value);
                if (pctAnterior is { } pa && pctAtual is { } pb)
                {
                    maiorSaltoPp = Math.Max(maiorSaltoPp, pa - pb);
                }
                pctAnterior = pctAtual;
                DescargaProgresso = t * 100.0 / duracaoSeg;
                DescargaStatus = $"Descarregando sob carga… {t / 60}:{t % 60:D2} de 5:00 · {pctAtual:F1}% · {(atual.Value.RateMw ?? 0) / -1000.0:F1} W";

                if (t == duracaoSeg)
                {
                    ConcluirDescarga(inicio, atual.Value, voltIdle, voltMinCarga, maiorSaltoPp);
                }
            }
        }
        catch (OperationCanceledException)
        {
            DescargaStatus = "Teste de descarga cancelado.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha no teste de descarga");
            DescargaStatus = $"Erro: {ex.Message}";
        }
        finally
        {
            loadCts.Cancel();
            DescargaRodando = false;
        }
    }

    [RelayCommand]
    private void CancelarDescarga() => _descargaCts?.Cancel();

    private void ConcluirDescarga(
        (bool OnLine, int? RemainingMwh, int? FullMwh, int? VoltageMv, int? RateMw) inicio,
        (bool OnLine, int? RemainingMwh, int? FullMwh, int? VoltageMv, int? RateMw) fim,
        int? voltIdle, int? voltMinCarga, double maiorSaltoPp)
    {
        var consumo = (inicio.RemainingMwh ?? 0) - (fim.RemainingMwh ?? 0);
        var quedaPct = (Percent(inicio) ?? 0) - (Percent(fim) ?? 0);
        var extras = new List<string>();

        _descargaVerdict = BatteryForensics.EvaluateDischarge(_fullReportadoMwh, consumo, quedaPct);
        var suspeita = _descargaVerdict?.Suspeita == true;

        if (voltIdle is { } vi && voltMinCarga is { } vc && vi > 0)
        {
            var sagPct = (vi - vc) * 100.0 / vi;
            if (sagPct >= 8)
            {
                extras.Add($"Afundamento de tensão sob carga de {sagPct:F1}% ({vi} → {vc} mV) — resistência interna alta, célula gasta.");
                suspeita = true;
            }
        }
        if (maiorSaltoPp >= 3)
        {
            extras.Add($"Percentual caiu em saltos de até {maiorSaltoPp:F1} p.p. entre leituras — gauge sem calibração (comum após reset).");
            suspeita = true;
        }

        var partes = new List<string>();
        if (_descargaVerdict is { } v) partes.Add(v.Resumo);
        else partes.Add($"Amostra insuficiente para extrapolar (consumo {consumo} mWh, queda {quedaPct:F1} p.p.) — rode com mais carga de bateria.");
        partes.AddRange(extras);
        DescargaResultado = string.Join(Environment.NewLine, partes);
        DescargaStatus = "Teste de descarga concluído.";

        if (suspeita)
        {
            BateriaNivel = "SUSPEITA DE RECONDICIONADA";
            BateriaTone = "bad";
            BateriaSinais.Add(new BatterySignal(
                "Descarga real divergente do registro",
                DescargaResultado,
                Forte: true));
        }
        else if (BateriaTone == "ok")
        {
            BateriaNivel = "Descarga compatível com o registro — sem indícios";
        }
    }

    private static (bool OnLine, int? RemainingMwh, int? FullMwh, int? VoltageMv, int? RateMw)? LerAgregado()
    {
        var baterias = BatteryDeviceReader.ReadAll();
        if (baterias.Count == 0) return null;
        return (
            OnLine: baterias.Any(b => b.OnLine),
            RemainingMwh: baterias.Sum(b => b.RemainingCapacityMwh ?? 0),
            FullMwh: baterias.Sum(b => b.FullChargeCapacityMwh ?? 0),
            VoltageMv: baterias.Select(b => b.VoltageMv).FirstOrDefault(v => v is > 0),
            RateMw: baterias.Sum(b => b.RateMw ?? 0));
    }

    private static double? Percent((bool OnLine, int? RemainingMwh, int? FullMwh, int? VoltageMv, int? RateMw) s)
        => s.FullMwh is > 0 && s.RemainingMwh is { } r ? r * 100.0 / s.FullMwh.Value : null;

    // ------------------------------------------------------------- relatório --

    [ObservableProperty] private string salvarStatus = "";

    /// <summary>Resultado consolidado do laudo (o pior componente manda).</summary>
    private string ResultadoConsolidado()
    {
        if (MemoriaReprovada || BateriaTone == "bad" || Discos.Any(d => d.Tone == "bad"))
            return "reprovado";
        if (MemoriaAprovada) return "aprovado";
        return BateriaAnalisada || Discos.Count > 0 ? "atencao" : "nao_testado";
    }

    private string ResumoConsolidado()
    {
        var partes = new List<string>();
        if (Modulos.Count > 0)
        {
            var caps = string.Join("+", Modulos.Select(m => m.Capacidade.Replace(" GB", "")));
            partes.Add($"{Modulos.Count}× RAM {caps} GB");
        }
        if (Discos.Count > 0) partes.Add($"{Discos.Count} disco(s)");
        if (!string.IsNullOrWhiteSpace(BateriaNivel)) partes.Add($"bateria: {BateriaNivel.ToLowerInvariant()}");
        return partes.Count > 0 ? string.Join(" · ", partes) : "laudo de componentes";
    }

    /// <summary>
    /// Envia o laudo para o ERP (aba Checklists → Componentes, de onde pode
    /// virar entrada no estoque de produtos) e guarda uma cópia local em JSON.
    /// </summary>
    [RelayCommand]
    private async Task SalvarRelatorioAsync()
    {
        try
        {
            var relatorio = new
            {
                tipo = "completo",
                resumo = ResumoConsolidado(),
                resultado = ResultadoConsolidado(),
                gerado_em = DateTime.Now,
                memoria = new
                {
                    modulos = Modulos.Select(m => new { m.Slot, m.Capacidade, m.Velocidade, m.Fabricante, m.PartNumber, m.Serial }),
                    resultado = MemoriaAprovada ? "aprovado" : MemoriaReprovada ? "reprovado" : "nao_testado",
                    observacoes = MemoriaObs,
                },
                ssd = new
                {
                    discos = Discos.Select(d => new { d.Modelo, d.Tamanho, d.Saude, d.Detalhe }),
                },
                bateria = new
                {
                    resumo = BateriaResumo,
                    nivel = BateriaNivel,
                    sinais = BateriaSinais.Select(s => new { s.Titulo, s.Detalhe, s.Forte }),
                    descarga = DescargaResultado,
                },
            };

            // cópia local sempre (pendrive leva o histórico mesmo sem rede)
            var json = System.Text.Json.JsonSerializer.Serialize(relatorio,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var dir = AppPaths.ResolveWritable(Path.Combine(AppPaths.ExeDir, "componentes"));
            var arquivo = Path.Combine(dir, $"COMPONENTES_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            await File.WriteAllTextAsync(arquivo, json);

            SalvarStatus = "Enviando laudo ao ERP…";
            try
            {
                await _erp.SendComponentCheckAsync(relatorio, Guid.NewGuid().ToString(), CancellationToken.None);
                SalvarStatus = $"Laudo enviado ao ERP (Checklists → Componentes) — cópia local em {arquivo}";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Laudo de componentes não subiu pro ERP");
                SalvarStatus = $"ERP indisponível ({ex.Message}) — laudo salvo localmente em {arquivo}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha gerando laudo de componentes");
            SalvarStatus = $"Erro ao gerar o laudo: {ex.Message}";
        }
    }

    private static long? ToLong(object? v)
        => v is null ? null : long.TryParse(v.ToString(), out var n) ? n : null;

    private static string? AsText(object? v)
    {
        var s = v?.ToString()?.Trim();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
