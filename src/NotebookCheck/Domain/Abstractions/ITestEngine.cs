using NotebookCheck.Domain.Models;

namespace NotebookCheck.Domain.Abstractions;

/// <summary>
/// Fachada do Motor_de_Testes que executa cada teste automático individualmente.
/// </summary>
/// <remarks>
/// Cada método retorna um <see cref="TestResult"/> e nunca propaga exceções para
/// o orquestrador — internamente envolto em <c>try/catch</c> que devolve
/// <c>Status = AutoStatus.Falha</c> com a causa em <see cref="TestResult.Details"/>
/// (Requirement 26).
/// </remarks>
public interface ITestEngine
{
    /// <summary>Executa o teste de RAM consumindo <paramref name="m"/>.RamGb.</summary>
    Task<TestResult> RunRamAsync(MachineInfo m, CancellationToken ct);

    /// <summary>
    /// Executa o teste consolidado de armazenamento sobre os discos coletados.
    /// </summary>
    Task<TestResult> RunStorageAsync(IReadOnlyList<StorageInfo> s, CancellationToken ct);

    /// <summary>
    /// Executa um teste detalhado de saúde/vida útil dos discos via WMI
    /// (MSStorageDriver_FailurePredictStatus + MSFT_PhysicalDisk).
    /// Reporta status SMART, percentual de vida útil restante (quando reportado
    /// pelo SSD via <c>HealthStatus</c>/<c>Wear</c>) e atributos de aviso.
    /// </summary>
    Task<TestResult> RunStorageHealthAsync(IReadOnlyList<StorageInfo> s, CancellationToken ct);

    /// <summary>
    /// Executa o teste de bateria; <paramref name="b"/> nulo retorna
    /// <c>Status = AutoStatus.NaoAplicavel</c>.
    /// </summary>
    Task<TestResult> RunBatteryAsync(BatteryInfo? b, CancellationToken ct);

    /// <summary>Executa o teste do carregador AC via <c>GetSystemPowerStatus</c>.</summary>
    Task<TestResult> RunChargerAsync(CancellationToken ct);

    /// <summary>Executa o teste de saída HDMI via <c>EnumDisplayMonitors</c>.</summary>
    Task<TestResult> RunHdmiAsync(CancellationToken ct);

    /// <summary>Executa o teste do adaptador Wi-Fi e enumeração de redes visíveis.</summary>
    Task<TestResult> RunWifiAsync(CancellationToken ct);

    /// <summary>Executa o teste do adaptador Bluetooth.</summary>
    Task<TestResult> RunBluetoothAsync(CancellationToken ct);

    /// <summary>Executa um probe HTTP de internet contra <paramref name="probeUrl"/>.</summary>
    Task<TestResult> RunInternetAsync(string probeUrl, CancellationToken ct);

    /// <summary>Executa o teste de áudio reproduzindo o tom embutido.</summary>
    Task<TestResult> RunAudioAsync(CancellationToken ct);

    /// <summary>
    /// Executa o teste estéreo reproduzindo um tom distinto no canal esquerdo
    /// e depois no direito, permitindo que o técnico confirme separadamente
    /// que ambas as caixas/lados respondem.
    /// </summary>
    Task<TestResult> RunStereoAsync(CancellationToken ct);

    /// <summary>
    /// Detecta câmeras disponíveis no sistema (via WMI).
    /// </summary>
    Task<IReadOnlyList<string>> DetectCamerasAsync(CancellationToken ct);

    /// <summary>
    /// Registra o resultado do teste de câmera fornecido pelo técnico após a
    /// inspeção visual. <paramref name="ok"/> = câmera funciona,
    /// <paramref name="ok"/> = false = não funciona.
    /// </summary>
    TestResult BuildWebcamResult(bool ok, string? note);

    /// <summary>
    /// Registra o resultado do teste de pixels mortos com base em quantas das
    /// telas de cor o técnico visualizou (até <c>totalScreens</c>) e se ele
    /// reportou pixels defeituosos.
    /// </summary>
    TestResult BuildPixelTestResult(int viewedScreens, int totalScreens, bool foundDefects, string? note);

    /// <summary>
    /// Executa o teste de microfone gravando por <paramref name="seconds"/>
    /// segundos (entre 3 e 10) e reproduzindo a gravação.
    /// </summary>
    Task<TestResult> RunMicrophoneAsync(int seconds, CancellationToken ct);

    /// <summary>
    /// Portas USB: conta os dispositivos conectados agora e as controladoras
    /// (classificadas por versão 2.0/3.x/USB4).
    /// </summary>
    Task<TestResult> RunUsbPortsAsync(CancellationToken ct);

    /// <summary>
    /// Saídas de vídeo em uso (desktop): lista cada monitor ligado com o
    /// conector (HDMI/DP/DVI/VGA) e a resolução. OK com pelo menos um.
    /// </summary>
    Task<TestResult> RunVideoOutputsAsync(CancellationToken ct);

    /// <summary>Detecta a taxa de atualização (Hz) da(s) tela(s).</summary>
    Task<TestResult> RunRefreshRateAsync(CancellationToken ct);

    /// <summary>Detecta leitor de digital e câmera IR (Windows Hello).</summary>
    Task<TestResult> RunBiometricsAsync(CancellationToken ct);

    /// <summary>Detecta leitor de cartão SD/MMC.</summary>
    Task<TestResult> RunCardReaderAsync(CancellationToken ct);
}
