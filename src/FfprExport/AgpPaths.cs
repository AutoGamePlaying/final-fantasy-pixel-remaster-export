using System.Text;

namespace FfprExport;

internal enum AgpRootSource
{
    None,
    AgpHomeEnv,
    WindowsLocalAppData,
    WindowsLocalLowAppData,
    XdgDataHome,
    UnixDefaultLocalShare,
    PortableBaseDirectory,
    UserProfileAgp,
    SavedLocationFile,
    UserSelected,
}

internal sealed record AgpCandidateAttempt(
    AgpRootSource Source,
    string? Path,
    bool Succeeded,
    string? FailureReason);

internal sealed record AgpRootResolution(
    string AgpHome,
    AgpRootSource Source,
    bool UsedFallback,
    string? WarningMessage,
    bool IsWindowsLayout,
    bool Succeeded = true,
    bool ExplicitAgpHomeFailed = false,
    string? FallbackReasonSummary = null,
    IReadOnlyList<AgpCandidateAttempt>? Attempts = null,
    ProcessIntegrityInfo? Integrity = null);

/// <summary>
/// Single source of truth shared by CLI and GUI for selecting a writable AGP data root
/// without ever touching system %TEMP% / TMP, and deriving ffpr-export output, logs, tmp,
/// and settings paths.
///
/// Priority order:
/// 1) AGP_HOME (explicit user override; if invalid or unwritable, fails immediately without fallback)
/// 2) Windows: %LOCALAPPDATA%\agp; Linux/macOS: $XDG_DATA_HOME/agp or ~/.local/share/agp
/// 3) Windows: %USERPROFILE%\AppData\LocalLow\agp (writable by Low-IL processes)
/// 4) &lt;exeDir&gt;\agp-data (portable mode)
/// 5) %USERPROFILE%\agp (Windows) or ~/agp (Linux/macOS)
/// 6) Saved custom directory in &lt;exeDir&gt;\agp-location.txt (if present and writable)
/// </summary>
internal static class AgpPaths
{
    public const string AgpHomeEnvVar = "AGP_HOME";
    public const string XdgDataHomeEnvVar = "XDG_DATA_HOME";
    public const string LocalAppDataEnvVar = "LOCALAPPDATA";
    public const string UserProfileEnvVar = "USERPROFILE";
    public const string HomeEnvVar = "HOME";
    public const string AgpFolderName = "agp";
    public const string PortableAgpFolderName = "agp-data";
    public const string LocationFileName = "agp-location.txt";
    public const string TmpFolderName = "tmp";
    public const string ToolName = "ffpr-export";
    public const string OutputFolderName = "output";
    public const string LogsFolderName = "logs";
    public const string SettingsFileName = "settings.json";

