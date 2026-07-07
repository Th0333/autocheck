using System;
using System.IO;

namespace NotebookCheck.Bootstrap;

/// <summary>
/// Resolve diretórios de gravação tolerantes a pendrive. O app roda portátil
/// (quase sempre de um pendrive), então a pasta do .exe pode sumir, trocar de
/// letra ou estar somente-leitura no meio da sessão. Estes helpers tentam a
/// pasta preferida e caem para uma pasta garantida no perfil do usuário
/// (<c>%LOCALAPPDATA%\Notelet</c>), evitando perder relatórios.
/// </summary>
public static class AppPaths
{
    /// <summary>Pasta do executável (preferida — acompanha o app portátil).</summary>
    public static string ExeDir => AppContext.BaseDirectory;

    /// <summary>Pasta garantida de fallback no perfil do usuário.</summary>
    public static string FallbackDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Notelet");

    /// <summary>True quando o fallback foi necessário (pendrive indisponível na resolução).</summary>
    public static bool UsingFallback { get; private set; }

    /// <summary>
    /// Devolve um diretório gravável: o preferido, se der para escrever nele;
    /// senão o fallback. Garante que o diretório escolhido exista.
    /// </summary>
    public static string ResolveWritable(string preferred)
    {
        if (IsWritable(preferred)) return preferred;
        UsingFallback = true;
        Directory.CreateDirectory(FallbackDir);
        return FallbackDir;
    }

    /// <summary>Testa escrita REAL criando e apagando um arquivo de prova.</summary>
    public static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".w_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
