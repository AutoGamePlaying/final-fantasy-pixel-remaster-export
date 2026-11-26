using System.Globalization;
using System.Runtime.InteropServices;

namespace FfprExport;

internal enum ProcessIntegrityKind
{
    Unknown,
    Untrusted,
    Low,
    Medium,
    High,
    System,
}

internal sealed record ProcessIntegrityInfo(
    ProcessIntegrityKind Kind,
    uint? RawRid,
    string? ErrorMessage = null)
{
    public static readonly ProcessIntegrityInfo Unknown = new(ProcessIntegrityKind.Unknown, null, null);
    public static readonly ProcessIntegrityInfo Low = new(ProcessIntegrityKind.Low, ProcessIntegrity.SecurityMandatoryLowRid, null);
    public static readonly ProcessIntegrityInfo Medium = new(ProcessIntegrityKind.Medium, ProcessIntegrity.SecurityMandatoryMediumRid, null);
    public static readonly ProcessIntegrityInfo High = new(ProcessIntegrityKind.High, ProcessIntegrity.SecurityMandatoryHighRid, null);

    public bool IsLowOrUntrusted => Kind is ProcessIntegrityKind.Low or ProcessIntegrityKind.Untrusted;

    public string LogLabel
    {
        get
        {
            var baseName = Kind.ToString();
            if (RawRid is { } rid)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{baseName} (0x{rid:X4})");
            }

            if (!string.IsNullOrWhiteSpace(ErrorMessage))
            {
                return $"{baseName} ({ErrorMessage})";
            }

            return baseName;
        }
    }

    public string DisplayLabel => Kind switch
    {
        ProcessIntegrityKind.Untrusted => L.Get("Integrity_Untrusted"),
        ProcessIntegrityKind.Low => L.Get("Integrity_Low"),
        ProcessIntegrityKind.Medium => L.Get("Integrity_Medium"),
        ProcessIntegrityKind.High => L.Get("Integrity_High"),
        ProcessIntegrityKind.System => L.Get("Integrity_System"),
        _ => L.Get("Integrity_Unknown"),
    };
}

/// <summary>
/// Detects the current Windows process mandatory integrity level via
/// GetTokenInformation(TokenIntegrityLevel) and resolves FOLDERID_LocalAppDataLow.
/// Safe to call on Linux/macOS (returns ProcessIntegrityKind.Unknown) and injectable for tests.
/// </summary>
internal static class ProcessIntegrity
{
    public const uint SecurityMandatoryUntrustedRid = 0x0000;
    public const uint SecurityMandatoryLowRid = 0x1000;
    public const uint SecurityMandatoryMediumRid = 0x2000;
    public const uint SecurityMandatoryHighRid = 0x3000;
    public const uint SecurityMandatorySystemRid = 0x4000;

    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevelClass = 25;
    private const int ErrorInsufficientBuffer = 122;

    // FOLDERID_LocalAppDataLow = {A520A1A4-1780-4FF6-BD18-167343C5AF16}
    private static readonly Guid FolderIdLocalAppDataLow = new("A520A1A4-1780-4FF6-BD18-167343C5AF16");

