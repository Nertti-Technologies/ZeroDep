using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ZeroDep.Ocr;
using ZeroDep.Ocr.Paddle;

namespace ZeroDep.Ocr.Paddle.Tests;

/// <summary>
/// Worker-process handling of the PaddleOCR adapter, exercised with stand-in bridge scripts run by
/// <c>/bin/sh</c> in place of Python: request ids keep results in step, a hung worker is killed after
/// the time limit, <see cref="PaddleOcrEngine.Dispose"/> stops a worker that a request is blocked on,
/// and a worker that fails to start is not left running.
/// </summary>
public sealed class PaddleWorkerTests : IDisposable
{
    private const string Shell = "/bin/sh";

    // Extracts the "id" and "path" members from the adapter's compact JSON request line.
    private const string ReadRequest =
        "  id=$(printf '%s\\n' \"$line\" | sed -n 's/.*\"id\":\"\\([^\"]*\\)\".*/\\1/p')\n" +
        "  path=$(printf '%s\\n' \"$line\" | sed -n 's/.*\"path\":\"\\([^\"]*\\)\".*/\\1/p')\n";

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    private static readonly DecodedImage Image = new DecodedImage
    {
        Width = 2,
        Height = 2,
        Format = PixelFormat.Gray8,
        Pixels = new byte[4],
    };

    private readonly string _work = Directory.CreateTempSubdirectory("zerodep-paddle-test-").FullName;

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
    public void StrayAndStaleLines_AreSkipped_ByRequestId()
    {
        string script = Script(
            "desync.sh",
            "echo 'a log line before the handshake'\n" +
            "echo READY\n" +
            "while IFS= read -r line; do\n" + ReadRequest +
            "  echo 'a log line from a native library'\n" +
            "  echo '{\"id\":\"stale\",\"words\":[{\"text\":\"wrong\",\"conf\":1}]}'\n" +
            "  echo \"{\\\"id\\\":\\\"$id\\\",\\\"words\\\":[{\\\"text\\\":\\\"right-$id\\\",\\\"conf\\\":0.9,\\\"box\\\":[1,2,3,4]}]}\"\n" +
            "done\n");
        using var engine = Engine(script, TimeSpan.FromSeconds(10));

        OcrResult first = engine.Recognize(Image, new OcrOptions());
        OcrResult second = engine.Recognize(Image, new OcrOptions());

        Assert.Equal("right-1", Assert.Single(first.Lines).Text);
        Assert.Equal("right-2", Assert.Single(second.Lines).Text);
        Assert.Equal(3, Assert.Single(second.Lines).Bounds.Width);
    }

