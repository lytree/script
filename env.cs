


public static class PathEntryPointExtensions
{
    extension(Path)
    {
        public static string EntryPointDataPath() => Path.Combine(EntryPointFileDirectoryPath(), "data");
        public static string EntryPointTempPath() => Path.Combine(EntryPointFileDirectoryPath(), "temp");
        public static string EntryPointModelsPath() => Path.Combine(EntryPointFileDirectoryPath(), "models");
        public static string EntryPointLibPath() => Path.Combine(EntryPointFileDirectoryPath(), "lib");
        public static string EntryPointLogsPath() => Path.Combine(EntryPointFileDirectoryPath(), "logs");
        public static string EntryPointCachePath() => Path.Combine(EntryPointFileDirectoryPath(), "cache");
        public static string EntryPointConfigPath() => Path.Combine(EntryPointFileDirectoryPath(), "config");
        public static string EntryPointOutputPath() => Path.Combine(EntryPointFileDirectoryPath(), "output");
        public static string EntryPointDownloadsPath() => Path.Combine(EntryPointFileDirectoryPath(), "downloads");
        public static string EntryPointBackupPath() => Path.Combine(EntryPointFileDirectoryPath(), "backup");
        public static string EntryPointAssetsPath() => Path.Combine(EntryPointFileDirectoryPath(), "assets");
        public static string EntryPointResourcesPath() => Path.Combine(EntryPointFileDirectoryPath(), "resources");
        public static string EntryPointBinPath() => Path.Combine(EntryPointFileDirectoryPath(), "bin");
        public static string EntryPointFilePath() => EntryPointImpl();

        public static string EntryPointFileDirectoryPath() => Path.GetDirectoryName(EntryPointImpl()) ?? "";

        private static string EntryPointImpl([System.Runtime.CompilerServices.CallerFilePath] string filePath = "") => filePath;
    }
}

public static class AppContextExtensions
{
    extension(AppContext)
    {
        public static string EntryPointDataPath() => Path.Combine(EntryPointFileDirectoryPath()!, "data");
        public static string EntryPointTempPath() => Path.Combine(EntryPointFileDirectoryPath()!, "temp");
        public static string EntryPointModelsPath() => Path.Combine(EntryPointFileDirectoryPath()!, "models");
        public static string EntryPointLibPath() => Path.Combine(EntryPointFileDirectoryPath()!, "lib");
        public static string EntryPointLogsPath() => Path.Combine(EntryPointFileDirectoryPath()!, "logs");
        public static string EntryPointCachePath() => Path.Combine(EntryPointFileDirectoryPath()!, "cache");
        public static string EntryPointConfigPath() => Path.Combine(EntryPointFileDirectoryPath()!, "config");
        public static string EntryPointOutputPath() => Path.Combine(EntryPointFileDirectoryPath()!, "output");
        public static string EntryPointDownloadsPath() => Path.Combine(EntryPointFileDirectoryPath()!, "downloads");
        public static string EntryPointBackupPath() => Path.Combine(EntryPointFileDirectoryPath()!, "backup");
        public static string EntryPointAssetsPath() => Path.Combine(EntryPointFileDirectoryPath()!, "assets");
        public static string EntryPointResourcesPath() => Path.Combine(EntryPointFileDirectoryPath()!, "resources");
        public static string EntryPointBinPath() => Path.Combine(EntryPointFileDirectoryPath()!, "bin");
        public static string? EntryPointFilePath() => AppContext.GetData("EntryPointFilePath") as string;
        public static string? EntryPointFileDirectoryPath() => AppContext.GetData("EntryPointFileDirectoryPath") as string;
    }
}