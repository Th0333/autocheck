# Diagnóstico do checker de Autopilot do NotebookCheck.
# Rode em QUALQUER PC (de preferência como administrador) para ver exatamente
# o que cada fonte retorna e qual seria a pontuação — espelha a lógica do
# AutopilotChecker.cs. Compatível com Windows PowerShell 5.1 e pwsh 7+.
#
# Uso:  powershell -ExecutionPolicy Bypass -File diag-autopilot.ps1

$ErrorActionPreference = 'SilentlyContinue'
$score = 0
$direct = $false
$registryRelevant = $false

Write-Host ""
Write-Host "=== Diagnóstico Autopilot (NotebookCheck) ===" -ForegroundColor Cyan
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Host ("Admin: {0}" -f $isAdmin)
Write-Host ""

# ----------------------------------------------------------------------
Write-Host "[1] MDM WMI bridge (root\cimv2\mdm\dmmap, MDM_DevDetail_Ext01)" -ForegroundColor Yellow
try {
    $mdm = Get-CimInstance -Namespace 'root/cimv2/mdm/dmmap' -ClassName 'MDM_DevDetail_Ext01' -ErrorAction Stop
    $n = @($mdm).Count
    Write-Host "    Instâncias: $n  (informativo — responde em QUALQUER Windows com admin; 0 pontos)"
} catch {
    Write-Host "    Erro: $($_.Exception.Message.Trim())  (0 pontos)"
}

# ----------------------------------------------------------------------
Write-Host "[2] dsregcmd /status" -ForegroundColor Yellow
$out = & dsregcmd /status 2>$null | Out-String
$aad = $false
if ($out -match 'AzureAdJoined\s*:\s*(YES|NO)') { $aad = ($matches[1] -eq 'YES'); Write-Host "    AzureAdJoined: $($matches[1])" }
if ($out -match 'DomainJoined\s*:\s*(YES|NO)') { Write-Host "    DomainJoined: $($matches[1])" }
if ($out -match 'WorkplaceJoined\s*:\s*(YES|NO)') { Write-Host "    WorkplaceJoined: $($matches[1])" }
if ($out -match 'TenantName\s*:\s*(.+?)\r?\n') { Write-Host "    TenantName: $($matches[1].Trim())" }
$mdmUrl = ''
if ($out -match 'MdmUrl\s*:\s*(\S+)') { $mdmUrl = $matches[1]; Write-Host "    MdmUrl: $mdmUrl" } else { Write-Host "    MdmUrl: (vazio)" }
if ($aad) { $score += 25; Write-Host "    => AzureAdJoined: +25" -ForegroundColor Green }
if ($mdmUrl -and $aad) { $direct = $true; Write-Host "    => MDM URL + Entra ID join: evidência DIRETA" -ForegroundColor Green }
elseif ($mdmUrl) { Write-Host "    => MDM URL sem AAD join (conta de trabalho/MAM): NÃO conta" -ForegroundColor DarkYellow }

# ----------------------------------------------------------------------
Write-Host "[3] Event log do Autopilot" -ForegroundColor Yellow
$log = Get-WinEvent -ListLog 'Microsoft-Windows-ModernDeployment-Diagnostics-Provider/Autopilot' -ErrorAction SilentlyContinue
if ($log) {
    Write-Host "    Log existe, RecordCount = $($log.RecordCount)"
    if ($log.RecordCount -gt 0) { $score += 15; Write-Host "    => eventos encontrados: +15" -ForegroundColor Green }
} else {
    Write-Host "    Log não existe"
}

# ----------------------------------------------------------------------
Write-Host "[4] Registro" -ForegroundColor Yellow

# 4a. Perfil cloud-assigned
$diag = Get-Item 'HKLM:\SOFTWARE\Microsoft\Provisioning\Diagnostics\Autopilot' -ErrorAction SilentlyContinue
if ($diag) {
    $tid = $diag.GetValue('CloudAssignedTenantId')
    $tdom = $diag.GetValue('CloudAssignedTenantDomain')
    $zero = '00000000-0000-0000-0000-000000000000'
    $realTenant = ($tid -and ($tid.ToString().Trim('{','}') -ne $zero) -and $tid.ToString().Length -ge 8) -or $tdom
    if ($realTenant) {
        $direct = $true; $registryRelevant = $true
        Write-Host "    Prov\Diag\Autopilot: tenant atribuído ($tdom $tid) => evidência DIRETA" -ForegroundColor Green
    } elseif ($diag.ValueCount -gt 0) {
        $registryRelevant = $true
        Write-Host "    Prov\Diag\Autopilot: $($diag.ValueCount) valor(es), sem tenant (sinal fraco)"
    } else {
        Write-Host "    Prov\Diag\Autopilot: existe vazio (padrão do Windows — 0 pontos)"
    }
}

