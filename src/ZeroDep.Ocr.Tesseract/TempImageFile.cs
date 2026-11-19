using System;
using System.IO;

namespace ZeroDep.Ocr.Tesseract;

/// <summary>
/// A scan image written to a fresh private directory for the OCR subprocess to read. On Unix the
/// directory is created owner-only (0700) and the file owner-only (0600), so other local users cannot
/// read a scanned page. <see cref="Dispose"/> removes both, best effort.
/// </summary>
internal sealed class TempImageFile : IDisposable
{
    private readonly string _directory;

    private TempImageFile(string directory, string path)
    {
        _directory = directory;
        Path = path;
    }

    /// <summary>The full path of the image file.</summary>
    public string Path { get; }

    /// <summary>Writes <paramref name="content"/> to a new private temporary file.</summary>
    public static TempImageFile Create(byte[] content)
    {
        // CreateTempSubdirectory uses mkdtemp on Unix, which creates the directory with mode 0700.
        string directory = Directory.CreateTempSubdirectory("zerodep-ocr-").FullName;
        string path = System.IO.Path.Combine(directory, "page.bmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(path, options))
            {
                stream.Write(content, 0, content.Length);
            }

            return new TempImageFile(directory, path);
        }
        catch
        {
            TryDelete(directory);
            throw;
        }
    }

    /// <summary>Deletes the file and its directory; a failure to delete never fails recognition.</summary>
    public void Dispose() => TryDelete(_directory);

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a leftover temp file must not fail recognition.
        }
        catch (UnauthorizedAccessException)
        {
            // As above: for example, the file is still held open by a process that is being killed.
        }
    }
}
