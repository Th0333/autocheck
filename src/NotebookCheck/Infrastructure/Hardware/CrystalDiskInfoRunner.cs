using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace NotebookCheck.Infrastructure.Hardware;

/// <summary>
/// Integra o CrystalDiskInfo REAL (binário oficial embutido como recurso).
/// Em vez de implementar leitura SMART própria, extrai o CrystalDiskInfo
/// portátil para %TEMP% e o executa:
///   - <see cref="CollectAsync"/> roda em modo /CopyExit, que gera o
///     relatório SMART completo em DiskInfo.txt, e parseia o resultado.
///   - <see cref="LaunchGui"/> abre a interface gráfica completa do
///     CrystalDiskInfo para o técnico inspecionar manualmente.
///
/// O binário é o CrystalDiskInfo 9.9.1 (freeware, MIT/BSD-like). Roda como
/// administrador (o app já solicita highestAvailable no manifest).
/// </summary>
public sealed class CrystalDiskInfoRunner
{
    private const string ResourceName = "NotebookCheck.Assets.CrystalDiskInfo.zip";
    private readonly ILogger<CrystalDiskInfoRunner> _logger;
    private string? _extractedDir;
    private readonly object _extractLock = new();

    public CrystalDiskInfoRunner(ILogger<CrystalDiskInfoRunner> logger)
    {
        _logger = logger;
    }

