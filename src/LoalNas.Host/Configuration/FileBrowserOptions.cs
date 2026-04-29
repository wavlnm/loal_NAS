namespace LoalNas.Host.Configuration;

public sealed class FileBrowserOptions
{
    public const string SectionName = "LoalNas:FileBrowser";

    public string Address { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 38080;

    public string RelativeExecutablePath { get; set; } = "tools/filebrowser/filebrowser.exe";

    public string RuntimeDirectoryName { get; set; } = "runtime/filebrowser";

    public string RootFolderName { get; set; } = "file";

    public string DatabaseFileName { get; set; } = "filebrowser.db";

    public int StartupTimeoutSeconds { get; set; } = 15;
}