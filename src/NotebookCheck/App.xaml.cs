using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NotebookCheck.Bootstrap;
using NotebookCheck.Presentation;

namespace NotebookCheck;

public partial class App : System.Windows.Application
{
    /// <summary>Acesso ao container DI para janelas adicionais (Relatórios, etc.).</summary>
    public IHost? Host { get; private set; }

    /// <summary>
    /// Changelog da versão mais recente, obtido do version.json no startup.
    /// Exibido na tela inicial ("Novidades"). Null se não foi possível obter.
    /// </summary>
    public static string[]? LatestChangelog { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        try
        {
            // Aplica o tema salvo (claro/escuro) antes de qualquer janela.
            ThemeManager.Initialize();

            // ----- Auto-update (antes de abrir o app principal) -----
            // Roda silenciosamente, mas mostra uma janela "Atualizando" para o
            // técnico não ficar no escuro. Se não houver update ou falhar,
            // segue normalmente para o app.
            if (await TryAutoUpdateAsync())
            {
                // Atualização aplicada — o .bat vai reabrir o app. Encerra este.
                Shutdown(0);
                return;
            }

            Host = AppHostBuilder.Build();
            await Host.StartAsync();

            var window = Host.Services.GetRequiredService<MainWindow>();
            window.DataContext = Host.Services.GetRequiredService<MainViewModel>();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Falha ao iniciar a aplicação: {ex.Message}\n\n{ex}", "Notelet", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        base.OnStartup(e);
    }

    /// <summary>
    /// Verifica e aplica atualização. Retorna true se uma atualização foi
    /// iniciada (o app deve fechar para o .bat trocar o .exe e reabrir).
    /// </summary>
    private async System.Threading.Tasks.Task<bool> TryAutoUpdateAsync()
    {
        // Permite pular o updater logo após uma troca (evita loop) via flag de arquivo.
        try
        {
            var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.Update.AutoUpdater>.Instance;
            var updater = new Infrastructure.Update.AutoUpdater(logger);

            // Timeout curto só para buscar o manifesto (rede pode estar fora).
            using var manifestCts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(20));
            var manifest = await updater.FetchManifestAsync(manifestCts.Token);
            if (manifest is null) return false;

            // Guarda o changelog para mostrar na tela inicial.
            if (manifest.Changelog is { Length: > 0 })
                LatestChangelog = manifest.Changelog;

            if (!Infrastructure.Update.AutoUpdater.IsNewer(manifest.Version, Bootstrap.AppDefaults.CurrentVersion))
                return false;

            // Há versão nova — mostra a janela de atualização.
            var win = new Presentation.Views.UpdateWindow();
            win.Show();
            win.SetPhase($"Baixando versão {manifest.Version}...");
            win.SetProgress(0);

            // Timeout longo e independente para o download do .exe (~113 MB).
            // O download em si tem seu próprio timeout interno no HttpClient.
            using var downloadCts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromMinutes(20));
            var progress = new System.Progress<int>(p => win.SetProgress(p));
            var newExe = await updater.DownloadAsync(manifest, progress, downloadCts.Token);

            if (newExe is null)
            {
                win.SetPhase("Falha ao baixar a atualização.\nAbrindo a versão atual...");
                await System.Threading.Tasks.Task.Delay(2500);
                win.Close();
                return false;
            }

            win.SetPhase("Instalando atualização...");
            win.SetIndeterminate();
            await System.Threading.Tasks.Task.Delay(600);

            if (updater.ApplyAndRestart(newExe))
            {
                win.SetPhase("Reiniciando na nova versão...");
                await System.Threading.Tasks.Task.Delay(800);
                return true; // caller fecha o app; o .bat reabre
            }

            win.Close();
            return false;
        }
        catch
        {
            return false; // qualquer erro: segue para o app normalmente
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (Host is not null)
            {
                await Host.StopAsync();
                Host.Dispose();
            }
        }
        catch { /* ignore */ }
        base.OnExit(e);
    }
}
