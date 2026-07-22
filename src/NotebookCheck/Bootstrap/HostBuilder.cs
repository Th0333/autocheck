using System;
using System.IO;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Extensions.Http;
using Serilog;
using Serilog.Events;
using NotebookCheck.Application.Orchestration;
using NotebookCheck.Application.Sync;
using NotebookCheck.Application.Testing;
using NotebookCheck.Domain.Abstractions;
using NotebookCheck.Infrastructure.Abstractions;
using NotebookCheck.Infrastructure.Api;
using NotebookCheck.Infrastructure.Erp;
using NotebookCheck.Infrastructure.Hardware;
using NotebookCheck.Infrastructure.IO;
using NotebookCheck.Infrastructure.Net;
using NotebookCheck.Infrastructure.Persistence;
using NotebookCheck.Infrastructure.PowerShell;
using NotebookCheck.Infrastructure.Wmi;

namespace NotebookCheck.Bootstrap;

/// <summary>
/// Construtor do Generic Host com DI, configuração e Serilog.
/// </summary>
public static class AppHostBuilder
{
    public static IHost Build()
    {
        var bootstrap = new ConfigBootstrap();
        var config = bootstrap.EnsureLoaded();

        // Diretório de gravação tolerante a pendrive: a pasta do .exe se for
        // gravável, senão %LOCALAPPDATA%\Notelet. Vale para logs, relatórios,
        // arquivo consolidado e fila offline.
        var storageDir = AppPaths.ResolveWritable(AppContext.BaseDirectory);
        var logsDir = Path.Combine(storageDir, "logs");
        Directory.CreateDirectory(logsDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: Path.Combine(logsDir, "app-.log"),
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: 5 * 1024 * 1024,
                retainedFileCountLimit: 5,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        return Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices(services =>
            {
                services.AddSingleton(bootstrap);
                services.AddSingleton(config);

                // Infra
                services.AddSingleton<IFileSystem, PhysicalFileSystem>();
                services.AddSingleton<IPowerStatusProvider, PowerStatusProvider>();
                services.AddSingleton<IDisplayEnumerator, DisplayEnumerator>();
                services.AddSingleton<IPowerShellRunner, PowerShellRunner>();
                services.AddSingleton<IWmiQueryRunner, WmiQueryRunner>();
                services.AddSingleton<IKeyboardBacklightDetector, KeyboardBacklightDetector>();
                services.AddSingleton<CrystalDiskInfoRunner>();
                services.AddSingleton<DellBiosPasswordReader>();
                services.AddSingleton<QuickMemoryTestRunner>();
                services.AddSingleton<IHardwareCollector, WmiHardwareCollector>();
                services.AddSingleton<IPortCollector, PortCollector>();
                services.AddSingleton<INetworkProbe, HttpNetworkProbe>();

                services.AddSingleton<Application.Bench.BenchmarkSuite>();
                services.AddSingleton<Application.Humanization.HumanizationRunner>();

                // Test engine
                services.AddSingleton<ITestEngine, TestEngine>();

                // Persistence — todos no diretório gravável resolvido acima.
                services.AddSingleton<IReportRepository>(sp =>
                    new JsonReportRepository(sp.GetRequiredService<ILogger<JsonReportRepository>>(), storageDir));
                services.AddSingleton<IReportArchive>(sp =>
                    new JsonReportArchive(sp.GetRequiredService<ILogger<JsonReportArchive>>(), storageDir));
                services.AddSingleton<IOfflineQueue>(sp =>
                    new FileOfflineQueue(sp.GetRequiredService<ILogger<FileOfflineQueue>>(), storageDir));

                // API
                services.AddHttpClient("api", c =>
                {
                    c.Timeout = TimeSpan.FromSeconds(30);
                }).AddTransientHttpErrorPolicy(p => p.WaitAndRetryAsync(new[]
                {
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(4),
                    TimeSpan.FromSeconds(8),
                }));
                services.AddHttpClient("probe", c =>
                {
                    c.Timeout = TimeSpan.FromSeconds(7);
                });
                services.AddSingleton<IApiClient, ApiClient>();

                // Integração com o ERP de estoque (modo "Cadastro no estoque").
                services.AddSingleton(ErpConfig.Load(storageDir));
                services.AddSingleton(sp => new SerialNtbStore(
                    sp.GetRequiredService<ILogger<SerialNtbStore>>(), storageDir));
                services.AddSingleton(sp => new LinhaStore(
                    sp.GetRequiredService<ILogger<LinhaStore>>(), storageDir));
                services.AddSingleton(sp => new MachineIdentityStore(
                    sp.GetRequiredService<ILogger<MachineIdentityStore>>(), storageDir));
                services.AddSingleton(sp => new AssumidosStore(
                    sp.GetRequiredService<ILogger<AssumidosStore>>(), storageDir));
                services.AddSingleton(sp => new KanbanHiddenStore(
                    sp.GetRequiredService<ILogger<KanbanHiddenStore>>(), storageDir));
                services.AddHttpClient("erp", c =>
                {
                    c.Timeout = TimeSpan.FromSeconds(30);
                }).AddTransientHttpErrorPolicy(p => p.WaitAndRetryAsync(new[]
                {
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(4),
                }));
                services.AddSingleton<ErpClient>();

                // Orchestration
                services.AddSingleton<IRetestController, RetestController>();
                services.AddSingleton<Application.Orchestration.PostRepairRetest>();
                services.AddSingleton<ChecklistSession>();

                // Sync background
                services.AddSingleton<OfflineSyncService>();
                services.AddHostedService(sp => sp.GetRequiredService<OfflineSyncService>());

                // Presentation
                services.AddSingleton<Presentation.MainViewModel>();
                services.AddTransient<Presentation.ReportsViewModel>();
                services.AddTransient<Presentation.Views.ReportsWindow>();
                services.AddTransient<Presentation.CadastroViewModel>();
                services.AddTransient<Presentation.Views.CadastroWindow>();
                services.AddTransient<Presentation.KanbanViewModel>();
                services.AddTransient<Presentation.Views.KanbanWindow>();
                services.AddTransient<Presentation.TesteCompletoViewModel>();
                services.AddTransient<Presentation.Views.TesteCompletoWindow>();
                services.AddTransient<Presentation.TesteComponentesViewModel>();
                services.AddTransient<Presentation.Views.TesteComponentesWindow>();
                services.AddSingleton<MainWindow>();
            })
            .Build();
    }
}
