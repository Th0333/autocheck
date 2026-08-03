using Microsoft.Extensions.Logging.Abstractions;
using NotebookCheck.Domain.Abstractions;
using NotebookCheck.Infrastructure.Persistence;

namespace NotebookCheck.Tests.Infrastructure.Persistence;

/// <summary>
/// Fila das gravações de microfone pendentes de envio ao ERP.
///
/// O que realmente importa aqui é o carimbo: cada item precisa devolver o
/// <c>test_id</c>/NTB da máquina de onde saiu, senão a fila entregaria o áudio
/// no check errado.
/// </summary>
public class FileAudioQueueTests : IDisposable
{
    private readonly string _dir;
    private readonly FileAudioQueue _queue;

    public FileAudioQueueTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nbc-audioq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _queue = new FileAudioQueue(NullLogger<FileAudioQueue>.Instance, _dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
        GC.SuppressFinalize(this);
    }

    private static PendingAudio Item(
        string? testId = null, string? ntb = null, DateTime? at = null, int attempts = 0) =>
        new(
            Id: Guid.NewGuid().ToString("N"),
            TestId: testId ?? Guid.NewGuid().ToString(),
            Ntb: ntb,
            Serial: "SER123",
            Mime: "audio/mp4",
            Extension: "m4a",
            DuracaoSeg: 12.5,
            EnqueuedAt: at ?? DateTime.UtcNow,
            Attempts: attempts);

    [Fact]
    public async Task Fila_nasce_vazia()
    {
        _queue.Count.Should().Be(0);
        (await _queue.ListAsync(default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Item_guardado_volta_com_o_carimbo_da_maquina()
    {
        var item = Item(testId: "11111111-1111-1111-1111-111111111111", ntb: "NTB-042");
        await _queue.EnqueueAsync(item, new byte[] { 1, 2, 3 }, default);

        var lidos = await _queue.ListAsync(default);

        lidos.Should().ContainSingle();
        lidos[0].TestId.Should().Be("11111111-1111-1111-1111-111111111111");
        lidos[0].Ntb.Should().Be("NTB-042");
        lidos[0].Serial.Should().Be("SER123");
        lidos[0].DuracaoSeg.Should().Be(12.5);
        lidos[0].Extension.Should().Be("m4a");
    }

    [Fact]
    public async Task Bytes_voltam_intactos()
    {
        var item = Item();
        var bytes = new byte[] { 9, 8, 7, 6, 5 };
        await _queue.EnqueueAsync(item, bytes, default);

        (await _queue.ReadBytesAsync(item.Id, default)).Should().Equal(bytes);
    }

    [Fact]
    public async Task Duas_gravacoes_da_mesma_maquina_convivem()
    {
        // A fila do relatório é uma por test_id; a de áudio não pode ser — o
        // técnico pode gravar, achar ruim, regravar e enviar as duas.
        const string mesmoTeste = "22222222-2222-2222-2222-222222222222";
        await _queue.EnqueueAsync(Item(testId: mesmoTeste), new byte[] { 1 }, default);
        await _queue.EnqueueAsync(Item(testId: mesmoTeste), new byte[] { 2 }, default);

        var lidos = await _queue.ListAsync(default);

        lidos.Should().HaveCount(2);
        lidos.Should().OnlyContain(i => i.TestId == mesmoTeste);
    }

    [Fact]
    public async Task Ordem_e_fifo_pela_data_de_entrada()
    {
        var antigo = Item(ntb: "ANTIGO", at: DateTime.UtcNow.AddMinutes(-30));
        var novo = Item(ntb: "NOVO", at: DateTime.UtcNow);
        await _queue.EnqueueAsync(novo, new byte[] { 1 }, default);
        await _queue.EnqueueAsync(antigo, new byte[] { 2 }, default);

        var lidos = await _queue.ListAsync(default);

        lidos.Select(i => i.Ntb).Should().Equal("ANTIGO", "NOVO");
    }

    [Fact]
    public async Task Remover_apaga_o_json_e_o_binario()
    {
        var item = Item();
        await _queue.EnqueueAsync(item, new byte[] { 1 }, default);

        await _queue.RemoveAsync(item.Id, default);

        _queue.Count.Should().Be(0);
        (await _queue.ReadBytesAsync(item.Id, default)).Should().BeNull();
        Directory.GetFiles(Path.Combine(_dir, "audio-queue")).Should().BeEmpty();
    }

    [Fact]
    public async Task Tentativas_acumulam()
    {
        var item = Item();
        await _queue.EnqueueAsync(item, new byte[] { 1 }, default);

        await _queue.IncrementAttemptsAsync(item.Id, default);
        await _queue.IncrementAttemptsAsync(item.Id, default);

        (await _queue.ListAsync(default))[0].Attempts.Should().Be(2);
    }

    [Fact]
    public async Task Item_com_mais_de_sete_dias_e_descartado_na_varredura()
    {
        var velho = Item(at: DateTime.UtcNow.AddDays(-8));
        await _queue.EnqueueAsync(velho, new byte[] { 1 }, default);

        (await _queue.ListAsync(default)).Should().BeEmpty();
        _queue.Count.Should().Be(0);
    }

    [Fact]
    public async Task Item_sem_o_binario_do_lado_e_descartado()
    {
        var item = Item();
        await _queue.EnqueueAsync(item, new byte[] { 1 }, default);
        File.Delete(Path.Combine(_dir, "audio-queue", $"audio_{item.Id}.bin"));

        (await _queue.ListAsync(default)).Should().BeEmpty();
        _queue.Count.Should().Be(0);
    }

    [Fact]
    public async Task Json_corrompido_nao_derruba_a_leitura_dos_outros()
    {
        var bom = Item(ntb: "BOM");
        await _queue.EnqueueAsync(bom, new byte[] { 1 }, default);
        await File.WriteAllTextAsync(
            Path.Combine(_dir, "audio-queue", "audio_lixo.json"), "{ isso não é json");

        var lidos = await _queue.ListAsync(default);

        lidos.Should().ContainSingle().Which.Ntb.Should().Be("BOM");
    }
}