    /// <summary>
    /// Resolves the first writable AGP root directory along the ordered candidate chain.
    /// Every candidate is verified by creating its tmp subdirectory, writing a small probe file,
    /// and deleting the probe file (or via the injected probeWritable delegate in unit tests).
    /// Never touches %TEMP% and never throws.
    /// </summary>
    public static AgpRootResolution ResolveAgpHome(
        bool? isWindowsOverride = null,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<Environment.SpecialFolder, string?>? getSpecialFolder = null,
        Func<string?>? getBaseDirectory = null,
        Func<string?>? getLocalLowDirectory = null,
        Func<string, (bool Writable, string? Error)>? probeWritable = null,
        Func<string, string?>? readLocationFile = null,
        ProcessIntegrityInfo? integrityOverride = null,
        Func<ProcessIntegrityInfo>? detectIntegrity = null)
    {
        var isWindows = isWindowsOverride ?? OperatingSystem.IsWindows();
        var getEnv = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var getFolder = getSpecialFolder ?? (folder => Environment.GetFolderPath(folder));
        var getBase = getBaseDirectory ?? (() => AppContext.BaseDirectory);
        var integrity = integrityOverride
            ?? (detectIntegrity is not null
                ? ProcessIntegrity.Detect(detectIntegrity)
                : isWindows == OperatingSystem.IsWindows()
                    ? ProcessIntegrity.Detect()
                    : ProcessIntegrityInfo.Unknown);

        var effectiveProbe = probeWritable ?? CreateDefaultProbe(isWindows);
        var attempts = new List<AgpCandidateAttempt>();
        var probedKeys = new HashSet<string>(isWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        // 1. AGP_HOME: explicit user override. If set and invalid or unwritable, fail immediately without fallback.
        var envAgpHome = TryInvokeProvider(() => getEnv(AgpHomeEnvVar), out var agpEnvError);
        if (!string.IsNullOrWhiteSpace(agpEnvError))
        {
            var reason = L.Format("Agp_ReadEnvFailed", AgpHomeEnvVar, agpEnvError);
            attempts.Add(new AgpCandidateAttempt(AgpRootSource.AgpHomeEnv, null, false, reason));
            return new AgpRootResolution(
                AgpHome: string.Empty,
                Source: AgpRootSource.None,
                UsedFallback: false,
                WarningMessage: reason,
                IsWindowsLayout: isWindows,
                Succeeded: false,
                ExplicitAgpHomeFailed: true,
                FallbackReasonSummary: null,
                Attempts: attempts,
                Integrity: integrity);
        }

        if (!string.IsNullOrWhiteSpace(envAgpHome))
        {
            if (!TryNormalizePath(envAgpHome, isWindows, out var normalizedAgpHome, out var normalizeError))
            {
                var reason = L.Format("Agp_EnvPathInvalid", AgpHomeEnvVar, envAgpHome, normalizeError);
                attempts.Add(new AgpCandidateAttempt(AgpRootSource.AgpHomeEnv, envAgpHome, false, normalizeError));
                return new AgpRootResolution(
                    AgpHome: string.Empty,
                    Source: AgpRootSource.None,
                    UsedFallback: false,
                    WarningMessage: reason,
                    IsWindowsLayout: isWindows,
                    Succeeded: false,
                    ExplicitAgpHomeFailed: true,
                    FallbackReasonSummary: null,
                    Attempts: attempts,
                    Integrity: integrity);
            }

            var (writable, writeError) = InvokeProbeSafe(effectiveProbe, normalizedAgpHome);
            attempts.Add(new AgpCandidateAttempt(AgpRootSource.AgpHomeEnv, normalizedAgpHome, writable, writeError));
            if (writable)
            {
                return new AgpRootResolution(
                    AgpHome: normalizedAgpHome,
                    Source: AgpRootSource.AgpHomeEnv,
                    UsedFallback: false,
                    WarningMessage: null,
                    IsWindowsLayout: isWindows,
                    Succeeded: true,
                    ExplicitAgpHomeFailed: false,
                    FallbackReasonSummary: null,
                    Attempts: attempts,
                    Integrity: integrity);
            }

            var detail = string.IsNullOrWhiteSpace(writeError) ? L.Get("Agp_DirNotWritable") : writeError;
            var explicitFailMessage = L.Format("Agp_EnvDirNotWritable", AgpHomeEnvVar, normalizedAgpHome, detail);
            return new AgpRootResolution(
                AgpHome: string.Empty,
                Source: AgpRootSource.None,
                UsedFallback: false,
                WarningMessage: explicitFailMessage,
                IsWindowsLayout: isWindows,
                Succeeded: false,
                ExplicitAgpHomeFailed: true,
                FallbackReasonSummary: null,
                Attempts: attempts,
                Integrity: integrity);
        }

        // 2. Primary OS user data root.
        string? resolvedLocalAppDataBase = null;
        if (isWindows)
        {
            var localAppData = TryInvokeProvider(
                () => getFolder(Environment.SpecialFolder.LocalApplicationData),
                out var specialFolderError);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                localAppData = TryInvokeProvider(() => getEnv(LocalAppDataEnvVar), out var envLocalError);
                if (string.IsNullOrWhiteSpace(localAppData))
                {
                    var detail = !string.IsNullOrWhiteSpace(specialFolderError)
                        ? specialFolderError
                        : !string.IsNullOrWhiteSpace(envLocalError)
                            ? envLocalError
                            : L.Get("Agp_NoLocalAppData");
                    attempts.Add(new AgpCandidateAttempt(AgpRootSource.WindowsLocalAppData, null, false, detail));
                }
            }

            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                resolvedLocalAppDataBase = localAppData;
                if (TryCombine(isWindows, localAppData, [AgpFolderName], out var winAgpHome, out var combineError))
                {
                    if (TryCandidate(AgpRootSource.WindowsLocalAppData, winAgpHome, effectiveProbe, probedKeys, attempts))
                    {
                        return new AgpRootResolution(
                            AgpHome: winAgpHome,
                            Source: AgpRootSource.WindowsLocalAppData,
                            UsedFallback: false,
                            WarningMessage: null,
                            IsWindowsLayout: isWindows,
                            Succeeded: true,
                            ExplicitAgpHomeFailed: false,
                            FallbackReasonSummary: null,
                            Attempts: attempts,
                            Integrity: integrity);
                    }
                }
                else
                {
                    attempts.Add(new AgpCandidateAttempt(
                        AgpRootSource.WindowsLocalAppData,
                        localAppData,
                        false,
                        L.Format("Agp_CombineLocalAppDataFailed", AgpFolderName, combineError)));
                }
            }
        }
        else
        {
            var xdgDataHome = TryInvokeProvider(() => getEnv(XdgDataHomeEnvVar), out var xdgError);
            if (!string.IsNullOrWhiteSpace(xdgDataHome))
            {
                if (TryCombine(isWindows, xdgDataHome, [AgpFolderName], out var xdgAgpHome, out var combineError))
                {
                    if (TryCandidate(AgpRootSource.XdgDataHome, xdgAgpHome, effectiveProbe, probedKeys, attempts))
                    {
                        return new AgpRootResolution(
                            AgpHome: xdgAgpHome,
                            Source: AgpRootSource.XdgDataHome,
                            UsedFallback: false,
                            WarningMessage: null,
                            IsWindowsLayout: isWindows,
                            Succeeded: true,
                            ExplicitAgpHomeFailed: false,
                            FallbackReasonSummary: null,
                            Attempts: attempts,
                            Integrity: integrity);
                    }
                }
                else
                {
                    attempts.Add(new AgpCandidateAttempt(
                        AgpRootSource.XdgDataHome,
                        xdgDataHome,
                        false,
                        L.Format("Agp_CombineXdgFailed", XdgDataHomeEnvVar, AgpFolderName, combineError)));
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(xdgError))
                {
                    attempts.Add(new AgpCandidateAttempt(
                        AgpRootSource.XdgDataHome,
                        null,
                        false,
                        L.Format("Agp_ReadEnvFailed", XdgDataHomeEnvVar, xdgError)));
                }

                var homeDir = ResolveUnixHome(getEnv, getFolder, out var homeError);
                if (!string.IsNullOrWhiteSpace(homeDir))
                {
                    if (TryCombine(isWindows, homeDir, [".local", "share", AgpFolderName], out var unixAgpHome, out var combineError))
                    {
                        if (TryCandidate(AgpRootSource.UnixDefaultLocalShare, unixAgpHome, effectiveProbe, probedKeys, attempts))
                        {
                            return new AgpRootResolution(
                                AgpHome: unixAgpHome,
                                Source: AgpRootSource.UnixDefaultLocalShare,
                                UsedFallback: false,
                                WarningMessage: null,
                                IsWindowsLayout: isWindows,
                                Succeeded: true,
                                ExplicitAgpHomeFailed: false,
                                FallbackReasonSummary: null,
                                Attempts: attempts,
                                Integrity: integrity);
                        }
                    }
                    else
                    {
                        attempts.Add(new AgpCandidateAttempt(
                            AgpRootSource.UnixDefaultLocalShare,
                            homeDir,
                            false,
                            L.Format("Agp_CombineLocalShareFailed", AgpFolderName, combineError)));
                    }
                }
                else
                {
                    attempts.Add(new AgpCandidateAttempt(
                        AgpRootSource.UnixDefaultLocalShare,
                        null,
                        false,
                        homeError ?? L.Get("Agp_NoHomeDir")));
                }
            }
        }

