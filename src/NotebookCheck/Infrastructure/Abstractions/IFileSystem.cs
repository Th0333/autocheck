using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NotebookCheck.Infrastructure.Abstractions;

/// <summary>
/// Wrapper mínimo sobre <c>System.IO.File</c> e <c>System.IO.Directory</c> para
/// permitir testes do <c>JsonReportRepository</c> e da <c>FileOfflineQueue</c>
/// sem tocar no disco real. Mantém apenas as operações usadas pela aplicação,
/// preservando a semântica de exceções (<see cref="UnauthorizedAccessException"/>,
/// <see cref="IOException"/>, <see cref="DriveNotFoundException"/>) referenciadas
/// nos Requirements 1.10 e 1.11.
/// </summary>
public interface IFileSystem
{
    /// <summary>Indica se o arquivo informado existe.</summary>
    bool FileExists(string path);

    /// <summary>Indica se o diretório informado existe.</summary>
    bool DirectoryExists(string path);

    /// <summary>
    /// Cria o diretório (e quaisquer intermediários necessários). Não lança
    /// se já existir.
    /// </summary>
    void CreateDirectory(string path);

    /// <summary>
    /// Enumera arquivos do diretório que correspondem ao
    /// <paramref name="searchPattern"/> (padrão glob aceito por
    /// <c>Directory.EnumerateFiles</c>). Retorna apenas arquivos diretos.
    /// </summary>
    IEnumerable<string> EnumerateFiles(string path, string searchPattern);

    /// <summary>Lê o conteúdo do arquivo como string usando UTF-8.</summary>
    Task<string> ReadAllTextAsync(string path, Encoding? encoding, CancellationToken ct);

    /// <summary>Grava texto substituindo o conteúdo existente.</summary>
    Task WriteAllTextAsync(string path, string contents, Encoding? encoding, CancellationToken ct);

    /// <summary>Remove um arquivo. Não lança se não existir.</summary>
    void DeleteFile(string path);

    /// <summary>
    /// Move um arquivo. <paramref name="overwrite"/> define se um destino
    /// existente deve ser substituído.
    /// </summary>
    void MoveFile(string sourcePath, string destinationPath, bool overwrite);

    /// <summary>Retorna o tamanho do arquivo em bytes ou <c>null</c> se inexistente.</summary>
    long? GetFileSize(string path);

    /// <summary>Retorna a data de criação do arquivo, em UTC.</summary>
    DateTime GetCreationTimeUtc(string path);
}
