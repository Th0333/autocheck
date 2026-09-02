namespace NotebookCheck.Bootstrap;

/// <summary>
/// ============================================================
///  CONFIGURAÃ‡ÃƒO DE BACKEND DA APLICAÃ‡ÃƒO â€” EDITE AQUI
/// ============================================================
/// Estes valores ficam embutidos no <c>NotebookCheck.exe</c> em tempo de
/// compilaÃ§Ã£o. Para mudar a URL do painel ou o token de autenticaÃ§Ã£o:
///
///   1. Edite as constantes abaixo.
///   2. Rode <c>pwsh scripts/publish.ps1</c> para gerar um novo .exe.
///   3. Distribua o .exe atualizado para os tÃ©cnicos.
///
/// O tÃ©cnico nÃ£o vÃª nem precisa preencher nada.
/// </summary>
internal static class AppDefaults
{
    /// <summary>URL base do painel hospedado na Vercel.</summary>
    public const string ApiBaseUrl = "https://notebook-gamma-seven.vercel.app";

    /// <summary>
    /// VersÃ£o atual do app. Atualizada automaticamente pelo publish.ps1.
    /// O auto-updater compara esta versÃ£o com a do version.json no site.
    /// </summary>
    public const string CurrentVersion = "1.9.1";

    /// <summary>
    /// URL do manifesto de versÃ£o (JSON) hospedado no site. ContÃ©m a versÃ£o
    /// mais nova, a URL do .exe no GitHub Release, o SHA256 e o changelog.
    /// Fonte primÃ¡ria: API que lÃª do MongoDB (sem precisar de redeploy).
    /// </summary>
    public const string VersionApiUrl = "https://notebook-gamma-seven.vercel.app/api/version";

    /// <summary>
    /// Fallback estÃ¡tico caso a API/banco esteja indisponÃ­vel. Arquivo servido
    /// pela CDN da Vercel (public/version.json).
    /// </summary>
    public const string VersionManifestUrl = "https://notebook-gamma-seven.vercel.app/version.json";

    /// <summary>Endpoint REST que recebe os relatÃ³rios.</summary>
    public const string ApiEndpoint = "/api/reports";

    /// <summary>
    /// Token compartilhado com o painel. Tem que ser idÃªntico ao
    /// <c>INGEST_TOKEN</c> configurado nas environment variables da Vercel.
    /// </summary>
    public const string AuthToken = "segredaotop";

    // -------------------- opÃ§Ãµes -------------------------------------------

    /// <summary>Habilita gravaÃ§Ã£o de PDF do relatÃ³rio (futuro).</summary>
    public const bool EnablePdf = false;

    /// <summary>Habilita captura de fotos pela webcam (futuro).</summary>
    public const bool EnablePhotoCapture = true;

    /// <summary>URL usada como probe HTTP para o teste de internet.</summary>
    public const string InternetTestUrl = "https://www.gstatic.com/generate_204";

    /// <summary>DuraÃ§Ã£o padrÃ£o da gravaÃ§Ã£o do microfone, em segundos (3..10).</summary>
    public const int MicrophoneRecordSeconds = 5;
}
