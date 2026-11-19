using System.Security.Cryptography;
using Xunit;
using ZeroDep.Abstractions;
using ZeroDep.Security;

namespace ZeroDep.Security.Tests;

/// <summary>
/// The standard security handler rejects parameters that break ISO 32000-2 invariants with
/// <see cref="PdfSyntaxException"/>, instead of raising argument or cryptographic exceptions.
/// </summary>
public sealed class HandlerInvariantTests
{
    private static readonly byte[] Id = new byte[16];

    [Theory]
    [InlineData(32)]
    [InlineData(17)]
    [InlineData(4)]
    public void Rc4KeyLengthOutside5To16BytesIsRejected(int keyLengthBytes)
    {
        var ex = Assert.Throws<PdfSyntaxException>(() => new StandardSecurityHandler(
            EncryptionAlgorithm.Rc4, 3, keyLengthBytes, new byte[32], new byte[32], Array.Empty<byte>(), Array.Empty<byte>(),
            -4, Id, true, null));
        Assert.Contains("5..16", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(16)]
    public void Rc4KeyLengthInsideTheRangeIsAccepted(int keyLengthBytes)
    {
        var handler = new StandardSecurityHandler(
            EncryptionAlgorithm.Rc4, 3, keyLengthBytes, new byte[32], new byte[32], Array.Empty<byte>(), Array.Empty<byte>(),
            -4, Id, true, null);
        Assert.Equal(AuthenticationResult.Failed, handler.Authentication);   // arbitrary O/U: no password matches
    }

    [Theory]
    [InlineData(5)]
    [InlineData(31)]
    [InlineData(48)]
    public void Revision5UeThatIsNot32BytesIsRejected(int ueLength)
    {
        byte[] validationSalt = new byte[8], keySalt = new byte[8];
        byte[] u = Concat(SHA256.HashData(validationSalt), validationSalt, keySalt);   // empty password authenticates

        var ex = Assert.Throws<PdfSyntaxException>(() => new StandardSecurityHandler(
            EncryptionAlgorithm.Aes256, 5, 32, new byte[48], u, new byte[32], new byte[ueLength], -4, Id, true, null));
        Assert.Contains("/UE", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IdentityMethodsLeaveDataUnchanged()
    {
        byte[] validationSalt = new byte[8], keySalt = new byte[8];
        byte[] u = Concat(SHA256.HashData(validationSalt), validationSalt, keySalt);
        var handler = new StandardSecurityHandler(
            EncryptionAlgorithm.Aes256, 5, 32, new byte[48], u, new byte[32], new byte[32], -4, Id, true, null,
            streamMethod: EncryptionAlgorithm.None, stringMethod: EncryptionAlgorithm.None);
        Assert.Equal(AuthenticationResult.UserPassword, handler.Authentication);

        byte[] data = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18 };
        Assert.Equal(data, handler.DecryptStream(data, 4, 0));
        Assert.Equal(data, handler.DecryptString(data, 4, 0));
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
