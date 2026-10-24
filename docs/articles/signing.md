# Signed updates

> ¿Prefieres español? Lee la versión en [español](signing.es.md).

How the app knows an update was published by whoever maintains it, and what to do when the key that
proves it is lost, leaked or due to be replaced. Tracked in
[#33](https://github.com/JuanP-G/MC-ServerLauncher/issues/33).

## Why a checksum was not enough

The in-app updater downloads the package from the GitHub release and compares it with a checksum
(`SHA256SUMS.txt`, or `<package>.sha256`) that comes **from the same release**. That catches a damaged
or truncated download. It does not catch a compromised account or release: whoever can upload the
package can upload its hash too, and on Windows the installer then runs silently and elevated.

A signature closes that gap because the key that makes it **does not live on GitHub**.

## What is signed, and how it is checked

| | |
| --- | --- |
| Algorithm | ECDSA over P-256 with SHA-256 |
| What is signed | A short message with the format, the **version**, the **file name** and the package's SHA-256 (below), for the `.exe`, the `.AppImage` and each `.dmg` |
| Where the signature is | A `<package>.sig2` asset in the same release: the DER signature, in base64, as text |
| Where the public key is | In the app: `UpdateSignature.TrustedKeys` (SubjectPublicKeyInfo, base64) |
| Where the private key is | On the maintainer's machine, encrypted, outside the repository and outside GitHub |

ECDSA P-256 is what .NET ships, so the update path adds **no package of its own**, and it is what
`openssl` and `cosign` produce, so anyone can check a release by hand (below).

In `MainViewModel.UpdateNow`, before anything is installed:

1. The checksum and the signature are read. **Both are mandatory**: if either is missing or unreadable
   the update is refused and the release page opens, as it already did for the checksum.
2. The package is downloaded and its checksum checked.
3. `UpdateSignature.Verify` checks the signature against every trusted key. If none accepts it, the
   package is deleted, the user is told, and the release page opens. Nothing is installed.

`Verify` never throws on bad input: a signature file is downloaded from the same place an attacker would
write to, and a crash would be a way around the check.

### The signed message

The signature is not of the package bytes alone but of four lines, joined by `\n` with nothing after the
last (`UpdateSignature.SignedMessage` and `tools\signing\sign-package.ps1` build the same text):

```
mcsl-update-v1
1.21.2
MC-ServerLauncher-Setup-1.21.2.exe
<the package's SHA-256, lowercase>
```

With the version inside, an old release that was signed correctly in its day does not verify when it is
offered again under a newer tag (the tag is what a person with write access chooses), and with the file
name inside, the `.dmg`'s signature does not verify as the `.exe`'s. The first line names the format, so a
later layout cannot be mistaken for this one.

### Two formats while 1.21.1 is around

Apps on 1.21.1 read `<package>.sig`, a signature of the package bytes alone, and know nothing of `.sig2`.
So every release publishes **both**: `.sig` so those apps can update, `.sig2` for everyone after. Apps
newer than 1.21.1 read only `.sig2` and never fall back to `.sig`: if they did, removing one asset would
downgrade the check to the weaker one. When nobody is left on 1.21.1, `sign-package.ps1 -NoLegacy` stops
writing `.sig`.

## Publishing a release

1. `publish.ps1` builds the installer and, if the key exists, signs it: `dist/…Setup-X.Y.Z.exe.sig2` and
   `.sig`. Attach both to the release next to the `.exe` and `SHA256SUMS.txt`.
2. Publishing the release triggers the Linux and macOS workflows, which attach the AppImage, the two
   `.dmg`s and their `.sha256`. **They cannot sign**: that would put the key on GitHub.
3. When they finish, run `tools\signing\sign-release.ps1 -Tag vX.Y.Z`. It waits for the packages,
   downloads them, checks each against its `.sha256`, prints the hashes, asks for the key's password
   once, signs them and uploads the `.sig2` and `.sig` files.

Between steps 2 and 3 (about five minutes) anyone who presses Update on Linux or macOS is sent to the
release page instead of being updated: the safe failure.

The hashes printed in step 3 are also in the workflow logs. This flow signs **what GitHub Actions
built**, so if you ever doubt the build, compare them before typing the password.

## The key

Create it once with `tools\signing\new-key.ps1`. It asks for a password (at least 12 characters), writes
the encrypted private key to `%USERPROFILE%\.mcsl-signing\` (or `MCSL_SIGNING_DIR`) and prints the public
key to paste into `UpdateSignature.TrustedKeys`.

Keep a **backup outside the machine** (a password manager's attachment, an encrypted drive). If the key is
lost, every installed app still trusts it and nothing can be signed with it: nobody could update from
inside the app, and everyone would have to reinstall by hand.

### Rotating it

`TrustedKeys` is a list so a key can be replaced without stranding anyone. An installed app only trusts
the keys it shipped with, so the order matters:

1. Create the new key (in another `MCSL_SIGNING_DIR`; the script refuses to overwrite).
2. **Release N**: `TrustedKeys = [new, old]`, and the packages are signed with the **old** key, because
   the apps that will install N only trust that one.
3. **Release N+1**: signed with the **new** key. Apps on N trust both.
4. Once nobody is left on N−1 or older, drop the old key from the list.

### If the key leaks

Installed apps keep trusting it, so whoever has it can sign an update they would accept. The only fix is
a release that removes it from `TrustedKeys`, signed with the last use of the leaked key, so ship it
fast; publish a security advisory as well. This is why the key stays encrypted and off GitHub.

## What this does not cover

- **Versions before the first signed one** (1.21.1, published on 2026-10-24, is the first) do not
  check anything. The protection starts with the update *after* the one that carries the key;
  installing that first one still relies on the checksum.
- **Downgrade, on 1.21.1.** Apps on 1.21.1 check the older `.sig`, which is tied to no version, so an
  old release correctly signed still passes for them. Apps that read `.sig2` are not affected.
- **Authenticode** (signing the Windows `.exe` with a code-signing certificate, which also removes the
  SmartScreen warning) is a separate, optional step and is **not done**. It needs a certificate that is
  not free, apart from programmes for open-source projects.
- **The macOS app is only ad-hoc signed**, as the workflow says; notarisation needs an Apple Developer
  account.
- **What the maintainer signs is what the build produced.** A signature proves who published it, not
  that the build was clean.

## Verifying a release by hand

With the public key written as a PEM file (`BEGIN PUBLIC KEY`, the base64 from `UpdateSignature.cs`
wrapped at 64 columns, `END PUBLIC KEY`):

```bash
V=1.21.2; F=MC-ServerLauncher-x86_64.AppImage
printf 'mcsl-update-v1\n%s\n%s\n%s' "$V" "$F" "$(sha256sum "$F" | cut -d' ' -f1)" > message.txt
base64 -d "$F.sig2" > sig.der
openssl dgst -sha256 -verify public.pem -signature sig.der message.txt
```

It prints `Verified OK` when the package is the one that was signed, for that version and that name.
