## DlnRenderer {{VERSION}}

A DLNA media renderer for Windows 10/11 (x64). Two ways to install; both run the same application
and need the .NET 10 desktop runtime.

### Portable

1. Extract `DlnRenderer-{{VERSION}}-win-x64.zip`.
2. Run `install.ps1` from an elevated PowerShell: it copies to Program Files, adds the firewall
   rules and creates shortcuts.
3. Start `DlnRenderer.exe`, then cast to it from any DLNA control point on your network.

### MSIX (per-user, no admin needed to install)

The package is signed, so Windows can install it, but with a **development certificate** rather than
one from a CA. Trust it once, as administrator:

```powershell
Import-Certificate -FilePath DlnRenderer-dev.cer -CertStoreLocation Cert:\LocalMachine\Root
```

**Read this before trusting it.** The private key for that certificate is committed in the public
repository, so anyone can produce a package Windows labels `CN=AppPublisher`. Trusting it is what
lets Windows install the package; it does **not** prove the package came from this project.
`SHA256SUMS-assets.txt` in this release is what proves that.

The certificate is stable across releases on purpose, so you only trust it once and future upgrades
keep working.

Then install without elevation:

```powershell
Add-AppxPackage -Path DlnRenderer-{{VERSION}}-win-x64.msix
```

### Verify

Every asset is listed below and in `SHA256SUMS-assets.txt`. To check a downloaded file:

```powershell
Get-FileHash -Algorithm SHA256 -Path .\DlnRenderer-{{VERSION}}-win-x64.msix
```

```
{{SUMS}}
```

`SHA256SUMS-portable.txt` inside the zip covers the 86 files of the portable folder separately.

### License

DlnRenderer is **GPL-2.0-or-later**. libmpv, the mpv library that does the playback, is a GPL
build and is included. The license texts and the third-party notices are inside both packages,
and they name how to obtain the corresponding source.

### Not yet verified

Real control-point interoperability of this build, Windows 10, ARM64, and a CA-signed certificate.
[`docs/testing.md`](https://github.com/tiramission/DlnRenderer/blob/main/docs/testing.md) records
exactly what was and was not tested.
