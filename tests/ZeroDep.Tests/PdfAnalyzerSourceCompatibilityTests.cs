using System.IO;
using Xunit;

namespace ZeroDep.Tests;

/// <summary>
/// Code written before the <c>AnalyzerOptions</c> overloads existed passes a literal <c>null</c> password.
/// It must still compile and keep its meaning: these calls only build if overload resolution picks the
/// password overloads.
/// </summary>
public sealed class PdfAnalyzerSourceCompatibilityTests
{
    private static MemoryStream Doc() => new MemoryStream(PdfAnalyzerLimitsTests.Pages("BT /F1 12 Tf (Hello) Tj ET"));

    [Fact]
    public void LiteralNullPassword_BindsToThePasswordOverloads()
    {
        Assert.Equal(PdfAnalyzer.ExtractPlainText(Doc()), PdfAnalyzer.ExtractPlainText(Doc(), null));
        Assert.Equal(PdfAnalyzer.ExtractText(Doc()).Count, PdfAnalyzer.ExtractText(Doc(), null).Count);
        Assert.Equal(PdfAnalyzer.ExtractForm(Doc()).Fields.Count, PdfAnalyzer.ExtractForm(Doc(), null).Fields.Count);
        Assert.Equal(PdfAnalyzer.ExtractImages(Doc()).Count, PdfAnalyzer.ExtractImages(Doc(), null).Count);
        Assert.Equal(PdfAnalyzer.ExtractColorImages(Doc()).Count, PdfAnalyzer.ExtractColorImages(Doc(), null).Count);
    }
}