    [UnixFact]
    public void HungRequest_KillsWorkerTreeAfterTimeout()
    {
        string pidFile = Path.Combine(_work, "sleep.pid");
        string script = Script(
            "hang.sh",
            "echo READY\n" +
            "while IFS= read -r line; do\n" +
            $"  sleep 600 & echo $! > '{pidFile}'; wait\n" +
            "done\n");
        using var engine = Engine(script, TimeSpan.FromSeconds(1));

        var sw = Stopwatch.StartNew();
        Exception? ex = RunBounded(() => engine.Recognize(Image, new OcrOptions()));

        Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("did not answer", ex!.Message, StringComparison.Ordinal);
        Assert.True(sw.Elapsed < Deadline);
        AssertExited(int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [UnixFact]
    public void Dispose_StopsWorkerThatARequestIsBlockedOn()
    {
        string marker = Path.Combine(_work, "received");
        string script = Script(
            "block.sh",
            "echo READY\n" +
            "while IFS= read -r line; do\n" +
            $"  touch '{marker}'\n" +
            "  sleep 600\n" +
            "done\n");
        var engine = Engine(script, TimeSpan.FromSeconds(60));

        Task<OcrResult> run = Task.Run(() => engine.Recognize(Image, new OcrOptions()));
        var waited = Stopwatch.StartNew();
        while (!File.Exists(marker) && waited.Elapsed < Deadline)
        {
            Thread.Sleep(20);
        }

        Assert.True(File.Exists(marker), "The stand-in worker never received the request.");

        // Dispose must not wait for the request's lock: it used to deadlock here.
        Task dispose = Task.Run(engine.Dispose);
        Assert.True(dispose.Wait(Deadline), "Dispose did not return.");
        Assert.True(((IAsyncResult)run).AsyncWaitHandle.WaitOne(Deadline), "The blocked request did not end.");

        AggregateException ex = Assert.Throws<AggregateException>(() => run.Wait());
        Assert.IsType<ObjectDisposedException>(ex.InnerException);
    }

    [UnixFact]
    public void WorkerThatExitsDuringInit_IsReportedAndCleanedUp()
    {
        string script = Script("initfail.sh", "echo 'model load failed' >&2\nexit 1\n");
        using var engine = Engine(script, TimeSpan.FromSeconds(10));

        Exception? ex = RunBounded(() => engine.Recognize(Image, new OcrOptions()));

        Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("failed to initialize", ex!.Message, StringComparison.Ordinal);
    }

    [UnixFact]
    public void WorkerThatNeverBecomesReady_IsKilledAfterInitTimeout()
    {
        string pidFile = Path.Combine(_work, "init.pid");
        string script = Script("initdelay.sh", $"sleep 600 & echo $! > '{pidFile}'; wait\n");
        using var engine = new PaddleOcrEngine("en", Shell, script, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));

        Exception? ex = RunBounded(() => engine.Recognize(Image, new OcrOptions()));

        Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("did not become ready", ex!.Message, StringComparison.Ordinal);
        AssertExited(int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [UnixFact]
    public void ScanImage_IsOwnerOnly_AndRemovedAfterwards()
    {
        string modes = Path.Combine(_work, "modes.txt");
        string seen = Path.Combine(_work, "seen.txt");
        string script = Script(
            "modes.sh",
            "echo READY\n" +
            "while IFS= read -r line; do\n" + ReadRequest +
            $"  ls -ld \"$path\" \"$(dirname \"$path\")\" > '{modes}'\n" +
            $"  echo \"$path\" > '{seen}'\n" +
            "  echo \"{\\\"id\\\":\\\"$id\\\",\\\"words\\\":[]}\"\n" +
            "done\n");
        using var engine = Engine(script, TimeSpan.FromSeconds(10));

        engine.Recognize(Image, new OcrOptions());

        string[] lines = File.ReadAllLines(modes);
        Assert.Equal(2, lines.Length);
        Assert.Contains(lines, l => l.StartsWith("-rw-------", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("drwx------", StringComparison.Ordinal));
        string imagePath = File.ReadAllText(seen).Trim();
        Assert.False(File.Exists(imagePath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(imagePath)));
    }

    // The shipped bridge script against a stand-in paddleocr module that writes to stdout both from
    // Python and at the file-descriptor level: none of it may reach the protocol channel.
    [UnixFact]
    public void BridgeScript_KeepsLibraryOutputOffTheProtocolChannel()
    {
        string? python = FindOnPath("python3");
        if (python is null)
        {
            return; // no interpreter on this machine; the shell-based tests above still cover the C# side
        }

        string stub = Directory.CreateDirectory(Path.Combine(_work, "stub")).FullName;
        File.WriteAllText(
            Path.Combine(stub, "paddleocr.py"),
            "import os\n" +
            "print('import-time print')\n" +
            "os.write(1, b'import-time native write\\n')\n" +
            "class PaddleOCR:\n" +
            "    def __init__(self, lang):\n" +
            "        print('init print')\n" +
            "        os.write(1, b'init native write\\n')\n" +
            "    def predict(self, path):\n" +
            "        print('predict print')\n" +
            "        os.write(1, b'predict native write\\n')\n" +
            "        return [{'rec_texts': ['hello'], 'rec_scores': [0.75], 'rec_polys': [[[0, 0], [10, 0], [10, 5], [0, 5]]]}]\n");
        string wrapper = Script("python.sh", $"PYTHONPATH='{stub}' PYTHONDONTWRITEBYTECODE=1 exec '{python}' \"$@\"\n");

        using var engine = new PaddleOcrEngine("en", wrapper, scriptPath: null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        OcrResult first = engine.Recognize(Image, new OcrOptions());
        OcrResult second = engine.Recognize(Image, new OcrOptions());

        OcrLine line = Assert.Single(first.Lines);
        Assert.Equal("hello", line.Text);
        Assert.Equal(0.75, line.Confidence, 3);
        Assert.Equal(10, line.Bounds.Width);
        Assert.Single(second.Lines);
    }

    private static PaddleOcrEngine Engine(string script, TimeSpan requestTimeout)
        => new PaddleOcrEngine("en", Shell, script, TimeSpan.FromSeconds(10), requestTimeout);

    private static Exception? RunBounded(Action body)
    {
        Task run = Task.Run(body);
        Assert.True(((IAsyncResult)run).AsyncWaitHandle.WaitOne(Deadline), "The call did not return.");
        return run.Exception?.InnerException;
    }

    private static void AssertExited(int pid)
    {
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromSeconds(5))
        {
            try
            {
                using Process p = Process.GetProcessById(pid);
                if (p.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return; // no such process
            }

            Thread.Sleep(50);
        }

        Assert.Fail($"Process {pid} from the worker's tree is still running.");
    }

    private static string? FindOnPath(string name)
        => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, name))
            .FirstOrDefault(File.Exists);

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
