using System;

namespace NotebookCheck.Bootstrap;

/// <summary>
/// Fornece o <see cref="AppConfig"/> em runtime a partir das constantes
/// embutidas em <see cref="AppDefaults"/>. O técnico não tem nada para
/// configurar — toda a integração com o backend vem fixada no executável.
/// </summary>
public sealed class ConfigBootstrap
{
    public string BaseDirectory { get; }
    public AppConfig Config { get; private set; } = new();

    public ConfigBootstrap(string? baseDirectory = null)
    {
        BaseDirectory = baseDirectory ?? AppContext.BaseDirectory;
    }

    /// <summary>
    /// Materializa o <see cref="AppConfig"/> a partir de <see cref="AppDefaults"/>.
    /// </summary>
    public AppConfig EnsureLoaded()
    {
        Config = new AppConfig
        {
            ApiBaseUrl = AppDefaults.ApiBaseUrl,
            ApiEndpoint = AppDefaults.ApiEndpoint,
            AuthToken = AppDefaults.AuthToken,
            Options = new AppConfigOptions
            {
                EnablePdf = AppDefaults.EnablePdf,
                EnablePhotoCapture = AppDefaults.EnablePhotoCapture,
                InternetTestUrl = AppDefaults.InternetTestUrl,
                MicrophoneRecordSeconds = AppDefaults.MicrophoneRecordSeconds,
            },
        };
        return Config;
    }

    /// <summary>
    /// Combina <see cref="AppConfig.ApiBaseUrl"/> + <see cref="AppConfig.ApiEndpoint"/>
    /// e valida com <see cref="Uri.TryCreate(string?, UriKind, out Uri?)"/>.
    /// </summary>
    public static bool TryBuildApiUri(AppConfig config, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(config.ApiBaseUrl)) return false;

        var combined = config.ApiBaseUrl.TrimEnd('/') + "/" + (config.ApiEndpoint ?? "").TrimStart('/');
        return Uri.TryCreate(combined, UriKind.Absolute, out uri);
    }
}
