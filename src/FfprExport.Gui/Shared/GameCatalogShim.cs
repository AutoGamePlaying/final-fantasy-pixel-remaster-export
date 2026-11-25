namespace FfprExport;

/// <summary>
/// Minimal FF1-FF6 game catalog shim allowing the GUI project to compile the shared
/// SteamLocator without pulling AssetsTools.NET or SQLite into the GUI executable.
/// </summary>
internal sealed record GameSpec(
    string Id,
    string Title,
    int AppId,
    string ExeName,
    string DataDirName);

internal static class GameDetector
{
    internal static readonly IReadOnlyList<GameSpec> KnownGames =
    [
        new("ff1", "FINAL FANTASY", 1173770, "FINAL FANTASY.exe", "FINAL FANTASY_Data"),
        new("ff2", "FINAL FANTASY II", 1173780, "FINAL FANTASY II.exe", "FINAL FANTASY II_Data"),
        new("ff3", "FINAL FANTASY III", 1173790, "FINAL FANTASY III.exe", "FINAL FANTASY III_Data"),
        new("ff4", "FINAL FANTASY IV", 1173800, "FINAL FANTASY IV.exe", "FINAL FANTASY IV_Data"),
        new("ff5", "FINAL FANTASY V", 1173810, "FINAL FANTASY V.exe", "FINAL FANTASY V_Data"),
        new("ff6", "FINAL FANTASY VI", 1173820, "FINAL FANTASY VI.exe", "FINAL FANTASY VI_Data"),
    ];
}
