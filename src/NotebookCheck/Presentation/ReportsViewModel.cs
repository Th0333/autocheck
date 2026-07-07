using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NotebookCheck.Bootstrap;
using NotebookCheck.Domain.Abstractions;
using NotebookCheck.Infrastructure.Api;

namespace NotebookCheck.Presentation;

/// <summary>
/// ViewModel da janela "Relatórios". Tem duas abas:
/// <list type="bullet">
///   <item>Offline — alimentada pelo arquivo local <c>reports.json</c>.</item>
///   <item>Online — busca a lista do painel via <c>GET /api/reports</c>.</item>
/// </list>
/// Permite reenviar individualmente qualquer relatório local ainda não
/// sincronizado.
/// </summary>
public sealed partial class ReportsViewModel : ObservableObject
{
    private readonly IReportArchive _archive;
    private readonly IApiClient _api;
    private readonly IHttpClientFactory _httpFactory;
    private readonly AppConfig _config;
    private readonly ILogger<ReportsViewModel> _logger;

    public ObservableCollection<LocalReportRow> LocalReports { get; } = new();
    public ObservableCollection<RemoteReportRow> RemoteReports { get; } = new();

    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string archivePath = "";
    [ObservableProperty] private int localTotal;
    [ObservableProperty] private int localPending;
    [ObservableProperty] private int localSynced;

    public ReportsViewModel(
        IReportArchive archive,
        IApiClient api,
        IHttpClientFactory httpFactory,
        AppConfig config,
        ILogger<ReportsViewModel> logger)
    {
        _archive = archive;
        _api = api;
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
        ArchivePath = _archive.ArchivePath;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await Task.WhenAll(LoadLocalAsync(), LoadRemoteAsync());
    }

    [RelayCommand]
    private async Task LoadLocalAsync()
    {
        try
        {
            var entries = await _archive.ListAsync(CancellationToken.None);
            LocalReports.Clear();
            foreach (var e in entries)
            {
                LocalReports.Add(new LocalReportRow(
                    TestId: e.TestId,
                    NtbCode: e.Payload.Machine?.NtbCode ?? "",
                    Manufacturer: e.Payload.Machine?.Manufacturer ?? "",
                    Model: e.Payload.Machine?.Model ?? "",
                    Location: e.Payload.Machine?.Location ?? "",
                    Technician: e.Payload.TechnicianName ?? "",
                    TestedAt: e.Payload.TestedAt ?? "",
                    Classification: e.Payload.FinalClassification ?? "",
                    SyncedAt: e.SyncedAt,
                    Payload: e.Payload));
            }
            LocalTotal = LocalReports.Count;
            LocalSynced = LocalReports.Count(r => r.SyncedAt is not null);
            LocalPending = LocalTotal - LocalSynced;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha lendo relatórios locais");
            StatusMessage = $"Falha lendo arquivo local: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task LoadRemoteAsync()
    {
        IsBusy = true;
        try
        {
            if (!ConfigBootstrap.TryBuildApiUri(_config, out var apiUri) || apiUri is null)
            {
                StatusMessage = "Painel não configurado.";
                RemoteReports.Clear();
                return;
            }

            // GET /api/reports — converte a URL do POST em URL do GET (mesmo path).
            var listUri = new Uri(apiUri.GetLeftPart(UriPartial.Path));
            using var client = _httpFactory.CreateClient("api");
            using var req = new HttpRequestMessage(HttpMethod.Get, listUri);
            // Em algumas instalações o GET é público; mantemos o bearer caso seja exigido.
            if (!string.IsNullOrWhiteSpace(_config.AuthToken))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AuthToken);
            }
            using var resp = await client.SendAsync(req, CancellationToken.None);
            if (!resp.IsSuccessStatusCode)
            {
                StatusMessage = $"Painel respondeu {(int)resp.StatusCode} — verifique configuração.";
                RemoteReports.Clear();
                return;
            }

            var body = await resp.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(body);
            RemoteReports.Clear();
            if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    string Get(string key) => item.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
                    RemoteReports.Add(new RemoteReportRow(
                        TestId: Get("test_id"),
                        NtbCode: Get("ntb_code"),
                        Manufacturer: Get("manufacturer"),
                        Model: Get("model"),
                        Location: Get("location"),
                        Technician: Get("technician_name"),
                        TestedAt: Get("tested_at"),
                        Classification: Get("final_classification")));
                }
            }
            StatusMessage = $"Online: {RemoteReports.Count} relatório(s) no painel.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha buscando lista remota");
            StatusMessage = $"Falha buscando painel: {ex.Message}";
            RemoteReports.Clear();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ResendAsync(LocalReportRow? row)
    {
        if (row is null) return;
        IsBusy = true;
        StatusMessage = $"Reenviando {row.NtbCode}...";
        try
        {
            var result = await _api.SendAsync(row.Payload, CancellationToken.None);
            if (result.Outcome == ApiOutcome.Sent)
            {
                await _archive.MarkSyncedAsync(row.TestId, CancellationToken.None);
                StatusMessage = $"{row.NtbCode}: enviado.";
                await LoadLocalAsync();
            }
            else
            {
                StatusMessage = $"{row.NtbCode}: {result.Outcome} {(result.StatusCode.HasValue ? $"({result.StatusCode})" : "")}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha reenviando {Test}", row.TestId);
            StatusMessage = $"Falha reenviando {row.NtbCode}: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ResendAllPendingAsync()
    {
        IsBusy = true;
        var pending = LocalReports.Where(r => r.SyncedAt is null).ToList();
        if (pending.Count == 0)
        {
            StatusMessage = "Nada pendente para reenviar.";
            IsBusy = false;
            return;
        }

        var ok = 0;
        var fail = 0;
        foreach (var row in pending)
        {
            try
            {
                var result = await _api.SendAsync(row.Payload, CancellationToken.None);
                if (result.Outcome == ApiOutcome.Sent)
                {
                    await _archive.MarkSyncedAsync(row.TestId, CancellationToken.None);
                    ok++;
                }
                else fail++;
            }
            catch { fail++; }
        }

        StatusMessage = $"Reenvio em lote: {ok} ok, {fail} falha(s).";
        await LoadLocalAsync();
        IsBusy = false;
    }
}

public record LocalReportRow(
    string TestId,
    string NtbCode,
    string Manufacturer,
    string Model,
    string Location,
    string Technician,
    string TestedAt,
    string Classification,
    string? SyncedAt,
    ApiPayload Payload)
{
    public string Equipment => string.Join(" ", new[] { Manufacturer, Model }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string SyncStatus => SyncedAt is null ? "Pendente" : "Sincronizado";
}

public record RemoteReportRow(
    string TestId,
    string NtbCode,
    string Manufacturer,
    string Model,
    string Location,
    string Technician,
    string TestedAt,
    string Classification)
{
    public string Equipment => string.Join(" ", new[] { Manufacturer, Model }.Where(s => !string.IsNullOrWhiteSpace(s)));
}
