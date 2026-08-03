using System;
using System.IO;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace NotebookCheck.Infrastructure.Audio;

/// <summary>
/// Encolhe a gravação do microfone antes de subir para o ERP.
///
/// Motivo: o storage do Supabase estourou, e WAV cru é caro — 30 s a 16 kHz dão
/// ~940 KB. Em AAC a mesma gravação fica em ~120 KB (~8× menos).
///
/// O codificador AAC vem no próprio Windows (Media Foundation), então isto não
/// adiciona dependência nenhuma. Como codec de sistema pode faltar ou recusar,
/// <b>toda falha cai no WAV original</b>: no pior caso o arquivo sobe maior, mas
/// sobe. É por isso que esta classe é isolada — trocar por MP3 (NAudio.Lame)
/// mexeria só aqui.
/// </summary>
public static class AudioCompressor
{
    /// <summary>Bitrate alvo do AAC. A Media Foundation escolhe o mais próximo que suporta.</summary>
    private const int TargetBitrate = 24_000;

    /// <summary>
    /// O codificador AAC do Windows só aceita entrada em 44,1/48 kHz, então o
    /// WAV de 16 kHz é reamostrado antes de entrar nele.
    /// </summary>
    private static readonly WaveFormat EncoderInput = new(44100, 16, 1);

    /// <summary>Formato pronto para envio, já com o MIME e a extensão certos.</summary>
    public readonly record struct Result(byte[] Bytes, string Mime, string Extension)
    {
        /// <summary>True quando a compressão funcionou (não é o WAV de reserva).</summary>
        public bool Compressed => Extension != "wav";
    }

    /// <summary>
    /// Converte o WAV para AAC/.m4a. Devolve o WAV intacto se a conversão falhar
    /// ou não valer a pena (saída vazia ou maior que a entrada).
    /// </summary>
    public static Result ForUpload(byte[] wav)
    {
        var fallback = new Result(wav, "audio/wav", "wav");
        if (wav is not { Length: > 44 }) return fallback;

        var tmp = Path.Combine(Path.GetTempPath(), $"nbc-mic-{Guid.NewGuid():N}.m4a");
        try
        {
            MediaFoundationApi.Startup();

            using (var src = new MemoryStream(wav, writable: false))
            using (var reader = new WaveFileReader(src))
            using (var resampler = new MediaFoundationResampler(reader, EncoderInput) { ResamplerQuality = 60 })
            {
                MediaFoundationEncoder.EncodeToAac(resampler, tmp, TargetBitrate);
            }

            var bytes = File.ReadAllBytes(tmp);
            return bytes.Length > 0 && bytes.Length < wav.Length
                ? new Result(bytes, "audio/mp4", "m4a")
                : fallback;
        }
        catch (Exception)
        {
            // Codec ausente, resampler recusando, disco cheio: manda o WAV.
            return fallback;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
        }
    }
}
