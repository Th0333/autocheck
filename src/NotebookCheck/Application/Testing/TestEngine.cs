using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NotebookCheck.Domain.Abstractions;
using NotebookCheck.Domain.Enums;
using NotebookCheck.Domain.Models;
using NotebookCheck.Domain.Rules;
using NotebookCheck.Infrastructure.Abstractions;
using NotebookCheck.Infrastructure.Wmi;

namespace NotebookCheck.Application.Testing;

/// <summary>
/// Implementação do <see cref="ITestEngine"/>. Cada teste é envelopado em
/// <see cref="TryRunAsync"/> que captura exceções e devolve <see cref="TestResult"/>
/// com Falha em vez de propagar (Requirement 26).
/// </summary>
public sealed class TestEngine : ITestEngine
{
    private readonly IPowerStatusProvider _power;
    private readonly IDisplayEnumerator _displays;
    private readonly IPowerShellRunner _ps;
    private readonly IWmiQueryRunner _wmi;
    private readonly INetworkProbe _probe;
    private readonly ILogger<TestEngine> _logger;

    public TestEngine(
        IPowerStatusProvider power,
        IDisplayEnumerator displays,
        IPowerShellRunner ps,
        IWmiQueryRunner wmi,
        INetworkProbe probe,
        ILogger<TestEngine> logger)
    {
        _power = power;
        _displays = displays;
        _ps = ps;
        _wmi = wmi;
        _probe = probe;
        _logger = logger;
    }

    public Task<TestResult> RunRamAsync(MachineInfo m, CancellationToken ct) =>
        TryRunAsync("ram", async () =>
        {
            await Task.Yield();
            if (m.RamGb <= 0)
            {
                return new TestResult("ram", AutoStatus.Falha, "RAM não detectada", DateTime.Now);
            }
            return new TestResult("ram", AutoStatus.OK, $"{m.RamGb:F1} GB", DateTime.Now);
        });

    public Task<TestResult> RunStorageAsync(IReadOnlyList<StorageInfo> s, CancellationToken ct) =>
        TryRunAsync("armazenamento", async () =>
        {
            await Task.Yield();
            if (s.Count == 0)
            {
                return new TestResult("armazenamento", AutoStatus.Falha, "Ausência de discos", DateTime.Now);
            }
            var perDisk = s.Select(d => MapSmartToAuto(d.SmartStatus)).ToArray();
            var worst = DomainRules.WorstOf(perDisk);
            var details = string.Join("; ", s.Select(d => $"#{d.Index} {d.Type} {d.CapacityGb:F1}GB SMART={d.SmartStatus}"));
            return new TestResult("armazenamento", worst, details, DateTime.Now);
        });

