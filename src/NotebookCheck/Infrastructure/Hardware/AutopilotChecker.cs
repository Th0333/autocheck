using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
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
///      correlação com o serviço ZTD (EstablishedCorrelations\ZtdRegistrationId,
///      que só existe quando o serviço da Microsoft reconheceu o hardware hash),
///      HKLM\SOFTWARE\Microsoft\Windows\Autopilot, AutopilotPolicyCache,
///      HKLM\SOFTWARE\Microsoft\Enrollments (enrollments MDM/Intune) e
///      Provisioning\OMADM\Accounts (contas de gerenciamento ativas).
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
/// LIMITE IMPORTANTE: tudo isso são RASTROS locais. Uma máquina registrada
/// no Autopilot do antigo dono e formatada do zero não deixa rastro nenhum —
/// o registro vive no tenant, e só o OOBE com internet revela. Por isso a
/// ausência de rastros NÃO prova que a máquina está livre; o veredito é
/// "sem rastros", e o técnico confirma na inspeção (campo AutopilotConfirmed).
///
/// Pontuação (interna, para ordenar os vereditos): +50 evidência direta de
/// MDM/Autopilot • +25 AzureAdJoined • +15 eventos de Autopilot • +10 chaves
/// de registro relevantes. 0–29 = Low, 30–69 = Medium, 70–100 = High.
/// Cada fonte é isolada em try/catch: falta de permissão ou ausência da fonte
/// nunca derruba o checker — vira uma linha em Details.
/// </summary>
public sealed class AutopilotChecker
{
    private readonly IWmiQueryRunner _wmi;
    private readonly ILogger _logger;