# 4b. Windows\Autopilot (DevicePreparation/EnrollmentStatusTracking vazias são padrão)
$ap = Get-Item 'HKLM:\SOFTWARE\Microsoft\Windows\Autopilot' -ErrorAction SilentlyContinue
if ($ap) {
    $defaults = @('DevicePreparation','EnrollmentStatusTracking')
    $hasContent = $ap.ValueCount -gt 0
    foreach ($sub in $ap.GetSubKeyNames()) {
        if ($defaults -notcontains $sub) { $hasContent = $true; break }
        $sk = Get-Item "HKLM:\SOFTWARE\Microsoft\Windows\Autopilot\$sub" -ErrorAction SilentlyContinue
        if ($sk -and ($sk.ValueCount -gt 0 -or $sk.SubKeyCount -gt 0)) { $hasContent = $true; break }
    }
    if ($hasContent) { $registryRelevant = $true; Write-Host "    Windows\Autopilot: COM conteúdo (sinal)" -ForegroundColor Green }
    else { Write-Host "    Windows\Autopilot: só estrutura padrão vazia (0 pontos)" }
}

# 4c. AutopilotPolicyCache
$cache = Get-Item 'HKLM:\SOFTWARE\Microsoft\Provisioning\AutopilotPolicyCache' -ErrorAction SilentlyContinue
if ($cache -and ($cache.ValueCount -gt 0 -or $cache.SubKeyCount -gt 0)) {
    $direct = $true; $registryRelevant = $true
    Write-Host "    AutopilotPolicyCache: presente => evidência DIRETA" -ForegroundColor Green
} else {
    Write-Host "    AutopilotPolicyCache: ausente"
}

# 4d. Enrollments
$builtin = @('Local Authority','Cloud Authority','Deploy Authority','WMI_Bridge_SCCM_Server')
$skipKeys = @('Context','Ownership','Status','ValidNodePaths')
$ens = Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Enrollments' -ErrorAction SilentlyContinue | Where-Object { $skipKeys -notcontains $_.PSChildName }
$found = 0
foreach ($e in @($ens)) {
    $p = Get-ItemProperty $e.PSPath -ErrorAction SilentlyContinue
    $upn = $p.UPN; $prov = $p.ProviderID; $url = $p.DiscoveryServiceFullURL; $etype = $p.EnrollmentType
    if (-not $upn -and -not $prov -and -not $url) { continue }
    if ($prov -and ($builtin -contains $prov)) { continue }
    $found++
    $isMam = ($url -and $url -match 'mam\.') -or ($etype -eq 6)
    $line = "    Enrollment $($e.PSChildName): Provider='$prov' UPN='$upn' URL='$url' Type=$etype"
    if ($isMam) {
        Write-Host "$line => MAM (conta de trabalho, NÃO conta)" -ForegroundColor DarkYellow
    } elseif ($url) {
        $direct = $true; $registryRelevant = $true
        Write-Host "$line => MDM de DISPOSITIVO: evidência DIRETA" -ForegroundColor Green
    } else {
        $registryRelevant = $true
        Write-Host "$line => sem URL de discovery (sinal fraco +10)" -ForegroundColor DarkYellow
    }
}
if ($found -eq 0) { Write-Host "    Enrollments: nenhum não-interno com dados (0 pontos)" }

# ----------------------------------------------------------------------
if ($direct) { $score += 50 }
if ($registryRelevant) { $score += 10 }
if ($score -gt 100) { $score = 100 }
$conf = if ($score -ge 70) { 'High (Provável)' } elseif ($score -ge 30) { 'Medium (Possível)' } else { 'Low (Improvável)' }

Write-Host ""
Write-Host "=== RESULTADO ===" -ForegroundColor Cyan
Write-Host ("Evidência direta: {0}  |  Registro relevante: {1}" -f $direct, $registryRelevant)
Write-Host ("PONTUAÇÃO: {0}/100  =>  {1}" -f $score, $conf) -ForegroundColor Cyan
Write-Host ""
Write-Host "Envie esta saída completa para análise se o resultado parecer errado."
