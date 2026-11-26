using System.Globalization;
using System.Resources;

namespace FfprExport;

internal enum AppLanguage
{
    En,
    ZhHans,
    Ja,
}

internal sealed record ExtractedLanguageArgs(
    string? RawLanguage,
    string[] RemainingArgs,
    bool MissingValue);

/// <summary>
/// Centralized localization and language selection shared by both the CLI and WinForms GUI.
/// Precedence: --lang CLI option -> AGP_LANG environment variable -> GUI settings.json -> "en".
/// Never follows the OS/system culture automatically.
/// </summary>
internal static class AppLocalization
{
    public const string EnvVarName = "AGP_LANG";
    public const string CliOptionName = "--lang";
    public const string CodeEn = "en";
    public const string CodeZhHans = "zh-Hans";
    public const string CodeJa = "ja";

    public static readonly IReadOnlyList<AppLanguage> SupportedLanguages =
    [
        AppLanguage.En,
        AppLanguage.ZhHans,
        AppLanguage.Ja,
    ];

    private static readonly CultureInfo CultureEn = CultureInfo.GetCultureInfo(CodeEn);
    private static readonly CultureInfo CultureZhHans = CultureInfo.GetCultureInfo(CodeZhHans);
    private static readonly CultureInfo CultureJa = CultureInfo.GetCultureInfo(CodeJa);

    private static readonly HashSet<string> ValueTakingCliOptions = new(StringComparer.Ordinal)
    {
        "--game",
        "--out",
        "--steam-buildid",
        "--jobs",
        "--export",
        "--db",
    };

    private static readonly ResourceManager ResourceManager = new(
        "FfprExport.Resources.Strings",
        typeof(AppLocalization).Assembly);

    private static readonly AsyncLocal<AppLanguage?> ScopedLanguage = new();
    private static AppLanguage s_processLanguage = AppLanguage.En;

    public static AppLanguage CurrentLanguage => ScopedLanguage.Value ?? s_processLanguage;

    public static string CurrentLanguageCode => ToCode(CurrentLanguage);

    public static CultureInfo CurrentCulture => ToCulture(CurrentLanguage);

    public static void SetProcessLanguage(AppLanguage language)
    {
        s_processLanguage = language;
    }

    public static IDisposable PushLanguage(AppLanguage language)
    {
        var previous = ScopedLanguage.Value;
        ScopedLanguage.Value = language;
        return new ScopeGuard(previous);
    }

    public static string ToCode(AppLanguage language) => language switch
    {
        AppLanguage.ZhHans => CodeZhHans,
        AppLanguage.Ja => CodeJa,
        _ => CodeEn,
    };

    public static CultureInfo ToCulture(AppLanguage language) => language switch
    {
        AppLanguage.ZhHans => CultureZhHans,
        AppLanguage.Ja => CultureJa,
        _ => CultureEn,
    };

    public static string GetNativeDisplayName(AppLanguage language) => language switch
    {
        AppLanguage.ZhHans => "简体中文",
        AppLanguage.Ja => "日本語",
        _ => "English",
    };

