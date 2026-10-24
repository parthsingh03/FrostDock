# Code signing policy — FrostDock

## Current status

Windows releases of FrostDock are currently **unsigned**. That means Windows
Smart App Control blocks them, because Smart App Control only trusts apps
signed with a valid certificate. This policy describes how signing works once
a certificate is issued; until then, the release page says plainly that a
build is unsigned.

## Who can request signing

Only the project maintainer, Parth Singh, may submit a signing request.
No one else can sign a FrostDock release.

## What gets signed

Only `FrostDock.exe` built from the source code in this public repository
(`src/`), from the commit tagged for that release. No third-party binary is
ever signed under FrostDock's name, and a binary that did not come from this
repository's source is not a FrostDock release, whatever it claims.

## How signing is done

Signing requests are submitted to SignPath, which provides free code signing
for qualifying open-source projects (direct upload for infrequent releases).
The private signing key is held by SignPath Foundation and is never
accessible to the maintainer.

Because the certificate is issued to SignPath Foundation, Windows shows the
publisher as "SignPath Foundation" on signed builds, not the maintainer's
name. If SignPath pauses or revokes the certificate, affected releases
revert to unsigned status, noted on the release page.

## How to verify a signature

In Windows Explorer, right-click `FrostDock.exe`, choose Properties, open
the Digital Signatures tab, and check the signer and certificate validity.
Or in PowerShell:

```powershell
Get-AuthenticodeSignature "FrostDock.exe" | Format-List
```

A valid signature returns `Valid` for Status. An unsigned file returns
`NotSigned` — do not mistake it for a signed build.