        // 3. Windows LocalLow: %USERPROFILE%\AppData\LocalLow\agp (writable by Low-IL processes).
        if (isWindows)
        {
            var localLowBase = ResolveWindowsLocalLowBase(
                isWindows,
                getLocalLowDirectory,
                getEnvironmentVariable,
                getSpecialFolder,
                resolvedLocalAppDataBase,
                out var localLowError);
            if (!string.IsNullOrWhiteSpace(localLowBase))
            {
                if (TryCombine(isWindows, localLowBase, [AgpFolderName], out var localLowAgpHome, out var combineError))
                {
                    if (TryCandidate(AgpRootSource.WindowsLocalLowAppData, localLowAgpHome, effectiveProbe, probedKeys, attempts))
                    {
                        return BuildFallbackSuccess(
                            localLowAgpHome,
                            AgpRootSource.WindowsLocalLowAppData,
                            isWindows,
                            attempts,
                            integrity);
                    }
                }
                else
                {
                    attempts.Add(new AgpCandidateAttempt(
                        AgpRootSource.WindowsLocalLowAppData,
                        localLowBase,
                        false,
                        L.Format("Agp_CombineLocalLowFailed", AgpFolderName, combineError)));
                }
            }
            else if (!string.IsNullOrWhiteSpace(localLowError))
            {
                attempts.Add(new AgpCandidateAttempt(AgpRootSource.WindowsLocalLowAppData, null, false, localLowError));
            }
        }

        // 4. Portable mode beside executable: <exeDir>\agp-data.
        var baseDir = TryInvokeProvider(getBase, out var baseDirError);
        if (!string.IsNullOrWhiteSpace(baseDir))
        {
            if (TryCombine(isWindows, baseDir, [PortableAgpFolderName], out var portableAgpHome, out var combineError))
            {
                if (TryCandidate(AgpRootSource.PortableBaseDirectory, portableAgpHome, effectiveProbe, probedKeys, attempts))
                {
                    return BuildFallbackSuccess(
                        portableAgpHome,
                        AgpRootSource.PortableBaseDirectory,
                        isWindows,
                        attempts,
                        integrity);
                }
            }
            else
            {
                attempts.Add(new AgpCandidateAttempt(
                    AgpRootSource.PortableBaseDirectory,
                    baseDir,
                    false,
                    L.Format("Agp_CombinePortableFailed", PortableAgpFolderName, combineError)));
            }
        }
        else
        {
            attempts.Add(new AgpCandidateAttempt(
                AgpRootSource.PortableBaseDirectory,
                null,
                false,
                baseDirError ?? L.Get("Agp_NoBaseDir")));
        }

