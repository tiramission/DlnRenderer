<#
.SYNOPSIS
  Reports the version, build configuration and license-relevant facts of a libmpv binary.

.DESCRIPTION
  THIRD-PARTY-NOTICES.md states what libmpv is built from and why it is GPL. Those claims are
  only useful if anyone can re-check them, so this script reads them out of the binary itself:
  mpv version, the meson configuration line (which is where -Dgpl=false would appear), the
  FFmpeg/libplacebo versions, and the SHA-256 that scripts/fetch-libmpv.ps1 pins.

  Bound through NativeLibrary + GetProcAddress rather than DllImport("mpv"): the module is named
  libmpv-2.dll, so a plain DllImport("mpv") would look for mpv.dll and fail. Needs pwsh 7,
  which has System.Runtime.InteropServices.NativeLibrary (Windows PowerShell 5.1 does not).

.PARAMETER Path
  The DLL to inspect. Defaults to src/DlnRenderer.Player/native/x64/libmpv-2.dll.

.EXAMPLE
  pwsh tools/verify-libmpv.ps1
  pwsh tools/verify-libmpv.ps1 -Path dist/DlnRenderer/libmpv-2.dll
#>
[CmdletBinding()]
param([string]$Path)

$ErrorActionPreference = 'Stop'
if (-not $Path) {
    $Path = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\DlnRenderer.Player\native\x64\libmpv-2.dll'
}
$Path = [IO.Path]::GetFullPath($Path)

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class MpvProbe
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr CreateFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFn(IntPtr ctx);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr GetStringFn(IntPtr ctx, IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void FreeFn(IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void DestroyFn(IntPtr ctx);

    public static IntPtr Module;
    static CreateFn _create;
    static InitFn _init;
    static GetStringFn _get;
    static FreeFn _free;
    static DestroyFn _destroy;

    static T Bind<T>(string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(Module, name));

    public static void Load(string path)
    {
        Module = NativeLibrary.Load(path);
        _create = Bind<CreateFn>("mpv_create");
        _init = Bind<InitFn>("mpv_initialize");
        _get = Bind<GetStringFn>("mpv_get_property_string");
        _free = Bind<FreeFn>("mpv_free");
        _destroy = Bind<DestroyFn>("mpv_terminate_destroy");
    }

    public static IntPtr Create() => _create();
    public static int Initialize(IntPtr ctx) => _init(ctx);

    public static string GetProperty(IntPtr ctx, string name)
    {
        IntPtr namePtr = Marshal.StringToCoTaskMemUTF8(name);
        IntPtr valuePtr = IntPtr.Zero;
        try
        {
            valuePtr = _get(ctx, namePtr);
            return valuePtr == IntPtr.Zero ? "<not set>" : Marshal.PtrToStringUTF8(valuePtr);
        }
        finally
        {
            if (valuePtr != IntPtr.Zero) _free(valuePtr);
            Marshal.FreeCoTaskMem(namePtr);
        }
    }

    public static void Destroy(IntPtr ctx) => _destroy(ctx);
}
'@

[MpvProbe]::Load($Path)

$ctx = [MpvProbe]::Create()
if ($ctx -eq [IntPtr]::Zero) { throw 'mpv_create returned null' }
Write-Host "mpv_initialize: $([MpvProbe]::Initialize($ctx))" -ForegroundColor DarkGray

$configuration = ''
foreach ($property in @('mpv-version', 'mpv-configuration', 'ffmpeg-version', 'libass-version', 'libplacebo-version')) {
    $value = [MpvProbe]::GetProperty($ctx, $property)
    if ($property -eq 'mpv-configuration') { $configuration = $value }
    Write-Host "$property = $value"
}

[MpvProbe]::Destroy($ctx)

# The license conclusion in THIRD-PARTY-NOTICES.md leans on this one test, so say it out loud
# instead of leaving the reader to grep the configuration line.
$license = if ($configuration -match '-Dgpl\s*=\s*false') { 'LGPL (built with -Dgpl=false)' } else { 'GPL-2.0-or-later (default mpv build)' }
Write-Host "license: $license"

$info = Get-Item -LiteralPath $Path
Write-Host ""
Write-Host "file: $($info.FullName)"
Write-Host "size: $([math]::Round($info.Length / 1MB, 1)) MiB ($($info.Length) bytes)"
$sha = [System.Security.Cryptography.SHA256]::Create()
$stream = [System.IO.File]::OpenRead($Path)
try { $hash = ([BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant() }
finally { $stream.Dispose(); $sha.Dispose() }
Write-Host "sha256: $hash"
Write-Host "fileVersion: $($info.VersionInfo.FileVersion)  productVersion: $($info.VersionInfo.ProductVersion)"
Write-Host "company: $($info.VersionInfo.CompanyName)  product: $($info.VersionInfo.ProductName)"