    public static bool TryParseLanguage(string? raw, out AppLanguage language)
    {
        language = AppLanguage.En;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();
        if (string.Equals(trimmed, CodeEn, StringComparison.OrdinalIgnoreCase))
        {
            language = AppLanguage.En;
            return true;
        }

        if (string.Equals(trimmed, CodeZhHans, StringComparison.OrdinalIgnoreCase))
        {
            language = AppLanguage.ZhHans;
            return true;
        }

        if (string.Equals(trimmed, CodeJa, StringComparison.OrdinalIgnoreCase))
        {
            language = AppLanguage.Ja;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves the active language in strict priority order:
    /// 1) CLI --lang
    /// 2) AGP_LANG environment variable
    /// 3) Saved GUI setting
    /// 4) Default English ("en")
    /// </summary>
    public static AppLanguage ResolveLanguage(
        string? cliLanguage = null,
        string? envLanguage = null,
        string? savedSettingLanguage = null)
    {
        if (TryParseLanguage(cliLanguage, out var fromCli))
        {
            return fromCli;
        }

        if (TryParseLanguage(envLanguage, out var fromEnv))
        {
            return fromEnv;
        }

        if (TryParseLanguage(savedSettingLanguage, out var fromSettings))
        {
            return fromSettings;
        }

        return AppLanguage.En;
    }

    public static AppLanguage ResolveLanguageFromEnvironment(
        string? cliLanguage = null,
        string? savedSettingLanguage = null,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        var getEnv = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        string? envValue = null;
        try
        {
            envValue = getEnv(EnvVarName);
        }
        catch
        {
            envValue = null;
        }

        return ResolveLanguage(cliLanguage, envValue, savedSettingLanguage);
    }

    public static ExtractedLanguageArgs ExtractCliLanguageArgument(string[] args)
    {
        string? rawLanguage = null;
        var missingValue = false;
        var remaining = new List<string>(args.Length);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (ValueTakingCliOptions.Contains(arg))
            {
                remaining.Add(arg);
                if (i + 1 < args.Length)
                {
                    remaining.Add(args[++i]);
                }

                continue;
            }

            if (string.Equals(arg, CliOptionName, StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length)
                {
                    missingValue = true;
                    break;
                }

                rawLanguage = args[++i];
                continue;
            }

            if (arg.StartsWith(CliOptionName + "=", StringComparison.Ordinal))
            {
                var value = arg[(CliOptionName.Length + 1)..];
                if (string.IsNullOrWhiteSpace(value))
                {
                    missingValue = true;
                }
                else
                {
                    rawLanguage = value;
                }

                continue;
            }

            remaining.Add(arg);
        }

        return new ExtractedLanguageArgs(rawLanguage, [.. remaining], missingValue);
    }

    public static string[] ApplyCliLanguageOrThrow(
        string[] args,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        var extracted = ExtractCliLanguageArgument(args);

        // Apply environment/default language first so any --lang argument error is localized properly.
        var fallbackLang = ResolveLanguageFromEnvironment(
            cliLanguage: null,
            savedSettingLanguage: null,
            getEnvironmentVariable: getEnvironmentVariable);
        SetProcessLanguage(fallbackLang);

        if (extracted.MissingValue)
        {
            throw new ArgumentException(L.Format("Cli_OptionRequiresValue", CliOptionName));
        }

        if (extracted.RawLanguage is not null)
        {
            if (!TryParseLanguage(extracted.RawLanguage, out var parsed))
            {
                throw new ArgumentException(L.Format("Lang_InvalidValue", extracted.RawLanguage));
            }

            SetProcessLanguage(parsed);
        }

        return extracted.RemainingArgs;
    }

    public static string Get(string key, AppLanguage? languageOverride = null)
    {
        var lang = languageOverride ?? CurrentLanguage;
        var culture = ToCulture(lang);
        var value = ResourceManager.GetString(key, culture);
        if (value is null)
        {
            throw new MissingManifestResourceException(
                $"Missing string resource key '{key}' for culture '{culture.Name}'.");
        }

        return value;
    }

    public static string Format(string key, params object?[] args)
    {
        return Format(languageOverride: null, key, args);
    }

    public static string Format(AppLanguage? languageOverride, string key, params object?[] args)
    {
        var template = Get(key, languageOverride);
        return string.Format(CultureInfo.InvariantCulture, template, args);
    }

    private sealed class ScopeGuard(AppLanguage? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ScopedLanguage.Value = previous;
        }
    }
}

/// <summary>
/// Short alias for looking up localized strings from Resources/Strings*.resx.
/// </summary>
internal static class L
{
    public static string Get(string key) => AppLocalization.Get(key);

    public static string Get(string key, AppLanguage language) => AppLocalization.Get(key, language);

    public static string Format(string key, params object?[] args) => AppLocalization.Format(key, args);
}