    public Task<TestResult> RunStorageHealthAsync(IReadOnlyList<StorageInfo> s, CancellationToken ct) =>
        TryRunAsync("saude_disco", async () =>
        {
            if (s.Count == 0)
            {
                return new TestResult("saude_disco", AutoStatus.NaoAplicavel, "Sem discos para avaliar", DateTime.Now);
            }

            // Status SMART simples vindo do collector — usado como base se nada melhor existir.
            var smartStatuses = s.Select(d => MapSmartToAuto(d.SmartStatus)).ToArray();

            // Saúde detalhada via MSFT_PhysicalDisk + MSFT_StorageReliabilityCounter
            var diskDetails = new List<string>();
            var perDiskStatus = new List<AutoStatus>();
            var hadAnyData = false;

            try
            {
                var pdRows = await _wmi.QueryAsync(
                    "root\\Microsoft\\Windows\\Storage",
                    "SELECT DeviceId, FriendlyName, HealthStatus, OperationalStatus, MediaType FROM MSFT_PhysicalDisk",
                    TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);

                IReadOnlyList<IReadOnlyDictionary<string, object?>> relRows;
                try
                {
                    relRows = await _wmi.QueryAsync(
                        "root\\Microsoft\\Windows\\Storage",
                        "SELECT DeviceId, Wear, Temperature, ReadErrorsTotal, WriteErrorsTotal, PowerOnHours FROM MSFT_StorageReliabilityCounter",
                        TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
                }
                catch
                {
                    relRows = Array.Empty<IReadOnlyDictionary<string, object?>>();
                }

                var relByDevice = new Dictionary<int, IReadOnlyDictionary<string, object?>>();
                foreach (var row in relRows)
                {
                    var did = row.GetInt("DeviceId");
                    if (did.HasValue) relByDevice[did.Value] = row;
                }

                foreach (var row in pdRows)
                {
                    hadAnyData = true;
                    var did = row.GetInt("DeviceId");
                    var name = row.GetString("FriendlyName") ?? "Disco";
                    var health = row.GetInt("HealthStatus") ?? 3;
                    var (healthLabel, healthStatus) = health switch
                    {
                        0 => ("Saudável", AutoStatus.OK),
                        1 => ("Aviso", AutoStatus.Atencao),
                        2 => ("Crítico", AutoStatus.Falha),
                        _ => ("Desconhecido", AutoStatus.NaoTestado),
                    };

                    // Status base do disco vem de HealthStatus.
                    var diskStatus = healthStatus;

                    var pieces = new List<string> { name, healthLabel };

                    if (did.HasValue && relByDevice.TryGetValue(did.Value, out var rel))
                    {
                        var wear = rel.GetInt("Wear");
                        if (wear is >= 0 and <= 100)
                        {
                            var lifeRemaining = 100 - wear.Value;
                            pieces.Add($"vida útil {lifeRemaining}%");
                            if (lifeRemaining <= 10) diskStatus = DomainRules.WorstOf(diskStatus, AutoStatus.Falha);
                            else if (lifeRemaining <= 30) diskStatus = DomainRules.WorstOf(diskStatus, AutoStatus.Atencao);
                        }
                        var temp = rel.GetInt("Temperature");
                        if (temp is > 0)
                        {
                            pieces.Add($"{temp}°C");
                            if (temp >= 70) diskStatus = DomainRules.WorstOf(diskStatus, AutoStatus.Atencao);
                        }
                        var readErr = rel.GetLong("ReadErrorsTotal") ?? 0;
                        var writeErr = rel.GetLong("WriteErrorsTotal") ?? 0;
                        if (readErr + writeErr > 0)
                        {
                            pieces.Add($"{readErr + writeErr} erros I/O");
                            diskStatus = DomainRules.WorstOf(diskStatus, AutoStatus.Atencao);
                        }
                        var hours = rel.GetLong("PowerOnHours");
                        if (hours.HasValue && hours.Value > 0)
                        {
                            pieces.Add($"{hours.Value}h ligado");
                        }
                    }

                    perDiskStatus.Add(diskStatus);
                    diskDetails.Add(string.Join(" • ", pieces));
                }
            }
            catch (WmiQueryException ex)
            {
                _logger.LogDebug(ex, "MSFT_PhysicalDisk indisponível");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Falha consultando saúde do disco");
            }

            // Determina status final:
            //  - Se MSFT_PhysicalDisk respondeu, usamos esses dados como verdade.
            //  - Senão, caímos para o status SMART simples (que pelo menos sinaliza
            //    discos com falha iminente).
            AutoStatus finalStatus;
            string detailsText;

            if (hadAnyData && perDiskStatus.Count > 0)
            {
                finalStatus = DomainRules.WorstOf(perDiskStatus.ToArray());
                detailsText = string.Join("; ", diskDetails);
            }
            else if (smartStatuses.All(st => st == AutoStatus.OK))
            {
                finalStatus = AutoStatus.OK;
                detailsText = $"SMART OK em {s.Count} disco(s); contadores detalhados indisponíveis";
            }
            else if (smartStatuses.Any(st => st != AutoStatus.NaoTestado))
            {
                finalStatus = DomainRules.WorstOf(smartStatuses);
                detailsText = $"SMART {finalStatus} em {s.Count} disco(s); contadores detalhados indisponíveis";
            }
            else
            {
                finalStatus = AutoStatus.NaoTestado;
                detailsText = $"Nenhuma fonte de saúde disponível para {s.Count} disco(s)";
            }

            return new TestResult("saude_disco", finalStatus, detailsText, DateTime.Now);
        });

    public Task<TestResult> RunBatteryAsync(BatteryInfo? b, CancellationToken ct) =>
        TryRunAsync("bateria", async () =>
        {
            await Task.Yield();
            if (b is null)
            {
                return new TestResult("bateria", AutoStatus.NaoAplicavel, "Sem bateria detectada", DateTime.Now);
            }
            if (b.DesignCapacityMwh is not > 0 || b.FullChargeCapacityMwh is null)
            {
                return new TestResult("bateria", AutoStatus.NaoTestado, "Capacidades indisponíveis", DateTime.Now);
            }
            if (b.FullChargeCapacityMwh.Value > b.DesignCapacityMwh.Value)
            {
                return new TestResult("bateria", AutoStatus.Atencao, "Anomalia: capacidade máxima maior que a de design", DateTime.Now);
            }
            var wear = DomainRules.ComputeWear(b.DesignCapacityMwh.Value, b.FullChargeCapacityMwh.Value);
            var status = DomainRules.ClassifyBatteryWear(wear);
            return new TestResult("bateria", status, $"Desgaste {wear:F2}%, ciclos {b.CycleCount?.ToString() ?? "n/d"}", DateTime.Now);
        });

    public Task<TestResult> RunChargerAsync(CancellationToken ct) =>
        TryRunAsync("carregador", async () =>
        {
            await Task.Yield();
            var s = _power.GetStatus();
            return s.AcLine switch
            {
                AcLineStatus.Online => new TestResult("carregador", AutoStatus.OK, "AC conectado", DateTime.Now),
                AcLineStatus.Offline => new TestResult("carregador", AutoStatus.Falha, "AC desconectado", DateTime.Now),
                _ => new TestResult("carregador", AutoStatus.NaoTestado, "Status indisponível", DateTime.Now),
            };
        });

    public Task<TestResult> RunHdmiAsync(CancellationToken ct) =>
        TryRunAsync("hdmi", async () =>
        {
            await Task.Yield();
            var monitors = _displays.EnumerateMonitors();
            var external = monitors.Count(m => m.Kind == MonitorKind.External);
            if (external > 0)
            {
                return new TestResult("hdmi", AutoStatus.OK, $"{external} monitor(es) externo(s) detectado(s)", DateTime.Now);
            }
            return new TestResult("hdmi", AutoStatus.NaoTestado, "Conecte um monitor externo para testar", DateTime.Now);
        });

    public Task<TestResult> RunWifiAsync(CancellationToken ct) =>
        TryRunAsync("wifi", async () =>
        {
            try
            {
                // WMI primeiro: Win32_NetworkAdapter com filtros físicos.
                var rows = await _wmi.QueryAsync(
                    "root\\cimv2",
                    "SELECT Name, NetEnabled, NetConnectionStatus, AdapterTypeID, MACAddress FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE",
                    TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);

                var wifi = rows.Where(r =>
                {
                    var name = r.GetString("Name") ?? "";
                    var typeId = r.GetInt("AdapterTypeID");
                    return typeId == 7
                        || name.IndexOf("Wi-Fi", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("Wireless", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("WLAN", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("802.11", StringComparison.OrdinalIgnoreCase) >= 0;
                }).ToList();

                if (wifi.Count == 0)
                {
                    return new TestResult("wifi", AutoStatus.Falha, "Nenhum adaptador Wi-Fi detectado", DateTime.Now);
                }

                var enabledCount = wifi.Count(r => (r.GetBool("NetEnabled") ?? false));
                var status = enabledCount > 0 ? AutoStatus.OK : AutoStatus.Atencao;
                var first = wifi[0].GetString("Name") ?? "Wi-Fi";
                var detail = $"{wifi.Count} adaptador(es) Wi-Fi — {first}";
                if (wifi.Count > 1) detail += $" + {wifi.Count - 1} outro(s)";
                detail += enabledCount > 0 ? " (habilitado)" : " (desabilitado)";

                // Enriquecimento: bandas suportadas + sinal da rede atual (netsh).
                var radio = await TryGetWifiRadioAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(radio)) detail += $" • {radio}";
                return new TestResult("wifi", status, detail, DateTime.Now);
            }
            catch (Exception ex)
            {
                return new TestResult("wifi", AutoStatus.Falha, $"Erro: {ex.Message}", DateTime.Now);
            }
        });

    /// <summary>
    /// Lê via netsh as bandas suportadas (2.4/5/6 GHz, derivadas dos tipos de
    /// rádio 802.11) e o sinal da rede conectada. Falhas são silenciosas.
    /// </summary>
    private async Task<string?> TryGetWifiRadioAsync(CancellationToken ct)
    {
        try
        {
            const string script = @"
$ErrorActionPreference = 'SilentlyContinue'
$drv = (netsh wlan show drivers) | Out-String
$ifc = (netsh wlan show interfaces) | Out-String
$radio = ''
if ($drv -match 'Radio types supported\s*:\s*(.+)') { $radio = $matches[1].Trim() }
elseif ($drv -match 'Tipos de r.dio compat.veis\s*:\s*(.+)') { $radio = $matches[1].Trim() }
$signal = ''
if ($ifc -match 'Signal\s*:\s*(\d+%)') { $signal = $matches[1] }
elseif ($ifc -match 'Sinal\s*:\s*(\d+%)') { $signal = $matches[1] }
[pscustomobject]@{ Radio = $radio; Signal = $signal }
";
            var rows = await _ps.InvokeAsync(script, null, TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            if (rows.Count == 0) return null;
            var radio = rows[0].GetString("Radio") ?? "";
            var signal = rows[0].GetString("Signal") ?? "";

            var bands = new List<string>();
            // 802.11ax/be → 6 GHz possível; ac/ax/be → 5 GHz; b/g/n → 2.4 GHz.
            if (radio.IndexOf("802.11be", StringComparison.OrdinalIgnoreCase) >= 0
                || radio.IndexOf("802.11ax", StringComparison.OrdinalIgnoreCase) >= 0) bands.Add("6 GHz");
            if (radio.IndexOf("802.11a", StringComparison.OrdinalIgnoreCase) >= 0
                || radio.IndexOf("802.11ac", StringComparison.OrdinalIgnoreCase) >= 0
                || radio.IndexOf("802.11ax", StringComparison.OrdinalIgnoreCase) >= 0
                || radio.IndexOf("802.11be", StringComparison.OrdinalIgnoreCase) >= 0) bands.Add("5 GHz");
            if (radio.IndexOf("802.11b", StringComparison.OrdinalIgnoreCase) >= 0
                || radio.IndexOf("802.11g", StringComparison.OrdinalIgnoreCase) >= 0
                || radio.IndexOf("802.11n", StringComparison.OrdinalIgnoreCase) >= 0) bands.Add("2.4 GHz");

            var bandsText = bands.Count > 0 ? string.Join("/", bands.Distinct()) : null;
            var pieces = new List<string>();
            if (bandsText is not null) pieces.Add(bandsText);
            if (!string.IsNullOrEmpty(signal)) pieces.Add($"sinal {signal}");
            return pieces.Count > 0 ? string.Join(", ", pieces) : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "netsh wlan falhou");
            return null;
        }
    }

    public Task<TestResult> RunBluetoothAsync(CancellationToken ct) =>
        TryRunAsync("bluetooth", async () =>
        {
            try
            {
                // Caminho 1: Win32_PnPEntity classe Bluetooth (sem PowerShell, sem módulos opcionais).
                var rows = await _wmi.QueryAsync(
                    "root\\cimv2",
                    "SELECT Name, Status, DeviceID, Service FROM Win32_PnPEntity WHERE PNPClass = 'Bluetooth'",
                    TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);

                if (rows.Count == 0)
                {
                    return new TestResult("bluetooth", AutoStatus.Falha, "Nenhum dispositivo Bluetooth presente", DateTime.Now);
                }

                // Filtra rádios/hosts: serviços BTHUSB ou BthEnum, ou nome com "radio/adapter/host".
                var radios = rows.Where(r =>
                {
                    var svc = r.GetString("Service") ?? "";
                    var name = r.GetString("Name") ?? "";
                    return string.Equals(svc, "BTHUSB", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(svc, "BthEnum", StringComparison.OrdinalIgnoreCase)
                        || name.IndexOf("radio", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("adapter", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("host", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("controller", StringComparison.OrdinalIgnoreCase) >= 0;
                }).ToList();

                // Se nenhum item bate o filtro de "rádio" mas existe Bluetooth, usa o primeiro
                if (radios.Count == 0) radios = rows.Take(1).ToList();

                var first = radios[0];
                var name2 = first.GetString("Name") ?? "Bluetooth";
                var statusStr = first.GetString("Status") ?? "";
                var anyOk = radios.Any(r => string.Equals(r.GetString("Status"), "OK", StringComparison.OrdinalIgnoreCase));

                // Tenta versão via LMP. Se falhar (ex.: módulo PnpDevice ausente), segue sem versão.
                var version = await TryGetBluetoothVersionAsync(first.GetString("DeviceID"), ct).ConfigureAwait(false);

                var detail = string.IsNullOrEmpty(version)
                    ? $"{radios.Count} rádio(s) — {name2} (status {statusStr})"
                    : $"{radios.Count} rádio(s) — {name2} • Bluetooth {version}";

                return new TestResult("bluetooth", anyOk ? AutoStatus.OK : AutoStatus.Atencao, detail, DateTime.Now);
            }
            catch (Exception ex)
            {
                return new TestResult("bluetooth", AutoStatus.Falha, $"Erro: {ex.Message}", DateTime.Now);
            }
        });

    /// <summary>
    /// Tenta obter a versão LMP do rádio Bluetooth. Falhas são silenciosas:
    /// retorna string vazia para que o teste principal não quebre.
    /// </summary>
    private async Task<string> TryGetBluetoothVersionAsync(string? deviceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return "";
        try
        {
            // Get-PnpDeviceProperty é a única fonte confiável do LMP via WMI público.
            // Mantemos try/catch grosso para não derrubar o teste principal.
            const string script = @"
$ErrorActionPreference = 'SilentlyContinue'
try {
    $p = Get-PnpDeviceProperty -InstanceId $args[0] -KeyName 'DEVPKEY_Bluetooth_RadioLmpVersion' -ErrorAction Stop
    if ($p -and $p.Data -ne $null) { [pscustomobject]@{ Lmp = [int]$p.Data } }
} catch {}
";
            var rows = await _ps.InvokeAsync(
                script,
                new Dictionary<string, object?> { ["args"] = new[] { deviceId } },
                TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
            if (rows.Count > 0)
            {
                var lmp = rows[0].GetInt("Lmp");
                return MapLmp(lmp);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Versão LMP indisponível, seguindo sem");
        }
        return "";
    }

    private static string MapLmp(int? lmp) => BluetoothVersionMap.FromLmp(lmp) ?? "";

    /// <summary>
    /// Converte o status SMART do coletor para <see cref="AutoStatus"/>. Status
    /// indisponível vira <see cref="AutoStatus.NaoTestado"/> (não Falha): um disco
    /// sem fonte de SMART não deve reprovar a máquina. Usado por ambos os testes
    /// de armazenamento para evitar divergência.
    /// </summary>
    private static AutoStatus MapSmartToAuto(SmartStatus s) => s switch
    {
        SmartStatus.OK => AutoStatus.OK,
        SmartStatus.Aviso => AutoStatus.Atencao,
        SmartStatus.Falha => AutoStatus.Falha,
        _ => AutoStatus.NaoTestado,
    };

    public Task<TestResult> RunInternetAsync(string probeUrl, CancellationToken ct) =>
        TryRunAsync("internet", async () =>
        {
            var r = await _probe.ProbeAsync(probeUrl, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            return r.Outcome switch
            {
                ProbeOutcome.Success => new TestResult("internet", AutoStatus.OK, $"{r.StatusCode} em {r.ElapsedMilliseconds}ms", DateTime.Now),
                ProbeOutcome.Timeout => new TestResult("internet", AutoStatus.Falha, "Tempo limite excedido", DateTime.Now),
                ProbeOutcome.InvalidUrl => new TestResult("internet", AutoStatus.NaoTestado, "URL inválida", DateTime.Now),
                _ => new TestResult("internet", AutoStatus.Falha, r.ErrorMessage ?? r.Outcome.ToString(), DateTime.Now),
            };
        });

    public Task<TestResult> RunAudioAsync(CancellationToken ct) =>
        TryRunAsync("audio", async () =>
        {
            await Task.Yield();
            try
            {
                if (WaveOut.DeviceCount == 0)
                {
                    return new TestResult("audio", AutoStatus.NaoTestado, "Sem dispositivo de áudio", DateTime.Now);
                }

                using var output = new WaveOutEvent();
                var tone = new SineWaveProvider32 { Frequency = 1000f, Amplitude = 0.25f };
                output.Init(tone);
                output.Play();
                await Task.Delay(800, ct).ConfigureAwait(false);
                output.Stop();
                return new TestResult("audio", AutoStatus.OK, "Tom 1 kHz reproduzido", DateTime.Now);
            }
            catch (Exception ex)
            {
                return new TestResult("audio", AutoStatus.Falha, $"Erro: {ex.Message}", DateTime.Now);
            }
        });

    public Task<TestResult> RunStereoAsync(CancellationToken ct) =>
        TryRunAsync("estereo", async () =>
        {
            try
            {
                if (WaveOut.DeviceCount == 0)
                {
                    return new TestResult("estereo", AutoStatus.NaoTestado, "Sem dispositivo de áudio", DateTime.Now);
                }

                // Esquerdo: 600 Hz (tom mais grave) — Direito: 1200 Hz (tom mais agudo)
                // 1.2 segundos por canal, com leve overlap evitando "click" ao trocar.
                using var output = new WaveOutEvent();
                var stereo = new StereoTestProvider();
                output.Init(stereo);

                stereo.PlayLeftOnly(600f);
                output.Play();
                await Task.Delay(1200, ct).ConfigureAwait(false);

                stereo.PlayRightOnly(1200f);
                await Task.Delay(1200, ct).ConfigureAwait(false);

                stereo.PlayBoth(800f);
                await Task.Delay(700, ct).ConfigureAwait(false);

                output.Stop();
                return new TestResult(
                    "estereo",
                    AutoStatus.OK,
                    "Esquerdo 600 Hz, direito 1200 Hz e ambos 800 Hz reproduzidos",
                    DateTime.Now);
            }
            catch (Exception ex)
            {
                return new TestResult("estereo", AutoStatus.Falha, $"Erro: {ex.Message}", DateTime.Now);
            }
        });

    public async Task<IReadOnlyList<string>> DetectCamerasAsync(CancellationToken ct)
    {
        try
        {
            // Webcams modernas em Windows 10/11 ficam na classe "Camera". Muitas
            // câmeras antigas e laptops de marca branca aparecem como "Image".
            // Nomes "USB Video Device" ou "Integrated" são comuns. Filtra-se
            // scanners e impressoras que entram em "Image".
            var rows = await _wmi.QueryAsync(
                "root\\cimv2",
                "SELECT Name, Status, PNPClass, Service FROM Win32_PnPEntity " +
                "WHERE PNPClass = 'Camera' OR PNPClass = 'Image' OR Service = 'usbvideo' OR Service = 'KSCamera'",
                TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);

            var list = new List<string>();
            foreach (var row in rows)
            {
                var name = row.GetString("Name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (name.IndexOf("Scanner", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (name.IndexOf("Printer", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (list.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(name);
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha enumerando câmeras");
            return Array.Empty<string>();
        }
    }

    public TestResult BuildWebcamResult(bool ok, string? note)
    {
        var status = ok ? AutoStatus.OK : AutoStatus.Falha;
        var details = !string.IsNullOrWhiteSpace(note)
            ? note!
            : (ok ? "Imagem confirmada pelo técnico" : "Câmera marcada como não funcional");
        return new TestResult("camera", status, details, DateTime.Now);
    }

    public TestResult BuildPixelTestResult(int viewedScreens, int totalScreens, bool foundDefects, string? note)
    {
        AutoStatus status;
        string baseDetails;
        if (foundDefects)
        {
            status = AutoStatus.Falha;
            baseDetails = $"Pixels defeituosos reportados pelo técnico ({viewedScreens}/{totalScreens} telas vistas)";
        }
        else if (viewedScreens >= totalScreens)
        {
            status = AutoStatus.OK;
            baseDetails = $"Todas as {totalScreens} telas de cor foram inspecionadas sem defeitos";
        }
        else if (viewedScreens > 0)
        {
            status = AutoStatus.Atencao;
            baseDetails = $"Inspeção parcial: {viewedScreens}/{totalScreens} telas vistas, sem defeitos reportados";
        }
        else
        {
            status = AutoStatus.NaoTestado;
            baseDetails = "Teste de pixels não foi executado";
        }
        var details = string.IsNullOrWhiteSpace(note) ? baseDetails : $"{baseDetails} — {note}";
        return new TestResult("tela", status, details, DateTime.Now);
    }

    public Task<TestResult> RunMicrophoneAsync(int seconds, CancellationToken ct) =>
        RecordMicrophoneAsync(seconds, ct);

    /// <summary>
    /// Grava microfone por <paramref name="seconds"/> segundos e devolve o
    /// buffer WAV completo (cabeçalho + samples) junto com o resultado para
    /// que a UI possa reproduzir depois.
    /// </summary>
    public async Task<(TestResult result, byte[]? wav)> RecordMicrophoneWithBufferAsync(int seconds, CancellationToken ct)
    {
        try
        {
            if (WaveIn.DeviceCount == 0)
            {
                return (new TestResult("microfone", AutoStatus.NaoTestado, "Sem dispositivo de captura", DateTime.Now), null);
            }
            seconds = Math.Clamp(seconds, 3, 10);
            using var capture = new WaveInEvent { WaveFormat = new WaveFormat(44100, 16, 1) };
            using var ms = new MemoryStream();
            using var writer = new WaveFileWriter(ms, capture.WaveFormat);
            capture.DataAvailable += (s, a) => writer.Write(a.Buffer, 0, a.BytesRecorded);
            var tcs = new TaskCompletionSource<bool>();
            capture.RecordingStopped += (s, a) => tcs.TrySetResult(true);
            capture.StartRecording();
            await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
            capture.StopRecording();
            await tcs.Task.ConfigureAwait(false);
            writer.Flush();
            var bytes = ms.ToArray();
            var r = new TestResult("microfone", AutoStatus.OK, $"{seconds}s gravados ({bytes.Length} bytes)", DateTime.Now);
            return (r, bytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha gravando microfone");
            return (new TestResult("microfone", AutoStatus.Falha, $"Erro: {ex.Message}", DateTime.Now), null);
        }
    }

    /// <summary>
    /// Reproduz um buffer WAV gravado anteriormente.
    /// </summary>
    public async Task<bool> PlayWavBufferAsync(byte[] wav, CancellationToken ct)
    {
        try
        {
            if (wav is null || wav.Length == 0) return false;
            using var ms = new MemoryStream(wav, writable: false);
            using var reader = new WaveFileReader(ms);
            using var output = new WaveOutEvent();
            output.Init(reader);
            output.Play();
            while (output.PlaybackState == PlaybackState.Playing)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha reproduzindo WAV");
            return false;
        }
    }

    private Task<TestResult> RecordMicrophoneAsync(int seconds, CancellationToken ct) =>
        TryRunAsync("microfone", async () =>
        {
            var (result, _) = await RecordMicrophoneWithBufferAsync(seconds, ct).ConfigureAwait(false);
            return result;
        });

    // ====================== Testes de detecção (read-only) ======================

    public Task<TestResult> RunUsbPortsAsync(CancellationToken ct) =>
        TryRunAsync("usb", async () =>
        {
            var rows = await _wmi.QueryAsync("root\\cimv2",
                "SELECT Name FROM Win32_USBController",
                TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);

            int usb4 = 0, usb3 = 0, usb2 = 0;
            foreach (var r in rows)
            {
                var n = r.GetString("Name") ?? "";
                if (n.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("Parsec", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                if (n.IndexOf("USB4", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("USB 4", StringComparison.OrdinalIgnoreCase) >= 0) usb4++;
                else if (n.IndexOf("3.2", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("3.1", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("3.0", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("xHCI", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("eXtensible", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("SuperSpeed", StringComparison.OrdinalIgnoreCase) >= 0) usb3++;
                else if (n.IndexOf("2.0", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("Enhanced", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("EHCI", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("OHCI", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("UHCI", StringComparison.OrdinalIgnoreCase) >= 0) usb2++;
                else usb3++; // controladora moderna sem rótulo de versão
            }

            var total = usb4 + usb3 + usb2;
            if (total == 0)
                return new TestResult("usb", AutoStatus.NaoTestado, "Nenhuma controladora USB detectada", DateTime.Now);

            // Controladora não é porta: um desktop tem UMA controladora com dez
            // portas. O que o técnico quer saber é quantos dispositivos estão
            // respondendo agora — mesma contagem da etapa "Inputs e portas".
            var devices = await CountUsbDevicesAsync(ct).ConfigureAwait(false);

            var parts = new List<string>();
            if (usb4 > 0) parts.Add($"{usb4}× USB4");
            if (usb3 > 0) parts.Add($"{usb3}× USB 3.x");
            if (usb2 > 0) parts.Add($"{usb2}× USB 2.0");
            var controllers = string.Join(" • ", parts);
            var detail = devices > 0
                ? $"{devices} dispositivo(s) USB conectado(s) • controladora: {controllers}"
                : $"Nenhum dispositivo USB conectado agora • controladora: {controllers}";
            // Só USB 2.0 num equipamento atual é sinal de atenção.
            var status = (usb4 > 0 || usb3 > 0) ? AutoStatus.OK : AutoStatus.Atencao;
            return new TestResult("usb", status, detail, DateTime.Now);
        });

    /// <summary>
    /// Dispositivos USB conectados agora (raiz por VID+PID, sem hubs, sem o
    /// rádio Bluetooth) — espelha o filtro do PortCollector.
    /// </summary>
    private async Task<int> CountUsbDevicesAsync(CancellationToken ct)
    {
        try
        {
            var rows = await _wmi.QueryAsync("root\\cimv2",
                "SELECT Name, DeviceID, Service, PNPClass FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\\\VID_%'",
                TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            var vidPid = new System.Text.RegularExpressions.Regex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
            {
                var id = r.GetString("DeviceID") ?? "";
                var m = vidPid.Match(id);
                if (!m.Success) continue;
                var name = r.GetString("Name") ?? "";
                var service = r.GetString("Service") ?? "";
                var pnpClass = r.GetString("PNPClass") ?? "";
                if (service.Equals("USBHUB3", StringComparison.OrdinalIgnoreCase)
                    || service.Equals("USBHUB", StringComparison.OrdinalIgnoreCase)
                    || service.Equals("usbccgp", StringComparison.OrdinalIgnoreCase)
                    || service.Equals("BTHUSB", StringComparison.OrdinalIgnoreCase)
                    || pnpClass.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase)
                    || name.IndexOf("Root Hub", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Generic USB Hub", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                seen.Add($"{m.Groups[1].Value}&{m.Groups[2].Value}");
            }
            return seen.Count;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Contagem de dispositivos USB falhou");
            return 0;
        }
    }

    public Task<TestResult> RunVideoOutputsAsync(CancellationToken ct) =>
        TryRunAsync("hdmi", async () =>
        {
            await Task.Yield();
            var outputs = _displays.EnumerateOutputs().Where(o => !o.IsInternal).ToList();
            if (outputs.Count == 0)
            {
                return new TestResult("hdmi", AutoStatus.NaoTestado,
                    "Nenhum monitor ligado nas saídas de vídeo — conecte um por HDMI/DisplayPort para testar", DateTime.Now);
            }
            var detail = string.Join(" • ", outputs.Select(o => o.Describe()));
            return new TestResult("hdmi", AutoStatus.OK, detail, DateTime.Now);
        });

    public Task<TestResult> RunRefreshRateAsync(CancellationToken ct) =>
        TryRunAsync("taxa_atualizacao", () => Task.Run(() =>
        {
            // Win32_VideoController.CurrentRefreshRate é NOTORIAMENTE errado em
            // multi-monitor (reporta 60 mesmo numa tela de 165 Hz). A fonte
            // correta é EnumDisplaySettings por display ativo.
            var (primaryHz, maxHz) = ReadRefreshRates();
            if (primaryHz == 0 && maxHz == 0)
                return new TestResult("taxa_atualizacao", AutoStatus.NaoTestado, "Taxa de atualização não reportada", DateTime.Now);

            var primary = RoundHz(primaryHz > 0 ? primaryHz : maxHz);
            var detail = $"{primary} Hz (tela principal)";
            var maxRounded = RoundHz(maxHz);
            if (maxRounded > primary) detail += $" • até {maxRounded} Hz em outra tela";
            return new TestResult("taxa_atualizacao", AutoStatus.OK, detail, DateTime.Now);
        }, ct));

    /// <summary>
    /// Lê o refresh rate (Hz) atual de cada display ativo via EnumDisplaySettings.
    /// Retorna (primário, maior entre todas as telas).
    /// </summary>
    private static (int primaryHz, int maxHz) ReadRefreshRates()
    {
        const int ENUM_CURRENT_SETTINGS = -1;
        const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;

        int primaryHz = 0, maxHz = 0;
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        uint i = 0;
        while (EnumDisplayDevices(null, i, ref dd, 0))
        {
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
            {
                var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
                if (EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref dm))
                {
                    var hz = (int)dm.dmDisplayFrequency;
                    if (hz > maxHz) maxHz = hz;
                    // O display primário fica na origem (0,0) do desktop virtual.
                    if (dm.dmPositionX == 0 && dm.dmPositionY == 0 && hz > 0) primaryHz = hz;
                }
            }
            dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
            i++;
        }
        return (primaryHz, maxHz);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct DEVMODE
    {
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    public Task<TestResult> RunBiometricsAsync(CancellationToken ct) =>
        TryRunAsync("biometria", async () =>
        {
            var fp = await _wmi.QueryAsync("root\\cimv2",
                "SELECT Name FROM Win32_PnPEntity WHERE PNPClass = 'Biometric'",
                TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            var cams = await _wmi.QueryAsync("root\\cimv2",
                "SELECT Name FROM Win32_PnPEntity WHERE PNPClass = 'Camera' OR PNPClass = 'Image'",
                TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);

            var hasFp = fp.Count > 0;
            var hasIr = cams.Any(c =>
            {
                var n = c.GetString("Name") ?? "";
                return n.IndexOf(" IR", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("Infrared", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("Hello", StringComparison.OrdinalIgnoreCase) >= 0;
            });

            var parts = new List<string>
            {
                hasFp ? $"Digital: {fp[0].GetString("Name") ?? "presente"}" : "Digital: não detectada",
                hasIr ? "Câmera IR (Hello): presente" : "Câmera IR: não detectada",
            };
            // Recurso opcional — ausência não reprova; presença é OK.
            var status = (hasFp || hasIr) ? AutoStatus.OK : AutoStatus.NaoAplicavel;
            return new TestResult("biometria", status, string.Join(" • ", parts), DateTime.Now);
        });

    public Task<TestResult> RunCardReaderAsync(CancellationToken ct) =>
        TryRunAsync("leitor_cartao", async () =>
        {
            var rows = await _wmi.QueryAsync("root\\cimv2",
                "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%Card Reader%' OR Name LIKE '%SD Host%' " +
                "OR Name LIKE '%SDA Standard%' OR Name LIKE '%MMC Storage%' OR Name LIKE '%RTS5%'",
                TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);

            var names = rows.Select(r => r.GetString("Name"))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (names.Count == 0)
                return new TestResult("leitor_cartao", AutoStatus.NaoAplicavel, "Leitor de cartão não detectado", DateTime.Now);
            return new TestResult("leitor_cartao", AutoStatus.OK, names[0], DateTime.Now);
        });

    /// <summary>Arredonda Hz medidos (ex.: 59→60, 143→144) para valores padrão.</summary>
    private static int RoundHz(int hz) => hz switch
    {
        >= 28 and <= 32 => 30,
        >= 47 and <= 52 => 50,
        >= 58 and <= 62 => 60,
        >= 70 and <= 78 => 75,
        >= 88 and <= 92 => 90,
        >= 98 and <= 102 => 100,
        >= 118 and <= 122 => 120,
        >= 142 and <= 146 => 144,
        >= 163 and <= 167 => 165,
        >= 198 and <= 202 => 200,
        >= 238 and <= 242 => 240,
        >= 358 and <= 362 => 360,
        _ => hz,
    };

    private async Task<TestResult> TryRunAsync(string testKey, Func<Task<TestResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Teste {Test} cancelado", testKey);
            return new TestResult(testKey, AutoStatus.NaoTestado, "Cancelado", DateTime.Now);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha em {Test}", testKey);
            return new TestResult(testKey, AutoStatus.Falha, $"Erro: {ex.Message}", DateTime.Now);
        }
    }
}

/// <summary>
/// Provider simples de onda senoidal usado pelo teste de áudio. Mantido aqui
/// para evitar dependência de samples extras do NAudio.
/// </summary>
internal sealed class SineWaveProvider32 : WaveProvider32
{
    private int _sample;
    public float Frequency { get; set; } = 1000f;
    public float Amplitude { get; set; } = 0.25f;

    public SineWaveProvider32() : base(44100, 1) { }

    public override int Read(float[] buffer, int offset, int sampleCount)
    {
        int sampleRate = WaveFormat.SampleRate;
        for (int n = 0; n < sampleCount; n++)
        {
            buffer[n + offset] = (float)(Amplitude * Math.Sin((2 * Math.PI * _sample * Frequency) / sampleRate));
            _sample++;
            if (_sample >= sampleRate) _sample = 0;
        }
        return sampleCount;
    }
}

/// <summary>
/// Provider estéreo (44.1 kHz, 2 canais, float 32-bit) que toca tons
/// independentes em cada canal. Usado pelo teste de áudio estéreo para que o
/// técnico possa identificar canal esquerdo e direito separadamente.
/// </summary>
internal sealed class StereoTestProvider : WaveProvider32
{
    private int _sample;
    private float _leftFreq;
    private float _rightFreq;
    public float Amplitude { get; set; } = 0.25f;

    public StereoTestProvider() : base(44100, 2) { }

    public void PlayLeftOnly(float frequencyHz)
    {
        _leftFreq = frequencyHz;
        _rightFreq = 0f;
    }

    public void PlayRightOnly(float frequencyHz)
    {
        _leftFreq = 0f;
        _rightFreq = frequencyHz;
    }

    public void PlayBoth(float frequencyHz)
    {
        _leftFreq = frequencyHz;
        _rightFreq = frequencyHz;
    }

    public override int Read(float[] buffer, int offset, int sampleCount)
    {
        // sampleCount conta amostras totais (L + R intercaladas).
        int sampleRate = WaveFormat.SampleRate;
        for (int n = 0; n < sampleCount; n += 2)
        {
            var t = _sample / (double)sampleRate;
            var left = _leftFreq > 0
                ? (float)(Amplitude * Math.Sin(2 * Math.PI * _leftFreq * t))
                : 0f;
            var right = _rightFreq > 0
                ? (float)(Amplitude * Math.Sin(2 * Math.PI * _rightFreq * t))
                : 0f;
            buffer[offset + n] = left;
            if (n + 1 < sampleCount) buffer[offset + n + 1] = right;
            _sample++;
            if (_sample >= sampleRate) _sample = 0;
        }
        return sampleCount;
    }
}
