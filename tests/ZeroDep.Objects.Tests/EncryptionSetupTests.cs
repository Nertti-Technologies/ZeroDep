using System.Security.Cryptography;
using Xunit;
using ZeroDep.Abstractions;
using static ZeroDep.Objects.Tests.CraftedPdf;

namespace ZeroDep.Objects.Tests;

/// <summary>
/// Regression tests for the standard security handler setup: key-length and /UE invariants, the AESV2 key
/// length, and the Identity, /StrF and /EncryptMetadata crypt-filter rules.
/// </summary>
public sealed class EncryptionSetupTests
{
    private static PdfFile Open(byte[] pdf) => PdfFile.Open(new MemoryStream(pdf));

    [Fact]
    public void Rc4KeyLengthAbove16BytesThrowsSyntaxException()
    {
        var objects = BasicPage(A("q Q"));
        objects[5] = A($"<< /Filter /Standard /V 2 /R 3 /Length 256 /P -4 /O <{new string('A', 64)}> /U <{new string('B', 64)}> >>");
        byte[] pdf = Classic(objects, $"/Encrypt 5 0 R {IdTrailer}");

        var ex = Bounded.Throws<PdfSyntaxException>(() => Open(pdf).Dispose());
        Assert.Contains("5..16", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Revision5WithShortUeThrowsSyntaxException()
    {
        byte[] validationSalt = new byte[8], keySalt = new byte[8];
        byte[] u = Concat(SHA256.HashData(validationSalt), validationSalt, keySalt);   // empty password authenticates
        var objects = BasicPage(A("q Q"));
        objects[5] = A($"<< /Filter /Standard /V 5 /R 5 /Length 256 /P -4 /O <{Hex(new byte[48])}> /U <{Hex(u)}> /OE <00> /UE <0011223344> >>");
        byte[] pdf = Classic(objects, $"/Encrypt 5 0 R {IdTrailer}");

        var ex = Bounded.Throws<PdfSyntaxException>(() => Open(pdf).Dispose());
        Assert.Contains("/UE", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/Length 16")]
    [InlineData("/Length 128")]
    [InlineData("")]
    public void AesV2WithoutTopLevelLengthUsesA128BitKey(string cryptFilterLength)
    {
        const int permissions = -4;
        byte[] key = FileKey(16, permissions);
        byte[] plain = A("BT /F1 12 Tf 72 720 Td (Hello) Tj ET");
        var objects = BasicPage(AesEncrypt(ObjectKey(key, 4, aes: true), plain));
        objects[5] = A($"<< /Filter /Standard /V 4 /R 4 /P {permissions} /CF << /StdCF << /CFM /AESV2 {cryptFilterLength} /AuthEvent /DocOpen >> >> /StmF /StdCF /StrF /StdCF /O <{Hex(OwnerValue())}> /U <{Hex(UserValue(key))}> >>");
        byte[] pdf = Classic(objects, $"/Encrypt 5 0 R {IdTrailer}");

        using PdfFile file = Open(pdf);
        Assert.Equal(AuthenticationResult.UserPassword, file.Authentication);
        Assert.Equal(EncryptionAlgorithm.Aes128, file.Algorithm);
        Assert.Equal(plain, Assert.IsType<PdfStream>(file.GetObject(4)).GetRawBytes());
    }

    [Fact]
    public void Rc4CryptFilterLengthIsUsedWhenTopLevelLengthIsAbsent()
    {
        const int permissions = -4;
        byte[] key = FileKey(16, permissions);
        byte[] plain = A("q Q");
        var objects = BasicPage(Rc4(ObjectKey(key, 4, aes: false), plain));
        objects[5] = A($"<< /Filter /Standard /V 4 /R 4 /P {permissions} /CF << /StdCF << /CFM /V2 /Length 16 >> >> /StmF /StdCF /StrF /StdCF /O <{Hex(OwnerValue())}> /U <{Hex(UserValue(key))}> >>");
        byte[] pdf = Classic(objects, $"/Encrypt 5 0 R {IdTrailer}");

        using PdfFile file = Open(pdf);
        Assert.Equal(AuthenticationResult.UserPassword, file.Authentication);
        Assert.Equal(plain, Assert.IsType<PdfStream>(file.GetObject(4)).GetRawBytes());
    }

    [Fact]
    public void IdentityStreamFilterLeavesStreamsAloneWhileStringsAreDecrypted()
    {
        const int permissions = -4;
        byte[] key = FileKey(16, permissions);
        byte[] plain = A("q Q");
        byte[] title = A("Quarterly report");
        var objects = BasicPage(plain);
        objects[1] = A($"<< /Type /Catalog /Pages 2 0 R /Lang <{Hex(Rc4(ObjectKey(key, 1, aes: false), title))}> >>");
        objects[5] = A($"<< /Filter /Standard /V 4 /R 4 /Length 128 /P {permissions} /CF << /StdCF << /CFM /V2 /Length 16 >> >> /StmF /Identity /StrF /StdCF /O <{Hex(OwnerValue())}> /U <{Hex(UserValue(key))}> >>");
        byte[] pdf = Classic(objects, $"/Encrypt 5 0 R {IdTrailer}");

        using PdfFile file = Open(pdf);
        Assert.Equal(AuthenticationResult.UserPassword, file.Authentication);
        Assert.Equal(plain, Assert.IsType<PdfStream>(file.GetObject(4)).GetRawBytes());
        var catalog = Assert.IsType<PdfDictionary>(file.GetObject(1));
        Assert.Equal(title, Assert.IsType<PdfString>(catalog["Lang"]!).ToArray());
    }

    [Fact]
    public void IdentityStringFilterLeavesStringsAloneWhileStreamsAreDecrypted()
    {
        const int permissions = -4;
        byte[] key = FileKey(16, permissions);
        byte[] plain = A("q Q");
        var objects = BasicPage(AesEncrypt(ObjectKey(key, 4, aes: true), plain));
        objects[1] = A("<< /Type /Catalog /Pages 2 0 R /Lang (en-GB) >>");
        objects[5] = A($"<< /Filter /Standard /V 4 /R 4 /P {permissions} /CF << /StdCF << /CFM /AESV2 /Length 16 >> >> /StmF /StdCF /StrF /Identity /O <{Hex(OwnerValue())}> /U <{Hex(UserValue(key))}> >>");
        byte[] pdf = Classic(objects, $"/Encrypt 5 0 R {IdTrailer}");

        using PdfFile file = Open(pdf);
        Assert.Equal(plain, Assert.IsType<PdfStream>(file.GetObject(4)).GetRawBytes());
        var catalog = Assert.IsType<PdfDictionary>(file.GetObject(1));
        Assert.Equal(A("en-GB"), Assert.IsType<PdfString>(catalog["Lang"]!).ToArray());
    }

    [Fact]
    public void MetadataStreamIsNotDecryptedWhenEncryptMetadataIsFalse()
    {
        const int permissions = -4;
        byte[] key = FileKey(16, permissions, encryptMetadata: false);
        byte[] plain = A("q Q");
        byte[] xmp = A("<metadata><title>Quarterly report</title></metadata>");
        var objects = BasicPage(Rc4(ObjectKey(key, 4, aes: false), plain));
        objects[1] = A("<< /Type /Catalog /Pages 2 0 R /Metadata 5 0 R >>");
        objects[5] = Stream("/Type /Metadata /Subtype /XML", xmp);
        objects[6] = A($"<< /Filter /Standard /V 4 /R 4 /Length 128 /P {permissions} /EncryptMetadata false /CF << /StdCF << /CFM /V2 /Length 16 >> >> /StmF /StdCF /StrF /StdCF /O <{Hex(OwnerValue())}> /U <{Hex(UserValue(key))}> >>");
        byte[] pdf = Classic(objects, $"/Encrypt 6 0 R {IdTrailer}");

        using PdfFile file = Open(pdf);
        Assert.Equal(AuthenticationResult.UserPassword, file.Authentication);
        Assert.False(file.EncryptMetadata);
        Assert.Equal(plain, Assert.IsType<PdfStream>(file.GetObject(4)).GetRawBytes());
        Assert.Equal(xmp, Assert.IsType<PdfStream>(file.GetObject(5)).GetRawBytes());
    }
}
