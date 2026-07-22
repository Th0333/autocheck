using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NotebookCheck.Domain.Enums;
using NotebookCheck.Domain.Models;
using NotebookCheck.Infrastructure.Abstractions;

namespace NotebookCheck.Infrastructure.Hardware;

/// <summary>
/// Lê o estado das senhas de BIOS em máquinas Dell pelo <b>Dell Command |
/// PowerShell Provider</b> (módulo <c>DellBIOSProvider</c>, publicado pela
/// própria Dell no PSGallery).
///
/// Existe porque o caminho por WMI (<c>root\dcim\sysman</c>) depende do Dell
/// Command | Monitor, que é um instalador pesado e some quando a máquina é
/// formatada — o caso normal de um recondicionador. O provider faz o mesmo
/// trabalho, instala com um <c>Install-Module</c> e é a via oficial da Dell.
///
/// Continua exigindo <b>administrador</b>: o provider conversa com a interface
/// SMBIOS da Dell, que nega acesso a usuário comum.
/// </summary>
public sealed class DellBiosPasswordReader
{
    private readonly IPowerShellRunner _ps;
    private readonly ILogger<DellBiosPasswordReader> _logger;

    public const string NomeModulo = "DellBIOSProvider";

    public DellBiosPasswordReader(IPowerShellRunner ps, ILogger<DellBiosPasswordReader> logger)
    {
        _ps = ps;
        _logger = logger;
    }

    /// <summary>Resultado de uma tentativa de leitura ou instalação.</summary>
    public sealed record Resultado(BiosSecurity? Leitura, string? Erro)
    {
        public bool Ok => Leitura is not null;
    }

    /// <summary>O módulo da Dell já está instalado nesta máquina?</summary>
    public async Task<bool> ModuloInstaladoAsync(CancellationToken ct)
    {
        try
        {
            var rows = await _ps.InvokeAsync(
                $"if (Get-Module -ListAvailable -Name {NomeModulo}) {{ [pscustomobject]@{{ Tem = $true }} }}",
                null, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            return rows.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha ao verificar {Modulo}", NomeModulo);
            return false;
        }
    }

    /// <summary>
    /// Instala o módulo do PSGallery. Precisa de internet e de administrador
    /// (escopo AllUsers, para o teste valer para qualquer conta da bancada).
    /// </summary>
    public async Task<string?> InstalarModuloAsync(CancellationToken ct)
    {
        // NuGet + PSGallery confiável antes do Install-Module, senão o cmdlet
        // trava esperando confirmação interativa que ninguém vai responder.
        const string script = """
            $ErrorActionPreference = 'Stop'
            try {
              [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
              if (-not (Get-PackageProvider -Name NuGet -ErrorAction SilentlyContinue)) {
                Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -Scope AllUsers | Out-Null
              }
              Set-PSRepository -Name PSGallery -InstallationPolicy Trusted -ErrorAction SilentlyContinue
              Install-Module -Name DellBIOSProvider -Force -Scope AllUsers -AllowClobber -ErrorAction Stop
              [pscustomobject]@{ Erro = $null }
            } catch {
              [pscustomobject]@{ Erro = $_.Exception.Message }
            }
            """;

        try
        {
            var rows = await _ps.InvokeAsync(script, null, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            var erro = rows.FirstOrDefault()?.GetValueOrDefault("Erro") as string;
            if (!string.IsNullOrWhiteSpace(erro))
            {
                _logger.LogWarning("Instalação do {Modulo} falhou: {Erro}", NomeModulo, erro);
                return erro;
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Instalação do {Modulo} falhou", NomeModulo);
            return ex.Message;
        }
    }

    /// <summary>
    /// Lê Admin / System / HDD password pelo provider. Devolve null quando o
    /// módulo não está instalado ou a máquina não é Dell.
    /// </summary>
    public async Task<Resultado> LerAsync(CancellationToken ct)
    {
        // O provider expõe DellSmbios:\Security com as chaves Is*PasswordSet.
        // Cada uma pode não existir dependendo do modelo, então lê uma a uma e
        // trata ausência como "não reportado" em vez de derrubar a leitura toda.
        const string script = """
            $ErrorActionPreference = 'Stop'
            try {
              Import-Module DellBIOSProvider -ErrorAction Stop
              function Ler($nome) {
                try { (Get-Item -Path "DellSmbios:\Security\$nome" -ErrorAction Stop).CurrentValue }
                catch { $null }
              }
              [pscustomobject]@{
                Admin  = Ler 'IsAdminPasswordSet'
                System = Ler 'IsSystemPasswordSet'
                Hdd    = Ler 'IsHddPasswordSet'
                Erro   = $null
              }
            } catch {
              [pscustomobject]@{ Admin = $null; System = $null; Hdd = $null; Erro = $_.Exception.Message }
            }
            """;

        try
        {
            var rows = await _ps.InvokeAsync(script, null, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            var row = rows.FirstOrDefault();
            if (row is null) return new Resultado(null, "sem resposta do provider");

            var erro = row.GetValueOrDefault("Erro") as string;
            if (!string.IsNullOrWhiteSpace(erro)) return new Resultado(null, erro);

            var admin = ParaFlag(row.GetValueOrDefault("Admin"));
            var system = ParaFlag(row.GetValueOrDefault("System"));
            var hdd = ParaFlag(row.GetValueOrDefault("Hdd"));

            // nenhuma das três respondeu → o provider carregou mas não é Dell,
            // ou o modelo não expõe nada; não vale reportar como leitura boa
            if (admin == AvailabilityFlag.Indisponivel
                && system == AvailabilityFlag.Indisponivel
                && hdd == AvailabilityFlag.Indisponivel)
            {
                return new Resultado(null, "o provider não reportou nenhuma senha");
            }

            return new Resultado(
                new BiosSecurity(admin, system, hdd, "DellBIOSProvider"),
                null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Leitura via {Modulo} falhou", NomeModulo);
            return new Resultado(null, ex.Message);
        }
    }

    /// <summary>
    /// O provider devolve "True"/"False" (string) ou bool, conforme a versão.
    /// Valor ausente vira Indisponível, não "sem senha" — a diferença entre
    /// "não tem senha" e "não consegui ler" é justamente o que importa aqui.
    /// </summary>
    private static AvailabilityFlag ParaFlag(object? valor)
    {
        switch (valor)
        {
            case null:
                return AvailabilityFlag.Indisponivel;
            case bool b:
                return b ? AvailabilityFlag.Habilitado : AvailabilityFlag.Desabilitado;
            case string s when bool.TryParse(s.Trim(), out var parsed):
                return parsed ? AvailabilityFlag.Habilitado : AvailabilityFlag.Desabilitado;
            case string s when s.Trim() is "1" or "0":
                return s.Trim() == "1" ? AvailabilityFlag.Habilitado : AvailabilityFlag.Desabilitado;
            default:
                return AvailabilityFlag.Indisponivel;
        }
    }
}
