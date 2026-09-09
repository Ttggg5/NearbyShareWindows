namespace NearbyShare.App.Services;

/// <summary>
/// Where the app keeps its state on disk. Kept in one place so the unpackaged
/// and packaged (MSIX) builds differ in exactly one method.
/// </summary>
public sealed class AppPaths
{
    private AppPaths(string dataFolder, string downloadFolder)
    {
        DataFolder = dataFolder;
        DownloadFolder = downloadFolder;
    }

    /// <summary>Holds the settings file, the device certificate and the pinned-peer store.</summary>
    public string DataFolder { get; }

    /// <summary>Default destination for received files.</summary>
    public string DownloadFolder { get; }

    public string SettingsPath => Path.Combine(DataFolder, "settings.json");

    public string KnownPeersPath => Path.Combine(DataFolder, "known-peers.json");

    public string CertificatePath => Path.Combine(DataFolder, "device-certificate.pfx");

    /// <summary>
    /// Builds the default locations: state under <c>%LOCALAPPDATA%\NearbyShare</c>
    /// and received files under the user's Downloads folder.
    /// </summary>
    public static AppPaths CreateDefault()
    {
        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

        string dataFolder = Path.Combine(localAppData, "NearbyShare");

        // There is no SpecialFolder for Downloads, and the shell KNOWNFOLDERID
        // lookup needs interop; the profile-relative path is correct for a default
        // Windows install and the user can override it in Settings.
        string downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "NearbyShare");

        Directory.CreateDirectory(dataFolder);
        Directory.CreateDirectory(downloads);

        return new AppPaths(dataFolder, downloads);
    }
}
