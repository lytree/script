


public static class PathEntryPointExtensions
{
    extension(Path)
    {
        public static string EntryPointDataPath() => Path.Combine(EntryPointFileDirectoryPath(), "data");
        public static string EntryPointTempPath() => Path.Combine(EntryPointFileDirectoryPath(), "temp");
        public static string EntryPointModelsPath() => Path.Combine(EntryPointFileDirectoryPath(), "models");
        public static string EntryPointLibPath() => Path.Combine(EntryPointFileDirectoryPath(), "lib");
        public static string EntryPointFilePath() => EntryPointImpl();

        public static string EntryPointFileDirectoryPath() => Path.GetDirectoryName(EntryPointImpl()) ?? "";

        private static string EntryPointImpl([System.Runtime.CompilerServices.CallerFilePath] string filePath = "") => filePath;
    }
}

public static class AppContextExtensions
{
    extension(AppContext)
    {
        public static string EntryPointDataPath() => Path.Combine(EntryPointFileDirectoryPath(), "data");
        public static string EntryPointTempPath() => Path.Combine(EntryPointFileDirectoryPath(), "temp");
        public static string EntryPointModelsPath() => Path.Combine(EntryPointFileDirectoryPath(), "models");
        public static string EntryPointLibPath() => Path.Combine(EntryPointFileDirectoryPath(), "lib");
        public static string? EntryPointFilePath() => AppContext.GetData("EntryPointFilePath") as string;
        public static string? EntryPointFileDirectoryPath() => AppContext.GetData("EntryPointFileDirectoryPath") as string;
    }
}