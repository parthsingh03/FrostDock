# Signing FrostDock

FrostDock is distributed as a single `FrostDock.exe`. Windows Smart App Control
blocks unsigned executables, so releases intended for other machines should
be signed with a certificate from a trusted provider (for example through
Microsoft Store publishing or a free open-source signing programme), then
timestamped so the signature stays valid after the certificate expires.

A typical signing step, once a certificate is available, uses Microsoft's
`signtool` from the Windows SDK:

```cmd
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 FrostDock.exe
```

The exact arguments depend on how the certificate is stored (cloud signing
service, hardware token, or certificate file). Self-signed certificates do
not pass Smart App Control — they are only useful for local testing.
