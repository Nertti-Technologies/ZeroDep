using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ZeroDep.Abstractions;

namespace ZeroDep.Ocr.Paddle;

/// <summary>
/// An <see cref="IOcrEngine"/> that recovers text from images by driving the PaddleOCR Python package
/// through a bundled bridge script (<c>zerodep_paddle_ocr.py</c>) as a subprocess, parsing its JSON
/// output for per-line text, confidence, and bounding boxes. PaddleOCR is notably stronger than
/// Tesseract on dense and CJK scripts.
/// </summary>
/// <remarks>
/// <para>
/// This adapter carries <b>no native or managed package dependency</b>: it shells out to a Python
/// interpreter that has the <c>paddleocr</c> package installed (<c>pip install paddleocr</c>). That
/// keeps platform support equal to PaddleOCR's own — including Apple-silicon macOS — and avoids binding
/// native PaddleInference in-process.
/// </para>
/// <para>
/// The first <see cref="Recognize"/> call starts one persistent worker process that loads the models
/// once; later calls reuse it, one request at a time, so instances are safe to use concurrently.
/// <see cref="Dispose"/> stops the worker, killing it if it does not exit promptly, even while a request
/// is in progress.
/// </para>
/// <para>
/// Each request carries an id that the worker echoes, so a stray output line cannot be mistaken for a
/// result. Starting the worker and each request are bounded by internal time limits; on expiry the
/// worker's process tree is killed and the call fails with <see cref="InvalidOperationException"/>. The
/// scan image is passed through a temporary file that only the current user can read, and the file is
/// deleted afterwards.
/// </para>
/// </remarks>
public sealed class PaddleOcrEngine : IOcrEngine, IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _python;
    private readonly string _script;
    private readonly string _language;
    private readonly TimeSpan _initTimeout;
    private readonly TimeSpan _requestTimeout;

    // Serializes requests to the single worker. Dispose never takes it, so it can stop a worker that a
    // request is blocked on.
    private readonly object _gate = new object();
    private readonly Queue<string> _stderrTail = new Queue<string>();
    private Process? _worker;
    private long _nextRequestId;
    private volatile bool _disposed;

    /// <summary>Creates an engine that drives PaddleOCR via the bundled Python bridge.</summary>
    /// <param name="language">A PaddleOCR language code, e.g. <c>en</c>, <c>fr</c>, <c>german</c>, <c>ch</c>, <c>japan</c>.</param>
    /// <param name="pythonExecutable">The Python interpreter to run; defaults to <c>python3</c> on <c>PATH</c>.</param>
    /// <param name="scriptPath">
    /// Path to <c>zerodep_paddle_ocr.py</c>; defaults to the copy shipped next to this assembly.
    /// </param>
    public PaddleOcrEngine(string language = "en", string pythonExecutable = "python3", string? scriptPath = null)
        : this(language, pythonExecutable, scriptPath, Limits.InitTimeout, Limits.RequestTimeout)
    {
    }

    /// <summary>Creates an engine with explicit time limits (tests use short ones).</summary>
    internal PaddleOcrEngine(string language, string pythonExecutable, string? scriptPath, TimeSpan initTimeout, TimeSpan requestTimeout)
    {
        _language = string.IsNullOrWhiteSpace(language) ? "en" : language;
        _python = string.IsNullOrWhiteSpace(pythonExecutable) ? "python3" : pythonExecutable;
        _script = scriptPath ?? Path.Combine(AppContext.BaseDirectory, "zerodep_paddle_ocr.py");
        _initTimeout = initTimeout;
        _requestTimeout = requestTimeout;
    }

    /// <inheritdoc/>
    public OcrResult Recognize(DecodedImage image, OcrOptions options)
    {
        if (image is null)
        {
            throw new ArgumentNullException(nameof(image));
        }

        options ??= new OcrOptions();
        var lines = new List<OcrLine>();
        BoundingBox pageBounds = new BoundingBox(0, 0, image.Width, image.Height);

        foreach (PaddleWord word in RunPaddle(image))
        {
            if (word.Confidence < options.MinConfidence)
            {
                continue;
            }

            lines.Add(new OcrLine
            {
                Text = word.Text,
                Confidence = word.Confidence,
                Bounds = word.Box ?? pageBounds,
            });
        }

        return new OcrResult { Lines = lines };
    }

    /// <summary>
    /// Recognizes detections with their bounding boxes and confidences. Used by the accuracy benchmark
    /// for box-aligned scoring. Not part of the public OCR surface.
    /// </summary>
    internal IReadOnlyList<OcrWordBox> RecognizeWords(DecodedImage image)
    {
        if (image is null)
        {
            throw new ArgumentNullException(nameof(image));
        }

        var words = new List<OcrWordBox>();
        foreach (PaddleWord word in RunPaddle(image))
        {
            words.Add(new OcrWordBox(
                word.Text,
                word.Confidence,
                word.Box ?? new BoundingBox(0, 0, image.Width, image.Height)));
        }

        return words;
    }

    /// <summary>
    /// Shuts down the persistent PaddleOCR worker process, if one was started. The worker is asked to
    /// exit by closing its input and is killed if it has not exited shortly after; a request blocked on
    /// it then fails with <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Process? worker = Interlocked.Exchange(ref _worker, null);
        if (worker is null)
        {
            return;
        }

        try
        {
            if (!worker.HasExited)
            {
                worker.StandardInput.Close();   // EOF → the worker's read loop exits
                worker.WaitForExit((int)Limits.ShutdownGrace.TotalMilliseconds);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // Best-effort graceful shutdown; the kill below still runs.
        }

        Terminate(worker);
    }

    private IReadOnlyList<PaddleWord> RunPaddle(DecodedImage image)
    {
        using TempImageFile tmp = TempImageFile.Create(Bmp.Encode(image));

        lock (_gate)
        {
            ThrowIfDisposed();
            Process worker = EnsureWorker();
            string id = Interlocked.Increment(ref _nextRequestId).ToString(System.Globalization.CultureInfo.InvariantCulture);

            try
            {
                worker.StandardInput.WriteLine(Request(id, tmp.Path));
                worker.StandardInput.Flush();

                var deadline = Stopwatch.StartNew();
                while (true)
                {
                    string? line = ReadLine(worker, _requestTimeout - deadline.Elapsed);
                    if (line is null)
                    {
                        Retire(worker);
                        ThrowIfDisposed();
                        throw new InvalidOperationException(
                            $"paddleocr worker exited unexpectedly. Recent stderr:\n{StderrTail()}");
                    }

                    // Only the response that echoes this request's id is ours; anything else (a log line
                    // that reached stdout, or a late reply to an earlier request) is skipped.
                    if (TryParseResponse(line, id, out List<PaddleWord> words))
                    {
                        return words;
                    }
                }
            }
            catch (TimeoutException)
            {
                Retire(worker);
                ThrowIfDisposed();
                throw new InvalidOperationException(
                    $"paddleocr worker did not answer within {_requestTimeout.TotalSeconds:0.#} s and was stopped.");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The worker's pipes closed under us: it died, or Dispose stopped it.
                Retire(worker);
                ThrowIfDisposed();
                throw new InvalidOperationException(
                    $"paddleocr worker exited unexpectedly. Recent stderr:\n{StderrTail()}", ex);
            }
        }
    }

    // Starts (once) the persistent worker and waits for its READY handshake. Caller holds _gate.
    private Process EnsureWorker()
    {
        Process? existing = _worker;
        if (existing is not null)
        {
            if (!existing.HasExited)
            {
                return existing;
            }

            Retire(existing);
        }

        if (!File.Exists(_script))
        {
            throw new InvalidOperationException(
                $"PaddleOCR bridge script not found at '{_script}'. Ensure zerodep_paddle_ocr.py ships with the adapter.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = _python,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardInputEncoding = Utf8NoBom,
        };
        psi.ArgumentList.Add(_script);
        psi.ArgumentList.Add("--serve");
        psi.ArgumentList.Add(_language);

        var proc = new Process { StartInfo = psi };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (_stderrTail)
            {
                _stderrTail.Enqueue(e.Data);
                while (_stderrTail.Count > 50)
                {
                    _stderrTail.Dequeue();
                }
            }
        };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            proc.Dispose();
            throw new InvalidOperationException(
                $"Could not start '{_python}'. Install Python and the paddleocr package (pip install paddleocr).", ex);
        }

        // Publish the worker before the (possibly long) model load so Dispose can stop it meanwhile.
        _worker = proc;
        if (_disposed)
        {
            Retire(proc);
            throw new ObjectDisposedException(nameof(PaddleOcrEngine));
        }

        try
        {
            proc.BeginErrorReadLine();

            // Block until the worker has loaded its model (first run may download models). Lines before
            // READY are not part of the protocol and are skipped.
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                string? ready = ReadLine(proc, _initTimeout - deadline.Elapsed);
                if (ready is null)
                {
                    break;
                }

                if (ready.StartsWith("READY", StringComparison.Ordinal))
                {
                    return proc;
                }
            }
        }
        catch (TimeoutException)
        {
            Retire(proc);
            ThrowIfDisposed();
            throw new InvalidOperationException(
                $"paddleocr worker did not become ready within {_initTimeout.TotalSeconds:0.#} s and was stopped. Recent stderr:\n{StderrTail()}");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Retire(proc);
            ThrowIfDisposed();
            throw new InvalidOperationException(
                $"paddleocr worker failed to initialize. Recent stderr:\n{StderrTail()}", ex);
        }

        Retire(proc);
        ThrowIfDisposed();
        throw new InvalidOperationException(
            $"paddleocr worker failed to initialize. Recent stderr:\n{StderrTail()}");
    }

    // Reads one stdout line, or throws TimeoutException when none arrives in time. The read itself is not
    // cancelled (pipe reads do not honour cancellation on every platform); instead the caller kills the
    // worker, which ends the pending read.
    private static string? ReadLine(Process worker, TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException();
        }

        Task<string?> read = worker.StandardOutput.ReadLineAsync();
        if (!read.Wait(remaining))
        {
            // Observe the abandoned read's eventual fault so it is not reported as unobserved.
            read.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            throw new TimeoutException();
        }

        return read.GetAwaiter().GetResult();
    }

    // Removes the worker if it is still the current one, then kills and disposes it.
    private void Retire(Process worker)
    {
        Interlocked.CompareExchange(ref _worker, null, worker);
        Terminate(worker);
    }

    // Kills the worker's whole process tree (Python may start helpers), reaps it, and disposes it. Safe to
    // call more than once, and concurrently from Dispose and a failing request.
    private static void Terminate(Process worker)
    {
        try
        {
            worker.Kill(entireProcessTree: true);
            worker.WaitForExit((int)Limits.KillGrace.TotalMilliseconds);
        }
        catch (InvalidOperationException)
        {
            // Already exited (or disposed).
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The process could not be signalled (e.g. it is exiting); nothing more can be done.
        }

        worker.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PaddleOcrEngine));
        }
    }

    // One request per line: {"id": "...", "path": "..."}. JSON keeps any path characters unambiguous.
    private static string Request(string id, string path)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WriteString("path", path);
            writer.WriteEndObject();
        }

        return Utf8NoBom.GetString(buffer.ToArray());
    }

    private string StderrTail()
    {
        lock (_stderrTail)
        {
            return string.Join("\n", _stderrTail);
        }
    }

    // True when the line is the JSON response to request <paramref name="id"/>; any other line is skipped.
    private static bool TryParseResponse(string line, string id, out List<PaddleWord> words)
    {
        words = new List<PaddleWord>();
        string trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] != '{')
        {
            return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(trimmed);
        }
        catch (JsonException)
        {
            return false;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("id", out JsonElement echoed)
                || echoed.ValueKind != JsonValueKind.String
                || echoed.GetString() != id)
            {
                return false;
            }

            if (doc.RootElement.TryGetProperty("words", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
            {
                ReadWords(arr, words);
            }

            return true;
        }
    }

    private static void ReadWords(JsonElement arr, List<PaddleWord> words)
    {
        foreach (JsonElement w in arr.EnumerateArray())
        {
            string text = w.TryGetProperty("text", out JsonElement t) ? (t.GetString() ?? string.Empty) : string.Empty;
            if (text.Length == 0)
            {
                continue;
            }

            double conf = w.TryGetProperty("conf", out JsonElement c) ? c.GetDouble() : 0.0;
            BoundingBox? box = null;
            if (w.TryGetProperty("box", out JsonElement b) && b.ValueKind == JsonValueKind.Array && b.GetArrayLength() >= 4)
            {
                box = new BoundingBox(b[0].GetDouble(), b[1].GetDouble(), b[2].GetDouble(), b[3].GetDouble());
            }

            words.Add(new PaddleWord(text, conf, box));
        }
    }

    private readonly struct PaddleWord
    {
        public PaddleWord(string text, double confidence, BoundingBox? box)
        {
            Text = text;
            Confidence = confidence;
            Box = box;
        }

        public string Text { get; }

        public double Confidence { get; }

        public BoundingBox? Box { get; }
    }
}

/// <summary>A single recognized detection with its confidence and image-space bounding box.</summary>
internal sealed class OcrWordBox
{
    public OcrWordBox(string text, double confidence, BoundingBox box)
    {
        Text = text;
        Confidence = confidence;
        Box = box;
    }

    /// <summary>The recognized text.</summary>
    public string Text { get; }

    /// <summary>Confidence in [0, 1].</summary>
    public double Confidence { get; }

    /// <summary>Bounding box in image pixels (origin top-left).</summary>
    public BoundingBox Box { get; }
}