        // 5. User profile root: %USERPROFILE%\agp (Windows) or ~/agp (Linux/macOS).
        var userProfileDir = isWindows
            ? ResolveWindowsUserProfile(getEnv, getFolder, out var profileError)
            : ResolveUnixHome(getEnv, getFolder, out profileError);
        if (!string.IsNullOrWhiteSpace(userProfileDir))
        {
            if (TryCombine(isWindows, userProfileDir, [AgpFolderName], out var profileAgpHome, out var combineError))
            {
                if (TryCandidate(AgpRootSource.UserProfileAgp, profileAgpHome, effectiveProbe, probedKeys, attempts))
                {
                    return BuildFallbackSuccess(
                        profileAgpHome,
                        AgpRootSource.UserProfileAgp,
                        isWindows,
                        attempts,
                        integrity);
                }
            }
            else
            {
                attempts.Add(new AgpCandidateAttempt(
                    AgpRootSource.UserProfileAgp,
                    userProfileDir,
                    false,
                    L.Format("Agp_CombineUserProfileFailed", AgpFolderName, combineError)));
            }
        }
        else if (!string.IsNullOrWhiteSpace(profileError))
        {
            attempts.Add(new AgpCandidateAttempt(AgpRootSource.UserProfileAgp, null, false, profileError));
        }

        // 6. Saved custom directory from <exeDir>\agp-location.txt (if present and writable).
        if (!string.IsNullOrWhiteSpace(baseDir)
            && TryCombine(isWindows, baseDir, [LocationFileName], out var locationFilePath, out _))
        {
            var savedRaw = TryReadLocationFile(locationFilePath, isWindows, readLocationFile);
            if (!string.IsNullOrWhiteSpace(savedRaw))
            {
                if (TryNormalizePath(savedRaw, isWindows, out var savedAgpHome, out var savedNormalizeError))
                {
                    if (TryCandidate(AgpRootSource.SavedLocationFile, savedAgpHome, effectiveProbe, probedKeys, attempts))
                    {
                        return BuildFallbackSuccess(
                            savedAgpHome,
                            AgpRootSource.SavedLocationFile,
                            isWindows,
                            attempts,
                            integrity);
                    }
                }
                else
                {
                    attempts.Add(new AgpCandidateAttempt(
                        AgpRootSource.SavedLocationFile,
                        savedRaw,
                        false,
                        L.Format("Agp_LocationFilePathInvalid", LocationFileName, savedNormalizeError)));
                }
            }
        }

