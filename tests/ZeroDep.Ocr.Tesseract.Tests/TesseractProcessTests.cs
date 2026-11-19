using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using ZeroDep.Ocr;
using ZeroDep.Ocr.Tesseract;

namespace ZeroDep.Ocr.Tesseract.Tests;

/// <summary>
/// Subprocess handling of the CLI adapter, exercised with stand-in shell scripts in place of the real
/// program: a run that hangs is killed after the time limit, the scan image is private while the
/// process runs, and it is removed afterwards.
/// </summary>
public sealed class TesseractProcessTests : IDisposable
{
    private static readonly DecodedImage Image = new DecodedImage
    {
        Width = 2,
        Height = 2,
        Format = PixelFormat.Gray8,
        Pixels = new byte[4],
    };

    private readonly string _work = Directory.CreateTempSubdirectory("zerodep-tess-test-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [UnixFact]
    public void HangingProcess_IsKilledAfterTimeout_AndImageRemoved()
    {
        string seen = Path.Combine(_work, "seen.txt");
        string exe = Script("hang.sh", $"echo \"$1\" > '{seen}'\nsleep 600\n");
        var engine = new TesseractOcrEngine(null, "eng", exe, TimeSpan.FromSeconds(1));

        var sw = Stopwatch.StartNew();
        Task<OcrResult> run = Task.Run(() => engine.Recognize(Image, new OcrOptions()));
        Assert.True(((IAsyncResult)run).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(10)), "Recognize did not return after the time limit.");

        AggregateException ex = Assert.Throws<AggregateException>(() => run.Wait());
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("did not finish", ex.InnerException!.Message, StringComparison.Ordinal);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));

        string imagePath = File.ReadAllText(seen).Trim();
        Assert.False(File.Exists(imagePath), "The temporary scan image was left behind.");
        Assert.False(Directory.Exists(Path.GetDirectoryName(imagePath)), "The temporary directory was left behind.");
    }

    [UnixFact]
    public void ScanImage_IsOwnerOnly_WhileProcessRuns()
    {
        string modes = Path.Combine(_work, "modes.txt");
        string exe = Script("modes.sh", $"ls -ld \"$1\" \"$(dirname \"$1\")\" > '{modes}'\n");
        var engine = new TesseractOcrEngine(null, "eng", exe, TimeSpan.FromSeconds(10));

        engine.Recognize(Image, new OcrOptions());

        string[] lines = File.ReadAllLines(modes);
        Assert.Equal(2, lines.Length);
        Assert.Contains(lines, l => l.StartsWith("-rw-------", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("drwx------", StringComparison.Ordinal));
    }

    [UnixFact]
    public void CompletedProcess_OutputIsParsed()
    {
        string exe = Script(
            "ok.sh",
            "printf 'level\\tpage_num\\tblock_num\\tpar_num\\tline_num\\tword_num\\tleft\\ttop\\twidth\\theight\\tconf\\ttext\\n'\n" +
            "printf '4\\t1\\t1\\t1\\t1\\t0\\t1\\t2\\t30\\t10\\t-1\\t\\n'\n" +
            "printf '5\\t1\\t1\\t1\\t1\\t1\\t1\\t2\\t30\\t10\\t95\\thello\\n'\n");
        var engine = new TesseractOcrEngine(null, "eng", exe, TimeSpan.FromSeconds(10));

        OcrResult result = engine.Recognize(Image, new OcrOptions());

        OcrLine line = Assert.Single(result.Lines);
        Assert.Equal("hello", line.Text);
        Assert.Equal(0.95, line.Confidence, 3);
    }

    [UnixFact]
    public void FailingProcess_IsReportedAsOcrFailure()
    {
        string exe = Script("fail.sh", "echo broken >&2\nexit 3\n");
        var engine = new TesseractOcrEngine(null, "eng", exe, TimeSpan.FromSeconds(10));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => engine.Recognize(Image, new OcrOptions()));
        Assert.Contains("code 3", ex.Message, StringComparison.Ordinal);
    }

    private string Script(string name, string body)
    {
        string path = Path.Combine(_work, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}
