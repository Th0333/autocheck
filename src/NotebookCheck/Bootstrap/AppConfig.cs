using System.Text.Json.Serialization;

namespace NotebookCheck.Bootstrap;

/// <summary>
/// Modelo tipado lido a partir de <c>config.json</c> em
/// <see cref="System.AppContext.BaseDirectory"/>.
/// </summary>
public sealed class AppConfig
{
    [JsonPropertyName("api_base_url")]
    public string ApiBaseUrl { get; set; } = "";

    [JsonPropertyName("api_endpoint")]
    public string ApiEndpoint { get; set; } = "";

    [JsonPropertyName("auth_token")]
    public string AuthToken { get; set; } = "";

    [JsonPropertyName("options")]
    public AppConfigOptions Options { get; set; } = new();
}

public sealed class AppConfigOptions
{
    [JsonPropertyName("enable_pdf")]
    public bool EnablePdf { get; set; }

    [JsonPropertyName("enable_photo_capture")]
    public bool EnablePhotoCapture { get; set; } = true;

    [JsonPropertyName("internet_test_url")]
    public string InternetTestUrl { get; set; } = "https://www.gstatic.com/generate_204";

    [JsonPropertyName("microphone_record_seconds")]
    public int MicrophoneRecordSeconds { get; set; } = 5;
}
