namespace C_toolsSetup;

internal enum AcadBundleReleaseBand
{
    R24NetFx = 0
}

internal static class AcadReleaseTargeting
{
    internal static bool TryParseReleaseMajor(string? versionKey, out int major)
    {
        major = 0;
        if (string.IsNullOrEmpty(versionKey) || versionKey[0] != 'R')
            return false;

        var len = 0;
        var s = versionKey.AsSpan(1);
        while (len < s.Length && char.IsDigit(s[len]))
            len++;
        if (len == 0)
            return false;

        return int.TryParse(s[..len], out major);
    }

    internal static bool IsBandApplicableToRelease(AcadBundleReleaseBand band, int acadReleaseMajor)
    {
        return band == AcadBundleReleaseBand.R24NetFx && acadReleaseMajor == 24;
    }

    internal static AcadBundleReleaseBand ClassifyBundle(string bundleDirectoryName, string? primaryDllFileName)
    {
        return AcadBundleReleaseBand.R24NetFx;
    }
}
