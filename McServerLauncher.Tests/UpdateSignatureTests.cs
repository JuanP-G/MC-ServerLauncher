using System.Security.Cryptography;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The check that stands between a release on GitHub and the installer that runs with elevation.
/// </summary>
/// <remarks>
/// <para>
/// Every case here is one way the check could say "yes" to something it should refuse, or blow up
/// instead of saying "no": a package changed after it was signed, a signature by somebody else, the
/// signature of another version or of another file, a key list that is empty or half broken, and
/// input that is not a signature at all. The last one matters most — the signature file is
/// downloaded from the same place an attacker would write to.
/// </para>
/// <para>
/// The keys are generated per test, so the real public key is never needed and no private key lives
/// in the repository.
/// </para>
/// </remarks>
public class UpdateSignatureTests : IDisposable
{
    private const string Version = "1.21.2";
    private const string FileName = "MC-ServerLauncher-Setup-1.21.2.exe";

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

    /// <summary>What the signing script writes into the .sig2 asset.</summary>
    private static string Sign(ECDsa key, string packagePath, string version = Version,
        string fileName = FileName)
    {
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath)));
        return Convert.ToBase64String(key.SignData(
            UpdateSignature.SignedMessage(version, fileName, hash),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    private static bool Verify(string package, string? signature, params string[] keys) =>
        UpdateSignature.Verify(package, Version, FileName, signature, keys);

    [Fact]
    public void TheKeysBuiltIntoTheAppAreUsable()
    {
        // A release with an empty or mistyped list would refuse every update from inside the app, and
        // nothing else would say so until somebody pressed the button. Each key has to parse and be
        // the P-256 one the signing scripts make.
        Assert.NotEmpty(UpdateSignature.TrustedKeys);
        foreach (var key in UpdateSignature.TrustedKeys)
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
            Assert.Equal(256, ecdsa.KeySize);
        }
    }

    [Fact]
    public void ASignatureMadeWithATrustedKeyIsAccepted()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = Package();

        Assert.True(Verify(package, Sign(key, package), PublicKey(key)));
    }

    [Fact]
    public void ThePackageChangingAfterItWasSignedIsRefused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = Package("the real installer");
        var signature = Sign(key, package);

        // The one-byte difference an attacker with write access to the release would make.
        File.WriteAllText(package, "the real installer, plus something extra");

        Assert.False(Verify(package, signature, PublicKey(key)));
    }

    [Fact]
    public void AnOldReleaseOfferedAsANewerOneIsRefused()
    {
        // The reason the version is in the message: 1.21.1 was signed correctly and still is. Offered
        // again as 1.99.0 — the tag is the one thing a person with write access chooses — it must not
        // pass as that.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = Package();
        var signedAs121 = Sign(key, package, version: "1.21.1");

        Assert.False(UpdateSignature.Verify(package, "1.99.0", FileName, signedAs121, [PublicKey(key)]));
        Assert.True(UpdateSignature.Verify(package, "1.21.1", FileName, signedAs121, [PublicKey(key)]));
    }

    [Fact]
    public void ASignatureForAnotherFileIsRefused()
    {
        // The .dmg and the .exe of one release share a version and a key. Their signatures must not
        // be interchangeable, or swapping one asset for another would go unnoticed.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = Package();
        var signedAsDmg = Sign(key, package, fileName: "MC-ServerLauncher-Intel.dmg");

        Assert.False(Verify(package, signedAsDmg, PublicKey(key)));
    }

    [Fact]
    public void AFolderInTheFileNameDoesNotMatter()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = Package();

        Assert.True(UpdateSignature.Verify(package, Version, @"C:\temp\" + FileName,
            Sign(key, package), [PublicKey(key)]));
    }

    [Fact]
    public void ASignatureFromAnotherKeyIsRefused()
    {
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = Package();

        Assert.False(Verify(package, Sign(attacker, package), PublicKey(publisher)));
    }

    [Fact]
    public void ARetiredKeyAndItsReplacementAreBothTrustedDuringARotation()
    {
        using var oldKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = Package();
        string[] trusted = [PublicKey(newKey), PublicKey(oldKey)];

        Assert.True(Verify(package, Sign(oldKey, package), trusted));
        Assert.True(Verify(package, Sign(newKey, package), trusted));
    }

    [Fact]
    public void WithNoTrustedKeyNothingIsAccepted()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = Package();

        Assert.False(Verify(package, Sign(key, package)));
    }

    [Fact]
    public void ABrokenKeyInTheListDoesNotHideAGoodOne()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = Package();

        Assert.True(Verify(package, Sign(key, package), "not a key at all", "AAAA", PublicKey(key)));
    }

    [Fact]
    public void OnlyP256KeysAreTrusted()
    {
        // A key on another curve would also verify its own signatures; the list is for the one curve
        // the signing script produces, so anything else in it is a mistake to refuse, not to honour.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var package = Package();

        Assert.False(Verify(package, Sign(key, package), PublicKey(key)));
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

        Assert.False(Verify(Package(), signature, PublicKey(key)));
    }

    [Theory]
    [InlineData("", FileName)]
    [InlineData("  ", FileName)]
    [InlineData(Version, "")]
    public void AMissingVersionOrFileNameIsRefused(string version, string fileName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = Package();

        Assert.False(UpdateSignature.Verify(package, version, fileName, Sign(key, package),
            [PublicKey(key)]));
    }

    [Fact]
    public void TheSignatureFileMayEndInANewline()
    {
        // `base64 file.der > file.sig2` and most editors leave one; the asset still has to work.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = Package();

        Assert.True(Verify(package, Sign(key, package) + "\r\n", PublicKey(key)));
    }

    [Fact]
    public void TheMessageIsExactlyWhatTheSigningScriptBuilds()
    {
        // tools/signing/sign-package.ps1 writes these same bytes before signing. If this changes, that
        // script has to change with it, or every release after would be refused by the app.
        var message = System.Text.Encoding.UTF8.GetString(
            UpdateSignature.SignedMessage("1.21.2", "pkg.exe", "ABCDEF"));

        Assert.Equal("mcsl-update-v1\n1.21.2\npkg.exe\nabcdef", message);
    }
}
