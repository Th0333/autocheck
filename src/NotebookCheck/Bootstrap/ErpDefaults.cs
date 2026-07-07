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

    /// <summary>Projeto Supabase usado para autenticar a conta de serviço.</summary>
    public const string SupabaseUrl = "https://xautxjscoppdncuszkcn.supabase.co";

    /// <summary>Chave pública (publishable/anon) do Supabase — vai no header <c>apikey</c>.</summary>
    public const string SupabaseAnonKey = "sb_publishable_Ksu23waITFnKYGGD0_H5RA_PGvq-Z1V";

    /// <summary>E-mail da conta de serviço (papel <c>integracao_recebimento</c>).</summary>
    public const string ServiceEmail = "integracao-recimento@notelet.com.br";

    /// <summary>Senha da conta de serviço.</summary>
    public const string ServicePassword = "integracaouwer2134";
}
