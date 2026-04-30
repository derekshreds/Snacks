using System.Runtime.InteropServices;
using Xunit;

namespace Snacks.Tests.Fixtures;

/// <summary>
///     <see cref="FactAttribute"/> that auto-skips when the test host isn't Linux.
///     Used for VAAPI scenarios — VAAPI is a Linux-only stack, and on Windows
///     <c>GetInitFlags</c> / <c>GetEncoder</c> deliberately return QSV / AMF
///     flags instead, so a test asserting on the VAAPI variants would fail on
///     a Windows dev box despite the production code being correct for both.
/// </summary>
public sealed class LinuxOnlyFactAttribute : FactAttribute
{
    public LinuxOnlyFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            Skip = "VAAPI tests run on Linux only";
    }
}
