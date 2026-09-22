# Code signing

Unsigned Windows executables are the single biggest source of "Windows
protected your PC" (SmartScreen) and antivirus heuristic warnings for a new
app. This doc explains what's set up in this repo, what it actually achieves,
and what's needed to go further.

## What's in the repo

- `scripts/New-SigningCertificate.ps1` — creates a self-signed code-signing
  certificate and exports it to `build/signing/` (gitignored — it's private
  key material, never commit it).
- `scripts/Sign-Binaries.ps1` — signs one or more files (exe/dll/msi) with
  that certificate via `signtool`.
- `scripts/Import-TrustedPublisherCert.ps1` — installs the certificate's
  public half into *this machine's* Trusted Root + Trusted Publisher stores,
  so signed binaries stop showing a warning *here*.
- `scripts/Build-PortableZip.ps1` signs the published binaries as part of
  producing the distributable ZIP (the shipped deliverable - a prior WiX MSI
  installer path was removed as unused/unverified).

## What a self-signed certificate does and does not do

**Does:** removes the "Unknown publisher" warning and stops some antivirus
heuristics from flagging the binary, but **only on machines where that exact
certificate has been explicitly trusted** — i.e. after running
`Import-TrustedPublisherCert.ps1` (or pushing the `.cer` via Group Policy to a
fleet of managed machines you control).

**Does not:** achieve warning-free distribution to the public. On a stranger's
PC, a self-signed certificate has no chain of trust behind it — Windows and
most antivirus engines still treat it as unknown, and some heuristics
specifically flag self-signed executables as *more* suspicious than plain
unsigned ones (it's a common pattern in malware trying to look legitimate).

**Use this for:** your own machine(s), or an org's fleet where you control
certificate deployment (e.g. via GPO or MDM pushing the `.cer` to
`LocalMachine\Root` + `LocalMachine\TrustedPublisher` on every managed PC).

## Getting to warning-free public distribution

This requires a certificate issued by a Certificate Authority already
trusted by Windows out of the box. Two tiers:

- **Standard (OV) code-signing certificate** — identity-verified, roughly
  $100–400/year from providers like DigiCert, Sectigo, or SSL.com. Removes
  the "unknown publisher" warning immediately, but Microsoft's SmartScreen
  *reputation* system still needs the binary's specific hash to accumulate
  enough clean downloads over time before SmartScreen fully stops
  challenging first-time runs — there's a warm-up period that varies by how
  widely the file is distributed.
- **EV (Extended Validation) code-signing certificate** — business-identity
  verified (typically requires a registered business entity, not just an
  individual), costs more (~$300–700/year) and usually ships on a hardware
  token (USB key or cloud HSM) rather than a plain file, since EV keys can't
  be exported as a `.pfx`. In exchange, SmartScreen trusts it **immediately**
  — no reputation warm-up period. This is the standard choice for
  "warning-free from day one" commercial software.

## Swapping in a real certificate later

Nothing else in the build needs to change:

- If it's a plain `.pfx` (OV cert): drop it at
  `build/signing/LocalWebcamSelfSigned.pfx` (or pass `-PfxPath` to
  `Sign-Binaries.ps1`) with its password in a sibling `.pfx.password` file.
- If it's an EV cert on a hardware token: `signtool sign` addresses it by
  certificate subject/thumbprint instead of a `.pfx` file — update
  `Sign-Binaries.ps1`'s `signtool sign` invocation to use `/n "<subject
  name>"` instead of `/f`/`/p` once you have the token.

Either way, `Import-TrustedPublisherCert.ps1` becomes unnecessary — a
CA-issued certificate is trusted by every Windows machine already, nothing to
import.