        // All automatic candidates failed. Never fall back to %TEMP%.
        var allFailed = new AgpRootResolution(
            AgpHome: string.Empty,
            Source: AgpRootSource.None,
            UsedFallback: true,
            WarningMessage: null,
            IsWindowsLayout: isWindows,
            Succeeded: false,
            ExplicitAgpHomeFailed: false,
            FallbackReasonSummary: isWindows ? L.Get("Agp_AllWinCandidatesFailed") : L.Get("Agp_AllUnixCandidatesFailed"),
            Attempts: attempts,
            Integrity: integrity);
        return allFailed with { WarningMessage = FormatCliAllFailedError(allFailed) };
    }

    public static AgpRootResolution RequireWritableAgpHome(
        bool? isWindowsOverride = null,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<Environment.SpecialFolder, string?>? getSpecialFolder = null,
        Func<string?>? getBaseDirectory = null,
        Func<string?>? getLocalLowDirectory = null,
        Func<string, (bool Writable, string? Error)>? probeWritable = null,
        Func<string, string?>? readLocationFile = null,
        ProcessIntegrityInfo? integrityOverride = null,
        Func<ProcessIntegrityInfo>? detectIntegrity = null)
    {
        var resolution = ResolveAgpHome(
            isWindowsOverride,
            getEnvironmentVariable,
            getSpecialFolder,
            getBaseDirectory,
            getLocalLowDirectory,
            probeWritable,
            readLocationFile,
            integrityOverride,
            detectIntegrity);
        if (!resolution.Succeeded)
        {
            throw new IOException(FormatCliAllFailedError(resolution));
        }

        return resolution;
    }

    public static AgpRootResolution CreateUserSelectedResolution(
        string selectedPath,
        AgpRootResolution previousFailure,
        ProcessIntegrityInfo? integrityOverride = null)
    {
        var isWindows = previousFailure.IsWindowsLayout;
        var normalized = TryNormalizePath(selectedPath, isWindows, out var norm, out _)
            ? norm
            : selectedPath.Trim();
        var integrity = integrityOverride ?? previousFailure.Integrity ?? ProcessIntegrityInfo.Unknown;
        var attempts = new List<AgpCandidateAttempt>(previousFailure.Attempts ?? []);
        attempts.Add(new AgpCandidateAttempt(AgpRootSource.UserSelected, normalized, true, null));

        return BuildFallbackSuccess(normalized, AgpRootSource.UserSelected, isWindows, attempts, integrity);
    }

    public static bool TryProbeWritableDirectory(string candidateRoot, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(candidateRoot))
        {
            error = L.Get("Agp_EmptyDirPath");
            return false;
        }

        string ioRoot;
        string tmpDir;
        string ioTmpDir;
        string probeFile;
        string ioProbeFile;
        try
        {
            ioRoot = PathSupport.ToIoPath(candidateRoot);
            tmpDir = Path.Combine(candidateRoot, TmpFolderName);
            ioTmpDir = PathSupport.ToIoPath(tmpDir);
            probeFile = Path.Combine(tmpDir, $".ffpr-write-probe-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
            ioProbeFile = PathSupport.ToIoPath(probeFile);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        var rootExistedBefore = TryDirectoryExists(ioRoot);
        var tmpExistedBefore = TryDirectoryExists(ioTmpDir);

        try
        {
            Directory.CreateDirectory(ioTmpDir);
            using (var stream = new FileStream(
                ioProbeFile,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                stream.Write("ok\n"u8);
                stream.Flush();
            }

            File.Delete(ioProbeFile);
            if (!tmpExistedBefore)
            {
                TryDeleteEmptyDirectoryQuietly(ioTmpDir);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            TryDeleteFileQuietly(ioProbeFile);
            if (!tmpExistedBefore)
            {
                TryDeleteEmptyDirectoryQuietly(ioTmpDir);
            }

            if (!rootExistedBefore)
            {
                TryDeleteEmptyDirectoryQuietly(ioRoot);
            }

            return false;
        }
    }

    public static bool TrySaveCustomLocation(
        string? baseDirectory,
        string chosenAgpHome,
        out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(baseDirectory) || string.IsNullOrWhiteSpace(chosenAgpHome))
        {
            error = L.Get("Agp_EmptyBaseOrTarget");
            return false;
        }

        try
        {
            var locationPath = Path.Combine(baseDirectory.Trim(), LocationFileName);
            TextFileWriter.WriteAllTextAtomic(locationPath, chosenAgpHome.Trim() + "\n");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Ensures &lt;agpRoot&gt;/tmp exists and redirects process-local TEMP/TMP environment variables
    /// to it on Windows so framework components (such as WinForms visual-styles activation context)
    /// never attempt to touch system %TEMP%.
    /// </summary>
    public static bool TryConfigureProcessTempDirectory(
        AgpRootResolution resolution,
        out string? tempDirectory,
        out string? error)
    {
        tempDirectory = null;
        error = null;
        if (!resolution.Succeeded || string.IsNullOrWhiteSpace(resolution.AgpHome))
        {
            error = L.Get("Agp_RootNotReady");
            return false;
        }

        try
        {
            var tmpDir = GetTempDirectory(resolution);
            Directory.CreateDirectory(PathSupport.ToIoPath(tmpDir));
            if (OperatingSystem.IsWindows())
            {
                Environment.SetEnvironmentVariable("TEMP", tmpDir);
                Environment.SetEnvironmentVariable("TMP", tmpDir);
            }

            tempDirectory = tmpDir;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Builds the prominent localized UI banner message when running at Low integrity level
    /// or when a fallback data directory (LocalLow, portable agp-data, %USERPROFILE%\agp, etc.) is active.
    /// Returns null when running normally at the primary default directory without Low IL.
    /// </summary>
    public static string? FormatUiNoticeBanner(
        AgpRootResolution resolution,
        ProcessIntegrityInfo? integrityOverride = null)
    {
        if (!resolution.Succeeded)
        {
            return resolution.WarningMessage;
        }

        var integrity = integrityOverride ?? resolution.Integrity ?? ProcessIntegrityInfo.Unknown;
        if (integrity.IsLowOrUntrusted)
        {
            if (resolution.Source == AgpRootSource.WindowsLocalLowAppData)
            {
                return L.Format("Agp_BannerLowIlLocalLow", resolution.AgpHome);
            }

            return L.Format("Agp_BannerLowIlOther", resolution.AgpHome);
        }

        if (resolution.UsedFallback)
        {
            var reason = resolution.IsWindowsLayout
                ? L.Get("Agp_ReasonLocalAppDataUnwritable")
                : L.Get("Agp_ReasonDefaultUnwritable");
            return L.Format("Agp_BannerFallback", resolution.AgpHome, reason);
        }

        return null;
    }

    public static string? FormatCliNotice(AgpRootResolution resolution)
    {
        return FormatUiNoticeBanner(resolution, resolution.Integrity);
    }

    public static string FormatCliAllFailedError(AgpRootResolution resolution)
    {
        if (resolution.ExplicitAgpHomeFailed && !string.IsNullOrWhiteSpace(resolution.WarningMessage))
        {
            return resolution.WarningMessage;
        }

        var sb = new StringBuilder();
        var intro = L.Get("Agp_CliAllFailedIntro");
        var details = FormatAttemptSummaries(resolution.Attempts);
        if (!string.IsNullOrWhiteSpace(details))
        {
            sb.Append(L.Format("Agp_CliAllFailedWithDetails", intro, details));
        }
        else
        {
            sb.Append(L.Format("Agp_CliAllFailedNoDetails", intro));
        }

        if (resolution.Integrity?.IsLowOrUntrusted == true)
        {
            sb.Append(L.Get("Agp_CliAllFailedLowIl"));
        }

        sb.Append(L.Format("Agp_CliAllFailedGuidance", AgpHomeEnvVar));
        return sb.ToString();
    }

    public static string FormatGuiAllFailedDialogMessage(
        AgpRootResolution resolution,
        ProcessIntegrityInfo? integrityOverride = null)
    {
        var integrity = integrityOverride ?? resolution.Integrity ?? ProcessIntegrityInfo.Unknown;
        var sb = new StringBuilder();
        sb.AppendLine(L.Get("Agp_GuiAllFailedIntro"));
        sb.AppendLine();

        var details = FormatAttemptSummaries(resolution.Attempts);
        if (!string.IsNullOrWhiteSpace(details))
        {
            sb.AppendLine(L.Format("Agp_GuiAllFailedAttempted", details));
            sb.AppendLine();
        }

        if (integrity.IsLowOrUntrusted)
        {
            sb.AppendLine(L.Get("Agp_GuiAllFailedLowIl1"));
            sb.AppendLine(L.Get("Agp_GuiAllFailedLowIl2"));
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine(L.Get("Agp_GuiAllFailedSecurity"));
            sb.AppendLine();
        }

        sb.AppendLine(L.Get("Agp_GuiAllFailedFolderPrompt"));
        sb.Append(L.Get("Agp_GuiAllFailedLastResort"));
        return sb.ToString();
    }

    public static string GetToolRoot(AgpRootResolution? resolution = null)
    {
        var resolved = EnsureSucceeded(resolution);
        return CombineForLayout(resolved.IsWindowsLayout, resolved.AgpHome, ToolName);
    }

    public static string GetToolRoot(
        bool? isWindowsOverride,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<Environment.SpecialFolder, string?>? getSpecialFolder = null,
        Func<string?>? getBaseDirectory = null,
        Func<string?>? getLocalLowDirectory = null,
        Func<string, (bool Writable, string? Error)>? probeWritable = null)
    {
        return GetToolRoot(RequireWritableAgpHome(
            isWindowsOverride,
            getEnvironmentVariable,
            getSpecialFolder,
            getBaseDirectory,
            getLocalLowDirectory,
            probeWritable));
    }

    public static string GetTempDirectory(AgpRootResolution? resolution = null)
    {
        var resolved = EnsureSucceeded(resolution);
        return CombineForLayout(resolved.IsWindowsLayout, resolved.AgpHome, TmpFolderName);
    }

    public static string GetOutputRoot(AgpRootResolution? resolution = null)
    {
        var resolved = EnsureSucceeded(resolution);
        return CombineForLayout(resolved.IsWindowsLayout, resolved.AgpHome, ToolName, OutputFolderName);
    }

    public static string GetOutputRoot(
        bool? isWindowsOverride,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<Environment.SpecialFolder, string?>? getSpecialFolder = null,
        Func<string?>? getBaseDirectory = null,
        Func<string?>? getLocalLowDirectory = null,
        Func<string, (bool Writable, string? Error)>? probeWritable = null)
    {
        return GetOutputRoot(RequireWritableAgpHome(
            isWindowsOverride,
            getEnvironmentVariable,
            getSpecialFolder,
            getBaseDirectory,
            getLocalLowDirectory,
            probeWritable));
    }

    public static string GetGameOutputDirectory(
        string gameId,
        string? steamBuildId,
        AgpRootResolution? resolution = null)
    {
        var resolved = EnsureSucceeded(resolution);
        return CombineForLayout(
            resolved.IsWindowsLayout,
            resolved.AgpHome,
            ToolName,
            OutputFolderName,
            BuildGameFolderName(gameId, steamBuildId));
    }

    public static string GetLogsDirectory(AgpRootResolution? resolution = null)
    {
        var resolved = EnsureSucceeded(resolution);
        return CombineForLayout(resolved.IsWindowsLayout, resolved.AgpHome, ToolName, LogsFolderName);
    }

    public static string GetSettingsPath(AgpRootResolution? resolution = null)
    {
        var resolved = EnsureSucceeded(resolution);
        return CombineForLayout(resolved.IsWindowsLayout, resolved.AgpHome, ToolName, SettingsFileName);
    }

    public static string BuildGameFolderName(string gameId, string? steamBuildId)
    {
        var normalizedId = string.IsNullOrWhiteSpace(gameId)
            ? "game"
            : gameId.Trim().ToLowerInvariant();
        var buildTag = SanitizeBuildIdForFolder(steamBuildId);
        return $"{normalizedId}-{buildTag}";
    }

    public static string SanitizeBuildIdForFolder(string? steamBuildId)
    {
        if (string.IsNullOrWhiteSpace(steamBuildId))
        {
            return "unknown";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(steamBuildId.Length);
        foreach (var ch in steamBuildId.Trim())
        {
            if (char.IsWhiteSpace(ch)
                || ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'
                || Array.IndexOf(invalidChars, ch) >= 0)
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(ch);
            }
        }

        var sanitized = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(sanitized) ? "unknown" : sanitized;
    }

    private static AgpRootResolution EnsureSucceeded(AgpRootResolution? resolution)
    {
        var resolved = resolution ?? RequireWritableAgpHome();
        if (!resolved.Succeeded || string.IsNullOrWhiteSpace(resolved.AgpHome))
        {
            throw new IOException(FormatCliAllFailedError(resolved));
        }

        return resolved;
    }

    private static AgpRootResolution BuildFallbackSuccess(
        string agpHome,
        AgpRootSource source,
        bool isWindows,
        List<AgpCandidateAttempt> attempts,
        ProcessIntegrityInfo integrity)
    {
        var reasonSummary = isWindows
            ? L.Get("Agp_ReasonLocalAppDataUnwritable")
            : L.Get("Agp_ReasonDefaultUnwritable");
        var draft = new AgpRootResolution(
            AgpHome: agpHome,
            Source: source,
            UsedFallback: true,
            WarningMessage: null,
            IsWindowsLayout: isWindows,
            Succeeded: true,
            ExplicitAgpHomeFailed: false,
            FallbackReasonSummary: reasonSummary,
            Attempts: attempts,
            Integrity: integrity);
        return draft with { WarningMessage = FormatUiNoticeBanner(draft, integrity) };
    }

    private static Func<string, (bool Writable, string? Error)> CreateDefaultProbe(bool isWindows)
    {
        if (isWindows != OperatingSystem.IsWindows())
        {
            return _ => (false, L.Get("Agp_CrossPlatformNoProbe"));
        }

        return path =>
        {
            var ok = TryProbeWritableDirectory(path, out var error);
            return (ok, error);
        };
    }

    private static (bool Writable, string? Error) InvokeProbeSafe(
        Func<string, (bool Writable, string? Error)> probe,
        string path)
    {
        try
        {
            return probe(path);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static bool TryCandidate(
        AgpRootSource source,
        string candidatePath,
        Func<string, (bool Writable, string? Error)> probe,
        HashSet<string> probedKeys,
        List<AgpCandidateAttempt> attempts)
    {
        if (!probedKeys.Add(candidatePath))
        {
            return false;
        }

        var (writable, error) = InvokeProbeSafe(probe, candidatePath);
        attempts.Add(new AgpCandidateAttempt(source, candidatePath, writable, error));
        return writable;
    }

    private static string? ResolveWindowsLocalLowBase(
        bool isWindows,
        Func<string?>? getLocalLowDirectory,
        Func<string, string?>? getEnvironmentVariable,
        Func<Environment.SpecialFolder, string?>? getSpecialFolder,
        string? localAppDataBase,
        out string? error)
    {
        error = null;
        if (getLocalLowDirectory is not null)
        {
            var injected = TryInvokeProvider(getLocalLowDirectory, out var injectedError);
            if (!string.IsNullOrWhiteSpace(injected))
            {
                return injected;
            }

            error = injectedError;
        }
        else if (isWindows
            && OperatingSystem.IsWindows()
            && getEnvironmentVariable is null
            && getSpecialFolder is null)
        {
            var known = ProcessIntegrity.TryGetWindowsLocalLowAppDataPath();
            if (!string.IsNullOrWhiteSpace(known))
            {
                return known;
            }
        }

        var getEnv = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var getFolder = getSpecialFolder ?? (folder => Environment.GetFolderPath(folder));
        var profile = ResolveWindowsUserProfile(getEnv, getFolder, out var profileError);
        if (!string.IsNullOrWhiteSpace(profile)
            && TryCombine(isWindows, profile, ["AppData", "LocalLow"], out var fromProfile, out _))
        {
            return fromProfile;
        }

        if (!string.IsNullOrWhiteSpace(localAppDataBase))
        {
            var trimmed = TrimTrailingSeparatorsForLayout(localAppDataBase.Trim(), isWindows);
            if (trimmed.EndsWith("\\Local", StringComparison.OrdinalIgnoreCase)
                || trimmed.EndsWith("/Local", StringComparison.OrdinalIgnoreCase))
            {
                var parent = trimmed[..^6];
                if (TryCombine(isWindows, parent, ["LocalLow"], out var siblingLocalLow, out _))
                {
                    return siblingLocalLow;
                }
            }
        }

        error ??= profileError ?? L.Get("Agp_NoLocalLowDir");
        return null;
    }

    private static string? ResolveWindowsUserProfile(
        Func<string, string?> getEnv,
        Func<Environment.SpecialFolder, string?> getFolder,
        out string? error)
    {
        error = null;
        var profile = TryInvokeProvider(() => getFolder(Environment.SpecialFolder.UserProfile), out var folderError);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            return profile;
        }

        profile = TryInvokeProvider(() => getEnv(UserProfileEnvVar), out var envError);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            return profile;
        }

        error = !string.IsNullOrWhiteSpace(folderError)
            ? folderError
            : !string.IsNullOrWhiteSpace(envError)
                ? envError
                : L.Get("Agp_NoUserProfile");
        return null;
    }

    private static string? ResolveUnixHome(
        Func<string, string?> getEnv,
        Func<Environment.SpecialFolder, string?> getFolder,
        out string? error)
    {
        error = null;
        var home = TryInvokeProvider(() => getEnv(HomeEnvVar), out var homeEnvError);
        if (!string.IsNullOrWhiteSpace(home))
        {
            return home;
        }

        home = TryInvokeProvider(() => getFolder(Environment.SpecialFolder.UserProfile), out var profileError);
        if (!string.IsNullOrWhiteSpace(home))
        {
            return home;
        }

        error = !string.IsNullOrWhiteSpace(profileError)
            ? profileError
            : !string.IsNullOrWhiteSpace(homeEnvError)
                ? homeEnvError
                : L.Get("Agp_NoHomeDir");
        return null;
    }

    private static string? TryReadLocationFile(
        string locationFilePath,
        bool isWindows,
        Func<string, string?>? readLocationFile)
    {
        try
        {
            string? content;
            if (readLocationFile is not null)
            {
                content = readLocationFile(locationFilePath);
            }
            else
            {
                if (isWindows != OperatingSystem.IsWindows())
                {
                    return null;
                }

                var ioPath = PathSupport.ToIoPath(locationFilePath);
                if (!File.Exists(ioPath))
                {
                    return null;
                }

                content = File.ReadAllText(ioPath, Encoding.UTF8);
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            foreach (var rawLine in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = rawLine.Trim();
                if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith('#'))
                {
                    return trimmed;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatAttemptSummaries(IReadOnlyList<AgpCandidateAttempt>? attempts)
    {
        if (attempts is null || attempts.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>(attempts.Count);
        foreach (var attempt in attempts)
        {
            if (attempt.Succeeded)
            {
                continue;
            }

            var label = string.IsNullOrWhiteSpace(attempt.Path)
                ? attempt.Source.ToString()
                : attempt.Path;
            var reason = string.IsNullOrWhiteSpace(attempt.FailureReason)
                ? L.Get("Agp_UnwritableShort")
                : attempt.FailureReason;
            parts.Add($"{label}: {reason}");
        }

        return string.Join(L.Get("Agp_ListSeparator"), parts);
    }

    private static bool TryDirectoryExists(string ioPath)
    {
        try
        {
            return Directory.Exists(ioPath);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteFileQuietly(string ioPath)
    {
        try
        {
            if (File.Exists(ioPath))
            {
                File.Delete(ioPath);
            }
        }
        catch
        {
            // Ignore cleanup failure after a failed write probe.
        }
    }

    private static void TryDeleteEmptyDirectoryQuietly(string ioPath)
    {
        try
        {
            if (Directory.Exists(ioPath) && !Directory.EnumerateFileSystemEntries(ioPath).Any())
            {
                Directory.Delete(ioPath, recursive: false);
            }
        }
        catch
        {
            // Ignore cleanup failure for probe directories.
        }
    }

    private static string? TryInvokeProvider(Func<string?> provider, out string? error)
    {
        error = null;
        try
        {
            var value = provider();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static bool TryNormalizePath(
        string rawPath,
        bool isWindows,
        out string normalized,
        out string? error)
    {
        normalized = string.Empty;
        error = null;
        try
        {
            var trimmed = rawPath.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.IndexOf('\0') >= 0)
            {
                error = L.Get("Agp_PathNullOrEmpty");
                return false;
            }

            if (isWindows == OperatingSystem.IsWindows())
            {
                normalized = PathSupport.TrimTrailingSeparators(Path.GetFullPath(trimmed));
            }
            else
            {
                normalized = TrimTrailingSeparatorsForLayout(trimmed, isWindows);
            }

            return !string.IsNullOrWhiteSpace(normalized);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryCombine(
        bool isWindows,
        string basePath,
        string[] segments,
        out string combined,
        out string? error)
    {
        combined = string.Empty;
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(basePath) || basePath.IndexOf('\0') >= 0)
            {
                error = L.Get("Agp_BasePathNullOrEmpty");
                return false;
            }

            combined = CombineForLayout(isWindows, basePath, segments);
            return !string.IsNullOrWhiteSpace(combined);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string CombineForLayout(bool isWindows, string basePath, params string[] segments)
    {
        if (isWindows == OperatingSystem.IsWindows())
        {
            var current = basePath.Trim();
            foreach (var segment in segments)
            {
                current = Path.Combine(current, segment);
            }

            return PathSupport.TrimTrailingSeparators(Path.GetFullPath(current));
        }

        var separator = isWindows ? '\\' : '/';
        var sb = new StringBuilder(TrimTrailingSeparatorsForLayout(basePath.Trim(), isWindows));
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            if (sb.Length > 0 && sb[^1] != '/' && sb[^1] != '\\')
            {
                sb.Append(separator);
            }

            sb.Append(segment.Trim('/', '\\'));
        }

        return TrimTrailingSeparatorsForLayout(sb.ToString(), isWindows);
    }

    private static string TrimTrailingSeparatorsForLayout(string path, bool isWindows)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        if (isWindows)
        {
            var trimmed = path.TrimEnd('\\', '/');
            if (trimmed.Length == 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':')
            {
                return trimmed + "\\";
            }

            return string.IsNullOrEmpty(trimmed) ? "\\" : trimmed;
        }

        var unixTrimmed = path.TrimEnd('/', '\\');
        return string.IsNullOrEmpty(unixTrimmed) ? "/" : unixTrimmed;
    }
}