    /// <summary>Caminho do DiskInfo executável apropriado para a arquitetura.</summary>
    private string? GetExePath(string dir)
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        var candidate = arch switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "DiskInfoA64.exe",
            System.Runtime.InteropServices.Architecture.X64 => "DiskInfo64.exe",
            _ => "DiskInfo32.exe",
        };
        var path = Path.Combine(dir, candidate);
        if (File.Exists(path)) return path;
        // fallback: qualquer DiskInfo*.exe presente
        return Directory.EnumerateFiles(dir, "DiskInfo*.exe").FirstOrDefault();
    }

    /// <summary>
    /// Garante que o CrystalDiskInfo está extraído em disco e devolve o
    /// diretório. Extrai apenas uma vez por sessão. Thread-safe.
    /// </summary>
    public string EnsureExtracted()
    {
        if (_extractedDir is not null && Directory.Exists(_extractedDir))
            return _extractedDir;

        lock (_extractLock)
        {
            if (_extractedDir is not null && Directory.Exists(_extractedDir))
                return _extractedDir;

            // Pasta carimbada com a versão: cada versão extrai um conjunto novo,
            // evitando reaproveitar uma extração antiga/incompleta de uma versão
            // anterior (causa do "Graph.html não encontrado").
            var version = NotebookCheck.Bootstrap.AppDefaults.CurrentVersion;
            var dir = Path.Combine(Path.GetTempPath(), "NotebookCheck", $"cdi-{version}");
            Directory.CreateDirectory(dir);

            // Sentinela: um recurso que o CrystalDiskInfo PRECISA além do exe.
            // Se o exe OU o sentinela faltarem, a extração anterior está
            // incompleta — extrai tudo de novo (overwrite repara o que faltou).
            var sentinel = Path.Combine(dir, "CdiResource", "dialog", "Graph.html");
            if (GetExePath(dir) is null || !File.Exists(sentinel))
            {
                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream(ResourceName)
                    ?? throw new InvalidOperationException($"Recurso {ResourceName} não encontrado no assembly");
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // diretório
                    var destPath = Path.Combine(dir, entry.FullName);
                    var destDir = Path.GetDirectoryName(destPath);
                    if (destDir is not null) Directory.CreateDirectory(destDir);
                    try
                    {
                        entry.ExtractToFile(destPath, overwrite: true);
                    }
                    catch (IOException)
                    {
                        // arquivo em uso (GUI aberta) — ignora (vale para o .exe;
                        // os recursos não ficam travados).
                    }
                }

                if (!File.Exists(sentinel))
                    _logger.LogWarning("CrystalDiskInfo: Graph.html ausente após extração em {Dir}", dir);
            }

            _extractedDir = dir;
            return dir;
        }
    }

    /// <summary>
    /// Roda o CrystalDiskInfo em modo /CopyExit (gera DiskInfo.txt e fecha),
    /// parseia o relatório e devolve a saúde de cada disco. Nunca lança —
    /// retorna lista vazia em caso de falha.
    /// </summary>
    public async Task<IReadOnlyList<CrystalDiskInfoDisk>> CollectAsync(CancellationToken ct)
    {
        try
        {
            var dir = EnsureExtracted();
            var exe = GetExePath(dir);
            if (exe is null)
            {
                _logger.LogWarning("DiskInfo.exe não encontrado após extração");
                return Array.Empty<CrystalDiskInfoDisk>();
            }

            var reportPath = Path.Combine(dir, "DiskInfo.txt");
            try { if (File.Exists(reportPath)) File.Delete(reportPath); } catch { }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "/CopyExit",
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using (var proc = Process.Start(psi))
            {
                if (proc is null) return Array.Empty<CrystalDiskInfoDisk>();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(40));
                try
                {
                    await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { if (!proc.HasExited) proc.Kill(); } catch { }
                }
            }

            // Pequena espera: o arquivo às vezes é escrito logo após o exit.
            for (var i = 0; i < 10 && !File.Exists(reportPath); i++)
                await Task.Delay(200, ct).ConfigureAwait(false);

            if (!File.Exists(reportPath))
            {
                _logger.LogWarning("CrystalDiskInfo não gerou DiskInfo.txt");
                return Array.Empty<CrystalDiskInfoDisk>();
            }

            var text = await File.ReadAllTextAsync(reportPath, ct).ConfigureAwait(false);
            return ParseReport(text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha executando CrystalDiskInfo");
            return Array.Empty<CrystalDiskInfoDisk>();
        }
    }

    /// <summary>Abre a interface gráfica completa do CrystalDiskInfo.</summary>
    public void LaunchGui()
    {
        var dir = EnsureExtracted();
        var exe = GetExePath(dir);
        if (exe is null) throw new InvalidOperationException("DiskInfo.exe não encontrado");

        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = dir,
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// Parseia o DiskInfo.txt. Cada disco começa numa linha "(NN) Modelo"
    /// dentro de um bloco delimitado por linhas de hífens, seguido de campos
    /// "  Campo : Valor". A seção S.M.A.R.T. lista atributos por ID.
    /// </summary>
    private static IReadOnlyList<CrystalDiskInfoDisk> ParseReport(string text)
    {
        var disks = new List<CrystalDiskInfoDisk>();
        // Normaliza quebras de linha
        var lines = text.Replace("\r\n", "\n").Split('\n');

        // Cada disco detalhado tem um cabeçalho "(NN) Modelo" seguido por
        // pares "Campo : Valor". Detectamos o início pela presença de
        // "Model :" logo após o cabeçalho.
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Flush()
        {
            if (fields.Count == 0) return;
            if (!fields.ContainsKey("Model")) { fields.Clear(); return; }

            var disk = BuildDisk(fields);
            if (disk is not null) disks.Add(disk);
            fields.Clear();
        }

        var inDetailSection = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            // Início da seção "Disk List" não tem campos; a parte detalhada
            // vem depois. Detectamos blocos de campos "X : Y".
            var m = Regex.Match(line, @"^\s{2,}([A-Za-z][A-Za-z0-9 \.\/\(\)\-]+?)\s*:\s*(.+?)\s*$");
            if (m.Success)
            {
                var key = m.Groups[1].Value.Trim();
                var val = m.Groups[2].Value.Trim();

                // "Model" sempre inicia um novo disco — faz flush do anterior.
                if (key.Equals("Model", StringComparison.OrdinalIgnoreCase))
                {
                    Flush();
                    inDetailSection = true;
                }
                if (inDetailSection)
                {
                    fields[key] = val;
                }
            }
        }
        Flush();

        return disks;
    }

    private static CrystalDiskInfoDisk? BuildDisk(Dictionary<string, string> f)
    {
        f.TryGetValue("Model", out var model);
        if (string.IsNullOrWhiteSpace(model)) return null;

        f.TryGetValue("Firmware", out var firmware);
        f.TryGetValue("Serial Number", out var serial);
        f.TryGetValue("Interface", out var iface);
        f.TryGetValue("Disk Size", out var sizeStr);
        f.TryGetValue("Drive Letter", out var driveLetter);

        // Health Status : Good (92 %)
        int? healthPercent = null;
        string? healthLabel = null;
        if (f.TryGetValue("Health Status", out var health))
        {
            var hm = Regex.Match(health, @"^([^\(]+?)\s*(?:\((\d+)\s*%\))?$");
            if (hm.Success)
            {
                healthLabel = hm.Groups[1].Value.Trim();
                if (hm.Groups[2].Success && int.TryParse(hm.Groups[2].Value, out var hp))
                    healthPercent = hp;
            }
        }

        long? powerOnHours = ParseLeadingLong(f, "Power On Hours");
        long? powerOnCount = ParseLeadingLong(f, "Power On Count");

        double? hostReadsGb = ParseGb(f, "Host Reads");
        double? hostWritesGb = ParseGb(f, "Host Writes");

        int? tempC = null;
        if (f.TryGetValue("Temperature", out var t))
        {
            var tm = Regex.Match(t, @"(\d+)\s*C");
            if (tm.Success && int.TryParse(tm.Groups[1].Value, out var tv)) tempC = tv;
        }

        return new CrystalDiskInfoDisk(
            Model: model.Trim(),
            Firmware: firmware,
            SerialNumber: serial,
            Interface: iface,
            DiskSize: sizeStr,
            DriveLetter: driveLetter,
            HealthLabel: healthLabel,
            HealthPercent: healthPercent,
            PowerOnHours: powerOnHours,
            PowerOnCount: powerOnCount,
            HostReadsGb: hostReadsGb,
            HostWritesGb: hostWritesGb,
            TemperatureC: tempC);
    }

    private static long? ParseLeadingLong(Dictionary<string, string> f, string key)
    {
        if (!f.TryGetValue(key, out var v)) return null;
        var m = Regex.Match(v, @"(\d[\d,\.]*)");
        if (!m.Success) return null;
        var digits = m.Groups[1].Value.Replace(",", "").Replace(".", "");
        return long.TryParse(digits, out var n) ? n : null;
    }

    /// <summary>Converte "37619 GB" para TB (÷1024).</summary>
    private static double? ParseGb(Dictionary<string, string> f, string key)
    {
        if (!f.TryGetValue(key, out var v)) return null;
        var m = Regex.Match(v, @"(\d[\d,\.]*)\s*(GB|TB)");
        if (!m.Success) return null;
        var num = m.Groups[1].Value.Replace(",", "");
        if (!double.TryParse(num, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var val)) return null;
        var unit = m.Groups[2].Value.ToUpperInvariant();
        return unit == "TB" ? val : val / 1024.0; // sempre devolve TB
    }
}

/// <summary>
/// Dados de um disco lidos do relatório oficial do CrystalDiskInfo.
/// </summary>
public record CrystalDiskInfoDisk(
    string Model,
    string? Firmware,
    string? SerialNumber,
    string? Interface,
    string? DiskSize,
    string? DriveLetter,
    string? HealthLabel,
    int? HealthPercent,
    long? PowerOnHours,
    long? PowerOnCount,
    double? HostReadsGb,
    double? HostWritesGb,
    int? TemperatureC);
