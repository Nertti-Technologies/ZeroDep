using System.Text;
using Xunit;
using ZeroDep.Abstractions;
using ZeroDep.Model;

namespace ZeroDep.Model.Tests;

/// <summary>
/// <see cref="PdfDocument.Open"/> surfaces crafted structures as <see cref="PdfSyntaxException"/> in bounded
/// time and memory, never as a crash or another exception type.
/// </summary>
public sealed class OpenExceptionContractTests
{
    private static byte[] A(string s) => Encoding.Latin1.GetBytes(s);

    private static PdfSyntaxException OpenThrows(byte[] pdf)
    {
        Exception? caught = null;
        long allocated = 0;
        Task task = Task.Run(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                PdfDocument.Open(new MemoryStream(pdf)).Dispose();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        });

        Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "Open did not finish within 10 s.");
        Assert.True(allocated < 300L * 1024 * 1024, $"Open allocated {allocated / (1024 * 1024)} MiB.");
        return Assert.IsType<PdfSyntaxException>(caught);
    }

    private static byte[] Classic(string catalog, string trailerExtra = "")
    {
        var ms = new MemoryStream();
        ms.Write(A("%PDF-1.7\n"));
        long o1 = ms.Position;
        ms.Write(A($"1 0 obj\n{catalog}\nendobj\n"));
        long o2 = ms.Position;
        ms.Write(A("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n"));
        long xref = ms.Position;
        ms.Write(A($"xref\n0 3\n0000000000 65535 f \n{o1:D10} 00000 n \n{o2:D10} 00000 n \ntrailer\n<< /Size 3 /Root 1 0 R {trailerExtra} >>\nstartxref\n{xref}\n%%EOF\n"));
        return ms.ToArray();
    }

    [Fact]
    public void DeeplyNestedCatalogIsASyntaxError()
    {
        const int depth = 100_000;
        OpenThrows(Classic("<< /Type /Catalog /Pages 2 0 R /X " + new string('[', depth) + new string(']', depth) + " >>"));
    }

    [Fact]
    public void ObjectStreamCycleIsASyntaxError()
    {
        // The catalog is listed as compressed inside an object stream that is itself (object 1).
        var ms = new MemoryStream();
        ms.Write(A("%PDF-1.7\n"));
        long xoff = ms.Position;
        byte[] data = { 0, 0, 0, 2, 1, 0, 1, (byte)xoff, 0 };
        ms.Write(A($"2 0 obj\n<< /Type /XRef /W [1 1 1] /Size 3 /Root 1 0 R /Length {data.Length} >>\nstream\n"));
        ms.Write(data);
        ms.Write(A($"\nendstream\nendobj\nstartxref\n{xoff}\n%%EOF\n"));

        OpenThrows(ms.ToArray());
    }

    [Fact]
    public void UnsupportedRc4KeyLengthIsASyntaxError()
    {
        string enc = $"/Encrypt << /Filter /Standard /V 2 /R 3 /Length 256 /P -4 /O <{new string('A', 64)}> /U <{new string('B', 64)}> >> /ID [<00> <00>]";
        OpenThrows(Classic("<< /Type /Catalog /Pages 2 0 R >>", enc));
    }
}
