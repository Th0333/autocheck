namespace NotebookCheck.Bootstrap;

/// <summary>
/// ============================================================
///  CONFIGURAÇÃO DA INTEGRAÇÃO COM O ERP DE ESTOQUE — EDITE AQUI
/// ============================================================
/// Usada pelo modo "Cadastro no estoque". Estes valores ficam embutidos no
/// executável, mas podem ser sobrescritos em runtime por um arquivo
/// <c>erp-config.json</c> colocado no diretório gravável do app (útil para
/// trocar a senha da conta de serviço sem republicar). Formato do JSON:
/// <code>
/// {
///   "baseUrl": "https://estoque-erp-web.vercel.app",
///   "supabaseUrl": "https://xxxx.supabase.co",
///   "supabaseAnonKey": "sb_publishable_...",
///   "email": "integracao@exemplo.com",
///   "password": "..."
/// }
/// </code>
/// </summary>
internal static class ErpDefaults
{
    /// <summary>Base do ERP que expõe <c>/api/integracao/...</c>.</summary>
    public const string BaseUrl = "https://estoque-erp-web.vercel.app";

    /// <summary>
    /// Supabase usado para autenticar a conta de serviço. Desde 05/08/2026 é a
    /// stack self-hosted no VPS da Notelet — o projeto gerenciado
    /// (<c>xautxjscoppdncuszkcn.supabase.co</c>) foi restrito por cota e
    /// respondia HTTP 402 no login, que o app mostrava como "Falha ao autenticar
    /// no ERP". O <c>sslip.io</c> é DNS curinga sobre o IP; quando houver domínio
    /// próprio, troque aqui (ou por <c>erp-config.json</c>, sem republicar).
    /// </summary>
    public const string SupabaseUrl = "https://179.198.111.82.sslip.io";

    /// <summary>Chave pública (anon) do Supabase self-hosted — vai no header <c>apikey</c>.</summary>
    public const string SupabaseAnonKey =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJyb2xlIjoiYW5vbiIsImlzcyI6InN1cGFiYXNlIiwiaWF0IjoxNzg1OTU1OTY0LCJleHAiOjIxMDEzMTU5NjR9.7tIxuHZA9qxNSC9rsra3xUnKiRI9_pWiSpxgNF2l9WU";

    /// <summary>E-mail da conta de serviço (papel <c>integracao_recebimento</c>).</summary>
    public const string ServiceEmail = "integracao-recimento@notelet.com.br";

    /// <summary>Senha da conta de serviço.</summary>
    public const string ServicePassword = "integracaouwer2134";
}
