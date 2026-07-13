using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace NotebookCheck.Infrastructure.Hardware;

/// <summary>
/// Empacota e executa o QuickMemoryTestOK portátil (freeware da SoftwareOK,
/// uso comercial permitido) para o teste avulso de memória. Mesmo padrão do
/// <see cref="CrystalDiskInfoRunner"/>: o ZIP viaja embutido no assembly
/// (publish single-file), é extraído para <c>%TEMP%</c> uma vez por versão e
/// o exe é aberto em GUI — o técnico roda o teste na ferramenta e reporta o
/// resultado de volta na janela de componentes.
/// </summary>
public sealed class QuickMemoryTestRunner
{
    private const string ResourceName = "NotebookCheck.Assets.QuickMemoryTest.zip";

    private readonly ILogger<QuickMemoryTestRunner> _logger;
    private readonly object _extractLock = new();
    private string? _extractedDir;

    public QuickMemoryTestRunner(ILogger<QuickMemoryTestRunner> logger)
    {
        _logger = logger;
    }

    private static string ExtractRoot => Path.Combine(
        Path.GetTempPath(), "NotebookCheck", $"qmt-{Bootstrap.AppDefaults.CurrentVersion}");

    /// <summary>
    /// Garante o ZIP extraído (1x por sessão/versão) e devolve o caminho do exe.
    /// Lança <see cref="InvalidOperationException"/> com mensagem amigável se o
    /// recurso não puder ser extraído.
    /// </summary>
    public string EnsureExtracted()
    {
        lock (_extractLock)
        {
            if (_extractedDir is not null)
            {
                var cached = FindExe(_extractedDir);
                if (cached is not null) return cached;
            }

            var dir = ExtractRoot;
            var existing = Directory.Exists(dir) ? FindExe(dir) : null;
            if (existing is null)
            {
                using var stream = typeof(QuickMemoryTestRunner).Assembly
                    .GetManifestResourceStream(ResourceName)
                    ?? throw new InvalidOperationException(
                        "Recurso do QuickMemoryTest não encontrado no executável.");

                Directory.CreateDirectory(dir);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
                zip.ExtractToDirectory(dir, overwriteFiles: true);
                existing = FindExe(dir)
                    ?? throw new InvalidOperationException(
                        "Exe do QuickMemoryTest não encontrado após a extração.");
            }

            _extractedDir = dir;
            return existing;
        }
    }

    /// <summary>
    /// Abre a GUI do QuickMemoryTestOK e devolve o processo (ou null se não
    /// iniciou). O chamador pode observar <see cref="Process.HasExited"/>.
    /// </summary>
    public Process? LaunchGui()
    {
        var exe = EnsureExtracted();
        _logger.LogInformation("Abrindo QuickMemoryTestOK: {Exe}", exe);
        return Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = true,
        });
    }

    /// <summary>Prefere a build x64; cai para qualquer exe portátil do pacote.</summary>
    private static string? FindExe(string dir)
    {
        var x64 = Path.Combine(dir, "QuickMemoryTestOK_x64_p.exe");
        if (File.Exists(x64)) return x64;
        var x86 = Path.Combine(dir, "QuickMemoryTestOK_p.exe");
        if (File.Exists(x86)) return x86;
        try
        {
            return Directory.EnumerateFiles(dir, "QuickMemoryTestOK*.exe", SearchOption.AllDirectories)
                .OrderByDescending(p => p.Contains("_x64", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
