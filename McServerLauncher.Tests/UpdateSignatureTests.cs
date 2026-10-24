using System.Security.Cryptography;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The check that stands between a release on GitHub and the installer that runs with elevation.
/// </summary>
/// <remarks>
/// <para>
/// Every case here is one way the check could say "yes" to something it should refuse, or blow up
/// instead of saying "no": a package changed after it was signed, a signature by somebody else, a
/// key list that is empty or half broken, and input that is not a signature at all. The last one
/// matters most — the signature file is downloaded from the same place an attacker would write to.
/// </para>
/// <para>
/// The keys are generated per test, so the real public key is never needed and no private key lives
/// in the repository.
/// </para>
/// </remarks>
public class UpdateSignatureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcsl-sigtests-" + Path.GetRandomFileName());

    public UpdateSignatureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* a temporary folder is not worth failing a test over */ }
    }

    private string Package(string content = "pretend this is the installer")
    {
        var path = Path.Combine(_dir, Path.GetRandomFileName());
        File.WriteAllText(path, content);
        return path;
    }

    private static string PublicKey(ECDsa key) =>
        Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    /// <summary>What <c>openssl dgst -sha256 -sign</c> writes, as the .sig asset holds it.</summary>
    private static string Sign(ECDsa key, string packagePath)
    {
        using var stream = File.OpenRead(packagePath);
        return Convert.ToBase64String(
            key.SignData(stream, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    [Fact]
    public void ASignatureMadeWithATrustedKeyIsAccepted()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = Package();

        Assert.True(UpdateSignature.Verify(package, Sign(key, package), [PublicKey(key)]));
    }

    [Fact]
    public void ThePackageChangingAfterItWasSignedIsRefused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = Package("the real installer");
        var signature = Sign(key, package);

        // The one-byte difference an attacker with write access to the release would make.
        File.WriteAllText(package, "the real installer, plus something extra");

        Assert.False(UpdateSignature.Verify(package, signature, [PublicKey(key)]));
    }

    [Fact]
    public void ASignatureFromAnotherKeyIsRefused()
    {
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = Package();

        Assert.False(UpdateSignature.Verify(package, Sign(attacker, package), [PublicKey(publisher)]));
    }

    [Fact]
    public void ARetiredKeyAndItsReplacementAreBothTrustedDuringARotation()
    {
        using var oldKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = Package();
        string[] trusted = [PublicKey(newKey), PublicKey(oldKey)];

        Assert.True(UpdateSignature.Verify(package, Sign(oldKey, package), trusted));
        Assert.True(UpdateSignature.Verify(package, Sign(newKey, package), trusted));
    }

    [Fact]
    public void WithNoTrustedKeyNothingIsAccepted()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = Package();

        Assert.False(UpdateSignature.Verify(package, Sign(key, package), []));
    }

    [Fact]
    public void ABrokenKeyInTheListDoesNotHideAGoodOne()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = Package();

        Assert.True(UpdateSignature.Verify(package, Sign(key, package),
            ["not a key at all", "AAAA", PublicKey(key)]));
    }

    [Fact]
    public void OnlyP256KeysAreTrusted()
    {
        // A key on another curve would also verify its own signatures; the list is for the one curve
        // the signing script produces, so anything else in it is a mistake to refuse, not to honour.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var package = Package();

        Assert.False(UpdateSignature.Verify(package, Sign(key, package), [PublicKey(key)]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("this is not base64 !!")]
    [InlineData("AAAA")]
    [InlineData("MEUCIQDxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxAiBxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public void InputThatIsNotASignatureIsRefusedWithoutThrowing(string? signature)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.False(UpdateSignature.Verify(Package(), signature, [PublicKey(key)]));
    }

    [Fact]
    public void TheSignatureFileMayEndInANewline()
    {
        // `base64 file.der > file.sig` and most editors leave one; the asset still has to work.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = Package();

        Assert.True(UpdateSignature.Verify(package, Sign(key, package) + "\r\n", [PublicKey(key)]));
    }
}
