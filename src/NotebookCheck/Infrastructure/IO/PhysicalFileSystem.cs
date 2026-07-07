using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NotebookCheck.Infrastructure.Abstractions;

namespace NotebookCheck.Infrastructure.IO;

/// <summary>
/// Implementação real de <see cref="IFileSystem"/> sobre <see cref="System.IO.File"/>
/// e <see cref="System.IO.Directory"/>.
/// </summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public IEnumerable<string> EnumerateFiles(string path, string searchPattern)
    {
        if (!Directory.Exists(path))
        {
            return System.Linq.Enumerable.Empty<string>();
        }
        return Directory.EnumerateFiles(path, searchPattern, SearchOption.TopDirectoryOnly);
    }

    public Task<string> ReadAllTextAsync(string path, Encoding? encoding, CancellationToken ct)
    {
        return File.ReadAllTextAsync(path, encoding ?? Encoding.UTF8, ct);
    }

    public Task WriteAllTextAsync(string path, string contents, Encoding? encoding, CancellationToken ct)
    {
        return File.WriteAllTextAsync(path, contents, encoding ?? Encoding.UTF8, ct);
    }

    public void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
    {
        File.Move(sourcePath, destinationPath, overwrite);
    }

    public long? GetFileSize(string path)
    {
        if (!File.Exists(path)) return null;
        return new FileInfo(path).Length;
    }

    public DateTime GetCreationTimeUtc(string path) => File.GetCreationTimeUtc(path);
}