    public static ProcessIntegrityInfo Detect(Func<ProcessIntegrityInfo>? detectorOverride = null)
    {
        if (detectorOverride is not null)
        {
            try
            {
                return detectorOverride() ?? ProcessIntegrityInfo.Unknown;
            }
            catch (Exception ex)
            {
                return new ProcessIntegrityInfo(ProcessIntegrityKind.Unknown, null, ex.Message);
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            return new ProcessIntegrityInfo(ProcessIntegrityKind.Unknown, null, L.Get("Integrity_NonWindows"));
        }

        return DetectWindowsIntegrityLevel();
    }

    public static ProcessIntegrityKind ClassifyRid(uint rid)
    {
        if (rid < SecurityMandatoryLowRid)
        {
            return ProcessIntegrityKind.Untrusted;
        }

        if (rid < SecurityMandatoryMediumRid)
        {
            return ProcessIntegrityKind.Low;
        }

        if (rid < SecurityMandatoryHighRid)
        {
            return ProcessIntegrityKind.Medium;
        }

        if (rid < SecurityMandatorySystemRid)
        {
            return ProcessIntegrityKind.High;
        }

        return ProcessIntegrityKind.System;
    }

    public static ProcessIntegrityInfo FromRid(uint rid)
    {
        return new ProcessIntegrityInfo(ClassifyRid(rid), rid, null);
    }

    internal static string? TryGetWindowsLocalLowAppDataPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var hr = SHGetKnownFolderPath(FolderIdLocalAppDataLow, 0, IntPtr.Zero, out var pszPath);
            if (hr == 0 && pszPath != IntPtr.Zero)
            {
                try
                {
                    var path = Marshal.PtrToStringUni(pszPath);
                    return string.IsNullOrWhiteSpace(path) ? null : path.Trim();
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pszPath);
                }
            }
        }
        catch
        {
            // Fall through to %USERPROFILE%\AppData\LocalLow derivation.
        }

        return null;
    }

    private static ProcessIntegrityInfo DetectWindowsIntegrityLevel()
    {
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var tokenHandle))
            {
                var err = Marshal.GetLastWin32Error();
                return new ProcessIntegrityInfo(
                    ProcessIntegrityKind.Unknown,
                    null,
                    L.Format("Integrity_OpenTokenFailed", err));
            }

            try
            {
                var firstOk = GetTokenInformation(
                    tokenHandle,
                    TokenIntegrityLevelClass,
                    IntPtr.Zero,
                    0,
                    out var requiredLength);
                var firstErr = Marshal.GetLastWin32Error();
                if (!firstOk && firstErr != ErrorInsufficientBuffer)
                {
                    return new ProcessIntegrityInfo(
                        ProcessIntegrityKind.Unknown,
                        null,
                        L.Format("Integrity_QueryLenFailed", firstErr));
                }

                if (requiredLength == 0 || requiredLength > 64 * 1024)
                {
                    return new ProcessIntegrityInfo(
                        ProcessIntegrityKind.Unknown,
                        null,
                        L.Format("Integrity_BadBufferLen", requiredLength));
                }

                var buffer = Marshal.AllocHGlobal(checked((int)requiredLength));
                try
                {
                    if (!GetTokenInformation(
                        tokenHandle,
                        TokenIntegrityLevelClass,
                        buffer,
                        requiredLength,
                        out _))
                    {
                        var err = Marshal.GetLastWin32Error();
                        return new ProcessIntegrityInfo(
                            ProcessIntegrityKind.Unknown,
                            null,
                            L.Format("Integrity_ReadTokenFailed", err));
                    }

                    var label = Marshal.PtrToStructure<SidAndAttributes>(buffer);
                    if (label.Sid == IntPtr.Zero)
                    {
                        return new ProcessIntegrityInfo(
                            ProcessIntegrityKind.Unknown,
                            null,
                            L.Get("Integrity_NullSid"));
                    }

                    var countPtr = GetSidSubAuthorityCount(label.Sid);
                    if (countPtr == IntPtr.Zero)
                    {
                        return new ProcessIntegrityInfo(
                            ProcessIntegrityKind.Unknown,
                            null,
                            L.Get("Integrity_NullSubAuthCount"));
                    }

                    var subAuthorityCount = Marshal.ReadByte(countPtr);
                    if (subAuthorityCount == 0)
                    {
                        return new ProcessIntegrityInfo(
                            ProcessIntegrityKind.Unknown,
                            null,
                            L.Get("Integrity_ZeroSubAuth"));
                    }

                    var ridPtr = GetSidSubAuthority(label.Sid, (uint)(subAuthorityCount - 1));
                    if (ridPtr == IntPtr.Zero)
                    {
                        return new ProcessIntegrityInfo(
                            ProcessIntegrityKind.Unknown,
                            null,
                            L.Get("Integrity_NullSubAuth"));
                    }

                    var rid = unchecked((uint)Marshal.ReadInt32(ridPtr));
                    return FromRid(rid);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                _ = CloseHandle(tokenHandle);
            }
        }
        catch (Exception ex)
        {
            return new ProcessIntegrityInfo(ProcessIntegrityKind.Unknown, null, ex.Message);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr pSid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthority(IntPtr pSid, uint nSubAuthority);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);
}
