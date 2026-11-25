namespace FfprExport.Tests;

internal static class SyntheticDbFixture
{
    public static string CreateSyntheticExport(
        string exportRoot,
        bool useFf3AbilityLayout = false,
        bool includeDanglingReferences = true,
        int extraRowsPerTypedTable = 0)
    {
        return FfprExport.Db.SyntheticDbFixture.CreateSyntheticExport(
            exportRoot,
            useFf3AbilityLayout,
            includeDanglingReferences,
            extraRowsPerTypedTable);
    }
}
