namespace C_toolsSetup;

internal readonly record struct SetupProgressUpdate(int Percent, string Status);

internal readonly record struct DirectoryCopyProgress(
    string SourcePath,
    string DestinationPath,
    long FileSizeBytes);
