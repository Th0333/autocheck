using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using NotebookCheck.Domain.Models;
using NotebookCheck.Infrastructure.Abstractions;
using NotebookCheck.Infrastructure.Wmi;

namespace NotebookCheck.Infrastructure.Hardware;

/// <summary>
/// Verificador de Windows Autopilot/MDM com múltiplas fontes independentes e
/// pontuação de confiança. Não existe forma 100% determinística de saber,
/// localmente, se o hardware hash está registrado num tenant (essa informação
/// é do lado do servidor/Intune); o que dá para fazer é agregar evidências:
///
///   1. PRINCIPAL — WMI MDM bridge (root\cimv2\mdm\dmmap, MDM_DevDetail_Ext01):
///      instâncias só enumeram em contexto de enrollment ⇒ evidência direta.
///   2. dsregcmd /status — AzureAdJoined / DomainJoined / WorkplaceJoined /
///      TenantName / TenantId / MdmUrl.
///   3. Event log Microsoft-Windows-ModernDeployment-Diagnostics-Provider/Autopilot
///      — registros só existem se o fluxo de Autopilot rodou nesta instalação.
///   4. Registro — perfil cloud-assigned (Provisioning\Diagnostics\Autopilot),
///      HKLM\SOFTWARE\Microsoft\Windows\Autopilot, AutopilotPolicyCache e
///      HKLM\SOFTWARE\Microsoft\Enrollments (enrollments MDM/Intune).
///   5. ARQUIVO DE PERFIL — método para PCs mais antigos/provisionados:
///      o JSON do perfil baixado no OOBE ainda existe na máquina
///      (C:\Windows\ServiceState\wmansvc\AutopilotDDSZTDFile.json e
///      C:\Windows\Provisioning\Autopilot\AutopilotConfigurationFile.json).
///      Presença = evidência direta; o arquivo ainda revela o tenant.
///
/// EM DESENVOLVIMENTO: verificação online autenticando no tenant (Intune /
/// Graph), que daria certeza do registro do hardware hash — será adicionada
/// em versão futura; por ora a linha aparece em Details como aviso.
///
/// Pontuação: +50 evidência direta de MDM/Autopilot • +25 AzureAdJoined •
/// +15 eventos de Autopilot • +10 chaves de registro relevantes.
/// 0–29 = Low, 30–69 = Medium, 70–100 = High.
/// Cada fonte é isolada em try/catch: falta de permissão ou ausência da fonte
/// nunca derruba o checker — vira uma linha em Details.
/// </summary>
public sealed class AutopilotChecker
{
    private readonly IWmiQueryRunner _wmi;
    private readonly IPowerShellRunner _ps;
    private readonly ILogger _logger;

    public AutopilotChecker(IWmiQueryRunner wmi, IPowerShellRunner ps, ILogger logger)
    {
        _wmi = wmi;
        _ps = ps;
        _logger = logger;
    }