    public AutopilotChecker(IWmiQueryRunner wmi, ILogger logger)
    {
        _wmi = wmi;
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
        // 2. REDUNDÂNCIA — dsregcmd /status, executado DIRETO. Antes passava
        //    pelo runner de PowerShell, que trata qualquer coisa no stderr
        //    como erro — na bancada isto virava "falha ao executar" e a fonte
        //    inteira era perdida (log de 18/08).
        // ============================================================
        try
        {
            var (_, output) = await RunAsync("dsregcmd.exe", "/status", TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            if (output.Length > 0)
            {
                anySource = true;
                string Pick(string pattern)
                {
                    var m = Regex.Match(output, pattern, RegexOptions.IgnoreCase);
                    return m.Success ? m.Groups[1].Value.Trim() : "";
                }
                status.AzureAdJoined = Eq(Pick(@"AzureAdJoined\s*:\s*(YES|NO)"), "YES");
                status.DomainJoined = Eq(Pick(@"DomainJoined\s*:\s*(YES|NO)"), "YES");
                status.WorkplaceJoined = Eq(Pick(@"WorkplaceJoined\s*:\s*(YES|NO)"), "YES");
                status.TenantName = Pick(@"TenantName\s*:\s*(.+?)\r?\n");
                status.TenantId = Pick(@"TenantId\s*:\s*([0-9a-fA-F-]+)");
                var mdmUrl = Pick(@"MdmUrl\s*:\s*(\S+)");

                if (status.AzureAdJoined) details.Add($"dsregcmd: AzureAdJoined = YES{(status.TenantName.Length > 0 ? $" (tenant {status.TenantName})" : "")}.");
                if (status.DomainJoined) details.Add("dsregcmd: DomainJoined = YES (domínio on-premises).");
                if (status.WorkplaceJoined) details.Add("dsregcmd: WorkplaceJoined = YES (conta corporativa adicionada).");
                if (!string.IsNullOrEmpty(mdmUrl))
                {
                    if (status.AzureAdJoined)
                    {
                        // MDM de DISPOSITIVO de verdade: AAD join + URL de MDM.
                        directMdmEvidence = true;
                        details.Add($"dsregcmd: MDM URL configurada ({Trunc(mdmUrl, 60)}) com Entra ID join — gerenciamento do dispositivo (evidência direta).");
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
        // 3. REDUNDÂNCIA — Event logs do Autopilot, via wevtutil (o runner de
        //    PowerShell acusava erro quando o log não existia e a fonte era
        //    perdida). O Windows tem DOIS canais: ModernDeployment (fluxo do
        //    OOBE/ESP) e Provisioning (perfil baixado do serviço ZTD).
        //    Qualquer um com registro = o fluxo de Autopilot já rodou aqui.
        // ============================================================
        var eventLogs = new (string Name, string Short)[]
        {
            ("Microsoft-Windows-ModernDeployment-Diagnostics-Provider/Autopilot", "ModernDeployment"),
            ("Microsoft-Windows-Provisioning-Diagnostics-Provider/AutoPilot", "Provisioning"),
        };
        foreach (var (logName, shortName) in eventLogs)
        {
            try
            {
                var (exit, info) = await RunAsync("wevtutil.exe", $"gli \"{logName}\"", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                if (exit != 0 || info.Length == 0)
                {
                    details.Add($"Event log do Autopilot ({shortName}) não existe nesta máquina.");
                    continue;
                }
                anySource = true;
                var cm = Regex.Match(info, @"numberOfLogRecords:\s*(\d+)", RegexOptions.IgnoreCase);
                var count = cm.Success ? long.Parse(cm.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                if (count > 0)
                {
                    status.AutopilotEventsFound = true;
                    var last = "";
                    try
                    {
                        // /f:xml porque os rótulos do /f:text são localizados; o
                        // atributo SystemTime é estável.
                        var (_, xml) = await RunAsync("wevtutil.exe", $"qe \"{logName}\" /c:1 /rd:true /f:xml", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                        var tm = Regex.Match(xml, @"SystemTime=['""]([^'""]+)['""]");
                        if (tm.Success && DateTime.TryParse(tm.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                            last = dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                    }
                    catch { /* só perde a data */ }
                    details.Add($"Event log do Autopilot ({shortName}): {count} evento(s){(last.Length > 0 ? $", último em {last}" : "")} — o fluxo de Autopilot já rodou nesta instalação.");
                }
                else
                {
                    details.Add($"Event log do Autopilot ({shortName}) existe mas está vazio (fluxo nunca rodou nesta instalação).");
                }
            }
            catch (Exception ex)
            {
                details.Add($"Event log do Autopilot ({shortName}): falha ao consultar.");
                _logger.LogDebug(ex, "Autopilot: event log {Log} falhou", logName);
            }
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
                    else
                    {
                        // Os valores CloudAssigned* existem VAZIOS em qualquer
                        // Windows (sete deles, de fábrica). Contar "ValueCount > 0"
                        // dava +10 para todo PC comum. Só contam quando o serviço
                        // preencheu algum de verdade.
                        var filled = diag.GetValueNames()
                            .Where(n => n.StartsWith("CloudAssigned", StringComparison.OrdinalIgnoreCase))
                            .Where(n => diag.GetValue(n) switch
                            {
                                string str => !string.IsNullOrWhiteSpace(str),
                                int i => i != 0,
                                _ => false,
                            })
                            .ToList();
                        if (filled.Count > 0)
                        {
                            registryRelevant = true;
                            details.Add($"Registro: perfil Autopilot com {filled.Count} campo(s) preenchido(s) ({string.Join(", ", filled.Take(4))}), sem tenant — sinal fraco.");
                        }
                        else
                        {
                            details.Add("Registro: Provisioning\\Diagnostics\\Autopilot só com os valores vazios de fábrica (0 pontos).");
                        }
                    }

                    // 4a-ii. Correlação com o serviço ZTD: o OOBE grava aqui o
                    //        ZtdRegistrationId devolvido pelo serviço da Microsoft
                    //        quando ele RECONHECE o hardware hash. É o rastro mais
                    //        direto que existe localmente de "este hardware está
                    //        registrado em algum tenant".
                    using var corr = diag.OpenSubKey("EstablishedCorrelations");
                    if (corr is not null)
                    {
                        var ztd = corr.GetValue("ZtdRegistrationId") as string;
                        var svc = corr.GetValue("AutopilotServiceCorrelationId") as string;
                        if (IsRealTenant(ztd))
                        {
                            registryRelevant = true;
                            directMdmEvidence = true;
                            status.ZtdRegistrationId = ztd!.Trim('{', '}');
                            details.Add($"Registro: ZtdRegistrationId {status.ZtdRegistrationId} — o serviço Autopilot da Microsoft reconheceu o hardware hash desta máquina (evidência direta).");
                        }
                        else if (!string.IsNullOrWhiteSpace(svc))
                        {
                            registryRelevant = true;
                            details.Add("Registro: EstablishedCorrelations com id de correlação do serviço, sem ZtdRegistrationId (a máquina consultou o serviço; sinal fraco).");
                        }
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

            // 4c. Cache de política do Autopilot. ATENÇÃO: a chave existe em
            //     QUALQUER Windows cujo OOBE consultou o serviço — com
            //     ProfileAvailable = 0 e tenant vazio quando a resposta foi "sem
            //     perfil". Era isto que fazia todo PC comum pontuar 50 e sair
            //     como "Possível/Provável". Só é evidência quando veio perfil
            //     de verdade; sem perfil vira um sinal NEGATIVO com data: naquele
            //     dia o serviço da Microsoft não reconheceu este hardware.
            using (var cache = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Provisioning\AutopilotPolicyCache"))
            {
                if (cache is not null)
                {
                    var profileAvailable = cache.GetValue("ProfileAvailable") is int pa && pa != 0;
                    var policyJson = cache.GetValue("PolicyJsonCache") as string;
                    string? cacheTenant = null;
                    DateTime? queriedAt = null;
                    if (!string.IsNullOrWhiteSpace(policyJson))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(policyJson);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("AutopilotCreationDate", out var cd)
                                && DateTime.TryParse(cd.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                                queriedAt = dt;
                            if (root.TryGetProperty("CloudAssignedAadServerData", out var aad) && aad.ValueKind == JsonValueKind.String)
                            {
                                using var inner = JsonDocument.Parse(aad.GetString() ?? "{}");
                                if (inner.RootElement.TryGetProperty("ZeroTouchConfig", out var ztc))
                                {
                                    foreach (var f in new[] { "CloudAssignedTenantDomain", "CloudAssignedTenantUpn" })
                                    {
                                        if (ztc.TryGetProperty(f, out var v) && v.ValueKind == JsonValueKind.String
                                            && !string.IsNullOrWhiteSpace(v.GetString()))
                                        {
                                            cacheTenant = v.GetString();
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // JSON ilegível — trata como "sem perfil"
                        }
                    }

                    if (profileAvailable || cacheTenant is not null)
                    {
                        registryRelevant = true;
                        directMdmEvidence = true;
                        if (cacheTenant is not null && string.IsNullOrEmpty(status.TenantName)) status.TenantName = cacheTenant;
                        details.Add($"Registro: AutopilotPolicyCache com perfil{(cacheTenant is not null ? $" do tenant {cacheTenant}" : "")} — o serviço Autopilot devolveu perfil para este hardware (evidência direta).");
                    }
                    else if (cache.ValueCount > 0 || cache.SubKeyCount > 0)
                    {
                        status.ServiceQueriedAt = queriedAt;
                        status.ServiceReturnedNoProfile = true;
                        var when = queriedAt is DateTime q ? $" em {q.ToLocalTime():dd/MM/yyyy}" : "";
                        details.Add($"Registro: o OOBE consultou o serviço Autopilot{when} e NÃO recebeu perfil (ProfileAvailable = 0) — naquela data este hardware não estava registrado em nenhum tenant.");
                    }
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
        // 4e. Contas OMA-DM ativas (Provisioning\OMADM\Accounts). Cada subchave
        //     é uma conta de gerenciamento (Intune, outro MDM) em uso — não
        //     existe em máquina doméstica. Isolado do bloco 4 para uma falha
        //     aqui não apagar o que já foi lido.
        // ============================================================
        try
        {
            using var omadm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Provisioning\OMADM\Accounts");
            if (omadm is not null)
            {
                var accounts = omadm.GetSubKeyNames();
                if (accounts.Length > 0)
                {
                    registryRelevant = true;
                    directMdmEvidence = true;
                    details.Add($"Registro: {accounts.Length} conta(s) OMA-DM ativa(s) — a máquina está sendo gerenciada por MDM (evidência direta).");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Autopilot: OMADM falhou");
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
        status.DirectEvidence = directMdmEvidence;
        status.DirectEvidenceCount = details.Count(d => d.Contains("evidência direta", StringComparison.OrdinalIgnoreCase));

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

    /// <summary>
    /// Roda um utilitário do Windows e devolve (código de saída, stdout).
    /// Sem PowerShell no meio: o runner de PS trata qualquer linha no stderr
    /// como falha, e dsregcmd/wevtutil escrevem lá em situações normais.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunAsync(string exe, string args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = new Process { StartInfo = psi };
        p.Start();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* já saiu */ }
            throw new TimeoutException($"{exe} {args} excedeu {timeout.TotalSeconds:0}s");
        }
        var output = await stdout.ConfigureAwait(false);
        _ = await stderr.ConfigureAwait(false);
        return (p.ExitCode, output ?? "");
    }

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
