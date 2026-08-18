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
///   "supabaseUrl": "https://179.198.111.82.sslip.io",
///   "supabaseAnonKey": "eyJhbGciOi...",
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
    /// Supabase usado para autenticar a conta de serviço.
    ///
    /// Desde 05/08/2026 é a stack self-hosted no VPS, e não mais o projeto
    /// gerenciado <c>xautxjscoppdncuszkcn.supabase.co</c>, que foi restringido
    /// por cota e responde <b>402</b>. O app pega o token AQUI antes de chamar
    /// qualquer <c>/api/integracao/...</c>, então enquanto isto apontou para o
    /// projeto morto o app morria no primeiro passo: desde a migração o ERP não
    /// recebeu nenhuma foto nem nenhum áudio, e o único sinal era o 402 no log.
    ///
    /// O <c>sslip.io</c> transforma o IP em nome porque não se emite certificado
    /// HTTPS para IP puro. Quando o domínio próprio for comprado, trocar aqui.
    /// </summary>
    public const string SupabaseUrl = "https://179.198.111.82.sslip.io";

    /// <summary>Chave pública (publishable/anon) do Supabase — vai no header <c>apikey</c>.</summary>
    public const string SupabaseAnonKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJyb2xlIjoiYW5vbiIsImlzcyI6InN1cGFiYXNlIiwiaWF0IjoxNzg1OTU1OTY0LCJleHAiOjIxMDEzMTU5NjR9.7tIxuHZA9qxNSC9rsra3xUnKiRI9_pWiSpxgNF2l9WU";

    /// <summary>E-mail da conta de serviço (papel <c>integracao_recebimento</c>).</summary>
    public const string ServiceEmail = "integracao-recimento@notelet.com.br";

    /// <summary>Senha da conta de serviço.</summary>
    public const string ServicePassword = "integracaouwer2134";
}