    public async Task<AutopilotStatus> CheckAsync(CancellationToken ct)
    {
        var status = new AutopilotStatus();
        var details = status.Details;

        var directMdmEvidence = false;   // +50 (uma vez, qualquer fonte direta)
        var registryRelevant = false;    // +10
        var anySource = false;

        // ============================================================
        // 1. PRINCIPAL — WMI/CIM MDM bridge (root\cimv2\mdm\dmmap)
        // ============================================================
        try
        {
            var rows = await _wmi.QueryAsync(
                "root\\cimv2\\mdm\\dmmap",
                "SELECT * FROM MDM_DevDetail_Ext01",
                TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
            anySource = true;

            if (rows.Count > 0)
            {
                // IMPORTANTE: como admin, MDM_DevDetail_Ext01 responde em QUALQUER
                // Windows — é exatamente a classe que o Get-WindowsAutopilotInfo
                // usa para extrair o hardware hash de máquinas NÃO registradas.
                // Logo, presença de instância NÃO é evidência de enrollment;
                // serve apenas como informação (hash disponível para registro).
                details.Add($"MDM WMI bridge respondeu ({rows.Count} instância(s) de MDM_DevDetail_Ext01) — disponível em qualquer Windows com admin; não prova enrollment.");

                foreach (var row in rows.Take(1))
                {
                    foreach (var (key, value) in row)
                    {
                        if (value is null) continue;
                        var s = value.ToString();
                        if (string.IsNullOrWhiteSpace(s)) continue;

                        // O hardware hash (4 KB base64) não é legível — só registramos presença.
                        if (key.Equals("DeviceHardwareData", StringComparison.OrdinalIgnoreCase))
                        {
                            details.Add($"MDM: DeviceHardwareData presente ({s!.Length} chars — hardware hash do Autopilot, utilizável para registro).");
                            continue;
                        }
                        if (s!.Length > 80) s = s.Substring(0, 80) + "…";
                        details.Add($"MDM: {key} = {s}");
                    }
                }
            }
            else
            {
                details.Add("MDM WMI bridge acessível, mas sem instâncias de MDM_DevDetail_Ext01.");
            }
        }
        catch (WmiQueryException ex)
        {
            details.Add(ex.Cause == WmiFailureCause.AccessDenied
                ? "MDM WMI bridge: acesso negado (rode como administrador para este método)."
                : $"MDM WMI bridge indisponível ({ex.Cause}).");
            _logger.LogDebug(ex, "Autopilot: dmmap falhou");
        }
        catch (Exception ex)
        {
            details.Add("MDM WMI bridge indisponível.");
            _logger.LogDebug(ex, "Autopilot: dmmap exceção");
        }

        // ============================================================
        // 2. REDUNDÂNCIA — dsregcmd /status
        // ============================================================
        try
        {
            const string script = @"
$out = & dsregcmd /status 2>$null | Out-String
function Pick($pattern) {
    if ($out -match $pattern) { return $matches[1].Trim() }
    return ''
}
[pscustomobject]@{
    AzureAdJoined   = (Pick 'AzureAdJoined\s*:\s*(YES|NO)')
    DomainJoined    = (Pick 'DomainJoined\s*:\s*(YES|NO)')
    WorkplaceJoined = (Pick 'WorkplaceJoined\s*:\s*(YES|NO)')
    TenantName      = (Pick 'TenantName\s*:\s*(.+?)\r?\n')
    TenantId        = (Pick 'TenantId\s*:\s*([0-9a-fA-F-]+)')
    MdmUrl          = (Pick 'MdmUrl\s*:\s*(\S+)')
    HasOutput       = ($out.Length -gt 0)
}
";
            var rows = await _ps.InvokeAsync(script, null, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            if (rows.Count > 0 && (rows[0].GetBool("HasOutput") ?? false))
            {
                anySource = true;
                var r = rows[0];
                status.AzureAdJoined = Eq(r.GetString("AzureAdJoined"), "YES");
                status.DomainJoined = Eq(r.GetString("DomainJoined"), "YES");
                status.WorkplaceJoined = Eq(r.GetString("WorkplaceJoined"), "YES");
                status.TenantName = r.GetString("TenantName") ?? "";
                status.TenantId = r.GetString("TenantId") ?? "";
                var mdmUrl = r.GetString("MdmUrl") ?? "";

                if (status.AzureAdJoined) details.Add($"dsregcmd: AzureAdJoined = YES{(status.TenantName.Length > 0 ? $" (tenant {status.TenantName})" : "")}.");
                if (status.DomainJoined) details.Add("dsregcmd: DomainJoined = YES (domínio on-premises).");
                if (status.WorkplaceJoined) details.Add("dsregcmd: WorkplaceJoined = YES (conta corporativa adicionada).");
                if (!string.IsNullOrEmpty(mdmUrl))
                {
                    if (status.AzureAdJoined)
                    {
                        // MDM de DISPOSITIVO de verdade: AAD join + URL de MDM.
                        directMdmEvidence = true;
                        details.Add($"dsregcmd: MDM URL configurada ({Trunc(mdmUrl, 60)}) com Entra ID join — gerenciamento do dispositivo.");
                    }
                    else
                    {
                        // MdmUrl SEM AAD join vem da seção de conta de trabalho
                        // (workplace/MAM) — gestão de APPS de uma conta corporativa
                        // adicionada pelo usuário, não do dispositivo. Não conta
                        // como evidência de Autopilot.
                        details.Add("dsregcmd: MDM URL presente sem Entra ID join (conta de trabalho/MAM — não indica Autopilot do dispositivo).");
                    }
                }
                if (!status.AzureAdJoined && !status.DomainJoined && !status.WorkplaceJoined && string.IsNullOrEmpty(mdmUrl))
                {
                    details.Add("dsregcmd: sem join a Entra ID/domínio/workplace e sem MDM.");
                }
            }
            else
            {
                details.Add("dsregcmd: sem saída (ferramenta indisponível nesta edição do Windows).");
            }
        }
        catch (Exception ex)
        {
            details.Add("dsregcmd: falha ao executar.");
            _logger.LogDebug(ex, "Autopilot: dsregcmd falhou");
        }

        // ============================================================
        // 3. REDUNDÂNCIA — Event log do Autopilot
        // ============================================================
        try
        {
            const string script = @"
$ErrorActionPreference = 'SilentlyContinue'
$name = 'Microsoft-Windows-ModernDeployment-Diagnostics-Provider/Autopilot'
$log = Get-WinEvent -ListLog $name -ErrorAction SilentlyContinue
if ($log) {
    $count = [long]($log.RecordCount)
    $last = ''
    if ($count -gt 0) {
        $ev = Get-WinEvent -LogName $name -MaxEvents 1 -ErrorAction SilentlyContinue
        if ($ev) { $last = $ev[0].TimeCreated.ToString('yyyy-MM-dd HH:mm') }
    }
    [pscustomobject]@{ Exists = $true; Count = $count; LastTime = [string]$last }
} else {
    [pscustomobject]@{ Exists = $false; Count = [long]0; LastTime = '' }
}
";
            var rows = await _ps.InvokeAsync(script, null, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            if (rows.Count > 0)
            {
                anySource = true;
                var exists = rows[0].GetBool("Exists") ?? false;
                var count = rows[0].GetLong("Count") ?? 0;
                var last = rows[0].GetString("LastTime") ?? "";

                if (exists && count > 0)
                {
                    status.AutopilotEventsFound = true;
                    details.Add($"Event log do Autopilot: {count} evento(s){(last.Length > 0 ? $", último em {last}" : "")} — o fluxo de Autopilot já rodou nesta instalação.");
                }
                else if (exists)
                {
                    details.Add("Event log do Autopilot existe mas está vazio (fluxo nunca rodou nesta instalação).");
                }
                else
                {
                    details.Add("Event log do Autopilot não existe nesta máquina.");
                }
            }
        }
        catch (Exception ex)
        {
            details.Add("Event log do Autopilot: falha ao consultar.");
            _logger.LogDebug(ex, "Autopilot: event log falhou");
        }

        // ============================================================
        // 4. REDUNDÂNCIA — Registro do Windows
        // ============================================================
        try
        {
            anySource = true;

            // 4a. Perfil cloud-assigned (o que o OOBE grava ao aplicar o perfil).
            //     ATENÇÃO: o nó existe VAZIO por padrão em qualquer Windows 11 —
            //     só conta como relevante quando há tenant de verdade.
            using (var diag = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Provisioning\Diagnostics\Autopilot"))
            {
                if (diag is not null)
                {
                    var tenantId = diag.GetValue("CloudAssignedTenantId") as string;
                    var tenantDomain = diag.GetValue("CloudAssignedTenantDomain") as string;
                    if (IsRealTenant(tenantId) || !string.IsNullOrWhiteSpace(tenantDomain))
                    {
                        registryRelevant = true;
                        directMdmEvidence = true;
                        if (string.IsNullOrEmpty(status.TenantId) && tenantId is not null) status.TenantId = tenantId.Trim('{', '}');
                        if (string.IsNullOrEmpty(status.TenantName) && tenantDomain is not null) status.TenantName = tenantDomain;
                        details.Add($"Registro: perfil Autopilot cloud-assigned presente (tenant {tenantDomain ?? tenantId}) — evidência direta.");
                    }
                    else if (diag.ValueCount > 0)
                    {
                        registryRelevant = true;
                        details.Add($"Registro: Provisioning\\Diagnostics\\Autopilot com {diag.ValueCount} valor(es), sem tenant.");
                    }
                }
            }

            // 4b. HKLM\SOFTWARE\Microsoft\Windows\Autopilot. As subchaves
            //     DevicePreparation e EnrollmentStatusTracking existem VAZIAS de
            //     fábrica no Win11 — só contam se tiverem conteúdo, ou se houver
            //     valores/subchaves fora desse par padrão.
            using (var ap = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\Autopilot"))
            {
                if (ap is not null)
                {
                    var defaultSubkeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        { "DevicePreparation", "EnrollmentStatusTracking" };
                    var hasContent = ap.ValueCount > 0;
                    foreach (var sub in ap.GetSubKeyNames())
                    {
                        if (!defaultSubkeys.Contains(sub)) { hasContent = true; break; }
                        try
                        {
                            using var sk = ap.OpenSubKey(sub);
                            if (sk is not null && (sk.ValueCount > 0 || sk.SubKeyCount > 0)) { hasContent = true; break; }
                        }
                        catch { }
                    }
                    if (hasContent)
                    {
                        registryRelevant = true;
                        details.Add($"Registro: HKLM\\...\\Windows\\Autopilot com conteúdo ({ap.ValueCount} valor(es), {ap.SubKeyCount} subchave(s)).");
                    }
                }
            }

            // 4c. Cache de política do Autopilot.
            using (var cache = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Provisioning\AutopilotPolicyCache"))
            {
                if (cache is not null && (cache.ValueCount > 0 || cache.SubKeyCount > 0))
                {
                    registryRelevant = true;
                    directMdmEvidence = true;
                    details.Add("Registro: AutopilotPolicyCache presente (perfil de Autopilot já foi baixado) — evidência direta.");
                }
            }

            // 4d. Enrollments MDM (Intune e similares).
            using (var enrollments = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Enrollments"))
            {
                if (enrollments is not null)
                {
                    // Subchaves utilitárias que não são enrollments de verdade.
                    var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        { "Context", "Ownership", "Status", "ValidNodePaths" };

                    // Autoridades INTERNAS do Windows — existem em qualquer
                    // instalação (inclusive máquinas domésticas) e NÃO indicam
                    // MDM real. Confirmado empiricamente: Local/Cloud/Deploy
                    // Authority e WMI_Bridge_SCCM_Server vêm de fábrica.
                    var builtinProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        { "Local Authority", "Cloud Authority", "Deploy Authority", "WMI_Bridge_SCCM_Server" };

                    foreach (var name in enrollments.GetSubKeyNames())
                    {
                        if (skip.Contains(name)) continue;
                        try
                        {
                            using var e = enrollments.OpenSubKey(name);
                            if (e is null) continue;
                            var upn = e.GetValue("UPN") as string;
                            var provider = e.GetValue("ProviderID") as string;
                            var discovery = e.GetValue("DiscoveryServiceFullURL") as string;

                            if (provider is not null && builtinProviders.Contains(provider)) continue;

                            var hasUpn = !string.IsNullOrWhiteSpace(upn);
                            var hasUrl = !string.IsNullOrWhiteSpace(discovery);

                            // MAM (gestão de APPS de uma conta de trabalho que o
                            // usuário adicionou — URL wip.mam.manage.microsoft.com,
                            // EnrollmentType 6) NÃO é MDM do dispositivo e existe
                            // em muitos PCs pessoais com conta corporativa no Office.
                            var enrollmentType = e.GetValue("EnrollmentType") is int et ? et : -1;
                            var isMam = (discovery ?? "").IndexOf("mam.", StringComparison.OrdinalIgnoreCase) >= 0
                                     || enrollmentType == 6;

                            if (isMam)
                            {
                                details.Add($"Registro: enrollment MAM (apps de conta de trabalho{(hasUpn ? $" — {upn}" : "")}) — não indica Autopilot do dispositivo.");
                            }
                            else if (hasUrl)
                            {
                                // Enrollment MDM de DISPOSITIVO: tem URL de discovery
                                // real (Intune: enrollment.manage.microsoft.com).
                                registryRelevant = true;
                                directMdmEvidence = true;
                                var isIntune = (discovery ?? "").IndexOf("manage.microsoft.com", StringComparison.OrdinalIgnoreCase) >= 0
                                            || string.Equals(provider, "MS DM Server", StringComparison.OrdinalIgnoreCase);
                                details.Add($"Registro: enrollment MDM {(isIntune ? "Intune" : provider ?? "desconhecido")}{(hasUpn ? $" ({upn})" : "")} (tipo {enrollmentType}) — evidência direta.");
                            }
                            else if (hasUpn || !string.IsNullOrWhiteSpace(provider))
                            {
                                // UPN ou provider sem URL de discovery: sinal fraco.
                                registryRelevant = true;
                                details.Add($"Registro: enrollment '{provider ?? upn}' sem URL de discovery (sinal fraco).");
                            }
                        }
                        catch
                        {
                            // Subchave sem permissão — segue para a próxima.
                        }
                    }
                }
            }
        }
        catch (System.Security.SecurityException)
        {
            details.Add("Registro: acesso negado a uma das chaves (permissão).");
        }
        catch (Exception ex)
        {
            details.Add("Registro: falha ao ler chaves de Autopilot/Enrollments.");
            _logger.LogDebug(ex, "Autopilot: registro falhou");
        }

        // ============================================================
        // 5. ARQUIVO DE PERFIL — PCs antigos/provisionados ainda guardam o
        //    JSON do perfil de Autopilot baixado durante o OOBE. A simples
        //    presença é evidência direta de que a máquina ESTÁ registrada
        //    num tenant (o arquivo só é gravado quando o DDS devolve perfil),
        //    e o conteúdo ainda revela o tenant.
        // ============================================================
        try
        {
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var candidates = new (string Path, string Desc)[]
            {
                (System.IO.Path.Combine(win, "ServiceState", "wmansvc", "AutopilotDDSZTDFile.json"),
                 "AutopilotDDSZTDFile.json (cache do perfil baixado no OOBE)"),
                (System.IO.Path.Combine(win, "Provisioning", "Autopilot", "AutopilotDDSZTDFile.json"),
                 "AutopilotDDSZTDFile.json (Provisioning)"),
                (System.IO.Path.Combine(win, "Provisioning", "Autopilot", "AutopilotConfigurationFile.json"),
                 "AutopilotConfigurationFile.json (Autopilot for existing devices)"),
            };
            anySource = true;
            var anyFile = false;

            foreach (var (path, desc) in candidates)
            {
                if (!System.IO.File.Exists(path)) continue;
                anyFile = true;
                status.ProfileFileFound = true;
                directMdmEvidence = true;

                // Tenta extrair o tenant do JSON (campos CloudAssigned*).
                string? fileTenantDomain = null, fileTenantId = null, deviceName = null;
                try
                {
                    using var fs = System.IO.File.OpenRead(path);
                    using var doc = System.Text.Json.JsonDocument.Parse(fs);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("CloudAssignedTenantDomain", out var td)) fileTenantDomain = td.GetString();
                    if (root.TryGetProperty("CloudAssignedTenantId", out var ti)) fileTenantId = ti.GetString();
                    if (root.TryGetProperty("CloudAssignedDeviceName", out var dn)) deviceName = dn.GetString();
                }
                catch
                {
                    // Conteúdo ilegível/corrompido — presença já é evidência.
                }

                if (!string.IsNullOrWhiteSpace(fileTenantDomain) && string.IsNullOrEmpty(status.TenantName))
                    status.TenantName = fileTenantDomain!;
                if (IsRealTenant(fileTenantId) && string.IsNullOrEmpty(status.TenantId))
                    status.TenantId = fileTenantId!.Trim('{', '}');

                var extra = !string.IsNullOrWhiteSpace(fileTenantDomain) ? $" — tenant {fileTenantDomain}"
                          : IsRealTenant(fileTenantId) ? $" — tenant {fileTenantId}"
                          : "";
                if (!string.IsNullOrWhiteSpace(deviceName)) extra += $", nome atribuído '{deviceName}'";
                details.Add($"Arquivo: {desc} presente{extra} — evidência direta de registro no Autopilot.");
            }

            if (!anyFile)
            {
                details.Add("Arquivo de perfil Autopilot não encontrado no disco (normal em máquinas não registradas ou reinstaladas do zero).");
            }
        }
        catch (UnauthorizedAccessException)
        {
            details.Add("Arquivo de perfil Autopilot: acesso negado (rode como administrador).");
        }
        catch (Exception ex)
        {
            details.Add("Arquivo de perfil Autopilot: falha ao verificar.");
            _logger.LogDebug(ex, "Autopilot: checagem de arquivo falhou");
        }

        // Método futuro — verificação online autenticada no tenant.
        details.Add("Verificação online no tenant (login Intune/Entra): em desenvolvimento — disponível em versão futura.");

        // ============================================================
        // 6. PONTUAÇÃO E VEREDITO
        // ============================================================
        var score = 0;
        if (directMdmEvidence) score += 50;
        if (status.AzureAdJoined) score += 25;
        if (status.AutopilotEventsFound) score += 15;
        if (registryRelevant) score += 10;
        score = Math.Min(100, score);

        status.Score = score;
        status.Confidence = score >= 70 ? "High" : score >= 30 ? "Medium" : "Low";
        status.IsLikelyAutopilot = score >= 30;
        status.AnySourceAvailable = anySource;

        if (!anySource)
        {
            details.Add("Nenhuma fonte pôde ser consultada — resultado indeterminado.");
        }

        _logger.LogInformation(
            "Autopilot check: score {Score} ({Confidence}) — MDM direto: {Mdm}, AAD: {Aad}, eventos: {Ev}, registro: {Reg}, arquivo de perfil: {File}",
            score, status.Confidence, directMdmEvidence, status.AzureAdJoined, status.AutopilotEventsFound, registryRelevant, status.ProfileFileFound);
        foreach (var d in details)
        {
            _logger.LogInformation("Autopilot evidência: {Detail}", d);
        }

        return status;
    }

    private static bool Eq(string? a, string b) =>
        string.Equals(a ?? "", b, StringComparison.OrdinalIgnoreCase);

    private static string Trunc(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";

    /// <summary>Um tenant é real quando não é nulo/vazio nem o GUID zero.</summary>
    private static bool IsRealTenant(string? tenant)
    {
        if (string.IsNullOrWhiteSpace(tenant)) return false;
        var t = tenant.Trim().Trim('{', '}');
        return t != "00000000-0000-0000-0000-000000000000" && t.Length >= 8;
    }
}
