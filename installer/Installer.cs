using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace D2RInstaller;

public enum InstallStep
{
    DownloadingMod,
    ExtractingFiles,
    CopyingFiles,
    Done
}

public readonly record struct InstallProgress(InstallStep Step, string Message, int Percent);

public readonly record struct PathResolution(bool Succeeded, string? GamePath, string? ErrorMessage)
{
    public static PathResolution Success(string gamePath) => new(true, gamePath, null);

    public static PathResolution Failure(string errorMessage) => new(false, null, errorMessage);
}

public readonly record struct InstallResult(bool Succeeded, string? InstalledPath, string? InstalledTag, string? ErrorMessage)
{
    public static InstallResult Success(string installedPath, string installedTag) => new(true, installedPath, installedTag, null);

    public static InstallResult Failure(string errorMessage) => new(false, null, null, errorMessage);
}

public readonly record struct UninstallResult(bool Succeeded, string? RemovedPath, string? ErrorMessage)
{
    public static UninstallResult Success(string removedPath) => new(true, removedPath, null);

    public static UninstallResult Failure(string errorMessage) => new(false, null, errorMessage);
}

public enum InstalledModState
{
    NotInstalled,
    InstalledUnknownVersion,
    InstalledOutdated,
    InstalledCurrent
}

public readonly record struct InstalledModStatus(
    InstalledModState State,
    string? InstalledTag,
    string? LatestTag,
    string? ErrorMessage)
{
    public bool IsInstalled => State is not InstalledModState.NotInstalled;
}

public readonly record struct GitHubReleaseInfo(string TagName, string DownloadUrl);

[SupportedOSPlatform("windows")]
public static class Installer
{
    public const string GitHubOwner = "ChrisTitusTech";
    public const string GitHubRepository = "d2r-loot-filter";
    public const string ModFolderName = "lootfilter";
    public const string GameRelativePath = "Diablo II Resurrected";
    public const string ModsSubfolder = "mods";
    public const string LaunchOptionsHint = "-mod lootfilter -txt";
    public const string InstallMetadataFileName = "installer-release.json";
    private const string GameExecutableName = "D2R.exe";
    private const string LatestReleaseApiUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepository}/releases/latest";

    private static readonly HttpClient HttpClient = new();
    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly Lock ReleaseCacheLock = new();

    private static GitHubReleaseInfo? _cachedLatestRelease;
    private static DateTimeOffset _cachedLatestReleaseAt;
    private static Task<GitHubReleaseInfo>? _latestReleaseTask;

    static Installer()
    {
        HttpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("D2RInstaller", BuildVersion.UserAgentVersion));
        HttpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    internal sealed record GitHubReleaseResponse(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("zipball_url")] string ZipballUrl,
        [property: JsonPropertyName("assets")] GitHubReleaseAsset[] Assets);

    internal sealed record GitHubReleaseAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
        [property: JsonPropertyName("content_type")] string ContentType);

    internal sealed record InstalledReleaseMetadata(
        string Repository,
        string InstalledTag,
        DateTimeOffset InstalledAtUtc);

    private static string? TryGetSteamPathFromRegistry()
    {
        string[] keys =
        [
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",
            @"HKEY_CURRENT_USER\SOFTWARE\Valve\Steam",
        ];

        foreach (var key in keys)
        {
            var value = Registry.GetValue(key, "InstallPath", null) as string;
            if (!string.IsNullOrEmpty(value))
                return value;
        }

        return null;
    }

    private static List<string> GetSteamLibraryFolders(string steamPath)
    {
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf))
            return [Path.Combine(steamPath, "steamapps")];

        var paths = new List<string>();
        foreach (var line in File.ReadAllLines(vdf))
        {
            var trimmed = line.Trim();
            if (!trimmed.Contains("\"path\"")) continue;

            var parts = trimmed.Split('"');
            if (parts.Length < 4) continue;

            var candidate = parts[3].Replace(@"\\", @"\");
            var steamapps = Path.Combine(candidate, "steamapps");
            if (Directory.Exists(steamapps))
                paths.Add(steamapps);
        }

        return paths.Count > 0 ? paths : [Path.Combine(steamPath, "steamapps")];
    }

    private static IEnumerable<string> EnumerateUninstallLocations(string productName)
    {
        RegistryKey?[] roots =
        [
            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        ];

        foreach (var root in roots)
        {
            if (root is null)
                continue;

            using (root)
            {
                foreach (var subKeyName in root.GetSubKeyNames())
                {
                    using var subKey = root.OpenSubKey(subKeyName);
                    if (subKey is null)
                        continue;

                    var displayName = subKey.GetValue("DisplayName") as string;
                    if (!string.Equals(displayName, productName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var installLocation = (subKey.GetValue("InstallLocation") as string)?.Trim();
                    if (!string.IsNullOrWhiteSpace(installLocation))
                        yield return installLocation;

                    var displayIcon = (subKey.GetValue("DisplayIcon") as string)?.Trim();
                    if (string.IsNullOrWhiteSpace(displayIcon))
                        continue;

                    var iconPath = displayIcon.Trim('"');
                    var separatorIndex = iconPath.IndexOf(',');
                    if (separatorIndex >= 0)
                        iconPath = iconPath[..separatorIndex];

                    var iconDirectory = Path.GetDirectoryName(iconPath);
                    if (!string.IsNullOrWhiteSpace(iconDirectory))
                        yield return iconDirectory;
                }
            }
        }
    }

    private static string? TryGetBattleNetLauncherPath()
    {
        return EnumerateUninstallLocations("Battle.net")
            .FirstOrDefault(path => Directory.Exists(path));
    }

    private static IEnumerable<string> GetBattleNetCandidatePaths()
    {
        foreach (var path in EnumerateUninstallLocations(GameRelativePath))
            yield return path;

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
            yield return Path.Combine(programFilesX86, GameRelativePath);

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            yield return Path.Combine(programFiles, GameRelativePath);

        var battleNetPath = TryGetBattleNetLauncherPath();
        if (string.IsNullOrWhiteSpace(battleNetPath))
            yield break;

        var launcherParent = Directory.GetParent(battleNetPath)?.FullName;
        if (!string.IsNullOrWhiteSpace(launcherParent))
            yield return Path.Combine(launcherParent, GameRelativePath);
    }

    private static bool LooksLikeGamePath(string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || !Directory.Exists(candidatePath))
            return false;

        var executablePath = Path.Combine(candidatePath, GameExecutableName);
        if (File.Exists(executablePath))
            return true;

        return Directory.Exists(Path.Combine(candidatePath, "Data"));
    }

    private static string? FindFirstExistingGamePath(IEnumerable<string> candidatePaths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidatePath in candidatePaths)
        {
            if (string.IsNullOrWhiteSpace(candidatePath))
                continue;

            var normalizedPath = Path.GetFullPath(candidatePath.Trim().Trim('"'))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (!seen.Add(normalizedPath))
                continue;

            if (LooksLikeGamePath(normalizedPath))
                return normalizedPath;
        }

        return null;
    }

    public static PathResolution ResolveGamePath(string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
            return PathResolution.Failure("Enter the Diablo II: Resurrected install folder.");

        var fullPath = Path.GetFullPath(candidatePath.Trim().Trim('"'));
        if (!Directory.Exists(fullPath))
            return PathResolution.Failure("The selected folder does not exist.");

        var normalizedPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var folderName = Path.GetFileName(normalizedPath);

        if (folderName.Equals(ModsSubfolder, StringComparison.OrdinalIgnoreCase))
        {
            var gameDirectory = Directory.GetParent(normalizedPath)?.FullName;
            return gameDirectory is null
                ? PathResolution.Failure("Select the Diablo II: Resurrected install folder.")
                : PathResolution.Success(gameDirectory);
        }

        if (folderName.Equals(ModFolderName, StringComparison.OrdinalIgnoreCase))
        {
            var modsDirectory = Directory.GetParent(normalizedPath)?.FullName;
            var gameDirectory = modsDirectory is null ? null : Directory.GetParent(modsDirectory)?.FullName;
            return gameDirectory is null
                ? PathResolution.Failure("Select the Diablo II: Resurrected install folder.")
                : PathResolution.Success(gameDirectory);
        }

        return PathResolution.Success(normalizedPath);
    }

    public static PathResolution FindD2RInstallPath()
    {
        var steamPath = TryGetSteamPathFromRegistry();
        if (steamPath is not null)
        {
            var steamGamePath = FindFirstExistingGamePath(
                GetSteamLibraryFolders(steamPath).Select(lib => Path.Combine(lib, "common", GameRelativePath)));
            if (steamGamePath is not null)
                return PathResolution.Success(steamGamePath);
        }

        var battleNetGamePath = FindFirstExistingGamePath(GetBattleNetCandidatePaths());
        if (battleNetGamePath is not null)
            return PathResolution.Success(battleNetGamePath);

        return PathResolution.Failure(
            "Diablo II: Resurrected installation not found in Steam libraries or common Battle.net install locations.");
    }

    public static async Task<InstalledModStatus> GetInstalledModStatusAsync(string gamePath)
    {
        var installPath = GetInstallPath(gamePath);
        if (!LooksLikeInstalledMod(installPath))
        {
            try
            {
                var latestRelease = await GetLatestReleaseAsync();
                return new InstalledModStatus(InstalledModState.NotInstalled, null, latestRelease.TagName, null);
            }
            catch (Exception ex)
            {
                return new InstalledModStatus(InstalledModState.NotInstalled, null, null, $"Unable to check GitHub tag: {ex.Message}");
            }
        }

        var installedTag = TryReadInstalledTag(installPath);

        try
        {
            var latestRelease = await GetLatestReleaseAsync();
            if (string.IsNullOrWhiteSpace(installedTag))
                return new InstalledModStatus(InstalledModState.InstalledUnknownVersion, null, latestRelease.TagName, null);

            var state = string.Equals(installedTag, latestRelease.TagName, StringComparison.OrdinalIgnoreCase)
                ? InstalledModState.InstalledCurrent
                : InstalledModState.InstalledOutdated;
            return new InstalledModStatus(state, installedTag, latestRelease.TagName, null);
        }
        catch (Exception ex)
        {
            return new InstalledModStatus(
                string.IsNullOrWhiteSpace(installedTag) ? InstalledModState.InstalledUnknownVersion : InstalledModState.InstalledOutdated,
                installedTag,
                null,
                $"Unable to check GitHub tag: {ex.Message}");
        }
    }

    public static async Task<InstallResult> InstallLatestLootFilterAsync(
        Action<InstallProgress> progress, string gamePath)
    {
        var installPath = GetInstallPath(gamePath);
        var tempFileId = Guid.NewGuid().ToString("N");
        var tempZipPath = Path.Combine(Path.GetTempPath(), $"d2r-loot-filter-{tempFileId}.zip");
        var tempExtractPath = Path.Combine(Path.GetTempPath(), $"d2r-loot-filter-{tempFileId}");

        try
        {
            var latestRelease = await GetLatestReleaseAsync();

            progress(new InstallProgress(InstallStep.DownloadingMod, "Downloading loot filter...", 10));
            await DownloadArchiveAsync(tempZipPath, latestRelease.DownloadUrl);

            progress(new InstallProgress(InstallStep.ExtractingFiles, "Extracting files...", 50));
            TryDeleteDirectory(tempExtractPath);
            ZipFile.ExtractToDirectory(tempZipPath, tempExtractPath);

            progress(new InstallProgress(InstallStep.CopyingFiles, "Copying mod files...", 75));
            var sourceDir = FindPackagedModRoot(tempExtractPath);
            if (!Directory.Exists(sourceDir))
                return InstallResult.Failure("Expected mod folder not found in archive.");

            ReplaceDirectoryContents(sourceDir, installPath);
            WriteInstalledTag(installPath, latestRelease.TagName);

            progress(new InstallProgress(InstallStep.Done, "Loot filter installed successfully!", 100));
            return InstallResult.Success(installPath, latestRelease.TagName);
        }
        catch (Exception ex)
        {
            return InstallResult.Failure($"Installation failed: {ex.Message}");
        }
        finally
        {
            TryDeleteFile(tempZipPath);
            TryDeleteDirectory(tempExtractPath);
        }
    }

    public static UninstallResult UninstallLootFilter(string gamePath)
    {
        var installPath = GetInstallPath(gamePath);
        if (!Directory.Exists(installPath))
            return UninstallResult.Failure("No installed mods folder was found for this game path.");

        try
        {
            TryDeleteDirectory(installPath);
            return UninstallResult.Success(installPath);
        }
        catch (Exception ex)
        {
            return UninstallResult.Failure($"Uninstall failed: {ex.Message}");
        }
    }

    private static string GetInstallPath(string gamePath)
    {
        return Path.Combine(gamePath, ModsSubfolder, ModFolderName);
    }

    private static string GetInstalledContentPath(string installPath)
    {
        var mpqPath = Path.Combine(installPath, "lootfilter.mpq");
        return Directory.Exists(mpqPath) ? mpqPath : installPath;
    }

    private static bool LooksLikeInstalledMod(string installPath)
    {
        if (!Directory.Exists(installPath))
            return false;

        var contentPath = GetInstalledContentPath(installPath);
        return File.Exists(Path.Combine(contentPath, "mod.json"))
            || File.Exists(Path.Combine(contentPath, "modinfo.json"))
            || Directory.Exists(Path.Combine(contentPath, "Data"));
    }

    private static string? TryReadInstalledTag(string installPath)
    {
        var metadataPath = Path.Combine(installPath, InstallMetadataFileName);
        if (!File.Exists(metadataPath))
            return null;

        try
        {
            var metadata = JsonSerializer.Deserialize(
                File.ReadAllText(metadataPath),
                InstallerJsonContext.Default.InstalledReleaseMetadata);
            return string.IsNullOrWhiteSpace(metadata?.InstalledTag) ? null : metadata.InstalledTag;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteInstalledTag(string installPath, string installedTag)
    {
        Directory.CreateDirectory(installPath);

        var metadata = new InstalledReleaseMetadata(
            $"{GitHubOwner}/{GitHubRepository}",
            installedTag,
            DateTimeOffset.UtcNow);

        var metadataPath = Path.Combine(installPath, InstallMetadataFileName);
        File.WriteAllText(
            metadataPath,
            JsonSerializer.Serialize(metadata, InstallerJsonContext.Default.InstalledReleaseMetadata));
    }

    private static string? FindPackagedModRoot(string extractedRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidateRoots = new List<string>();

        void AddCandidate(string path)
        {
            if (!Directory.Exists(path))
                return;

            var fullPath = Path.GetFullPath(path);
            if (seen.Add(fullPath))
                candidateRoots.Add(fullPath);
        }

        AddCandidate(Path.Combine(extractedRoot, ModFolderName));

        foreach (var directory in Directory.GetDirectories(extractedRoot))
        {
            AddCandidate(directory);
            AddCandidate(Path.Combine(directory, ModFolderName));
        }

        foreach (var directory in Directory.EnumerateDirectories(extractedRoot, ModFolderName, SearchOption.AllDirectories))
            AddCandidate(directory);

        return candidateRoots.FirstOrDefault(IsPackagedModRoot);
    }

    private static bool IsPackagedModRoot(string candidateRoot)
    {
        return Directory.Exists(Path.Combine(candidateRoot, "lootfilter.mpq"))
            || File.Exists(Path.Combine(candidateRoot, "mod.json"))
            || Directory.Exists(Path.Combine(candidateRoot, "Data"));
    }

    public static Task<GitHubReleaseInfo> GetLatestReleaseAsync()
    {
        lock (ReleaseCacheLock)
        {
            if (_cachedLatestRelease is not null && DateTimeOffset.UtcNow - _cachedLatestReleaseAt < TimeSpan.FromMinutes(10))
                return Task.FromResult(_cachedLatestRelease.Value);

            _latestReleaseTask ??= FetchLatestReleaseAsync();
            return _latestReleaseTask;
        }
    }

    private static async Task<GitHubReleaseInfo> FetchLatestReleaseAsync()
    {
        try
        {
            using var response = await HttpClient.GetAsync(LatestReleaseApiUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var responseStream = await response.Content.ReadAsStreamAsync();
            var release = await JsonSerializer.DeserializeAsync(
                responseStream,
                InstallerJsonContext.Default.GitHubReleaseResponse)
                ?? throw new InvalidOperationException("GitHub latest release response was empty.");

            if (string.IsNullOrWhiteSpace(release.TagName))
                throw new InvalidOperationException("GitHub latest release did not include a tag.");

            var assetUrl = release.Assets
                .FirstOrDefault(asset =>
                    asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    || asset.ContentType.Contains("zip", StringComparison.OrdinalIgnoreCase))
                ?.BrowserDownloadUrl;

            var downloadUrl = !string.IsNullOrWhiteSpace(assetUrl)
                ? assetUrl
                : release.ZipballUrl;

            if (string.IsNullOrWhiteSpace(downloadUrl))
                throw new InvalidOperationException("GitHub latest release did not include a downloadable archive.");

            var latestRelease = new GitHubReleaseInfo(release.TagName, downloadUrl);

            lock (ReleaseCacheLock)
            {
                _cachedLatestRelease = latestRelease;
                _cachedLatestReleaseAt = DateTimeOffset.UtcNow;
                _latestReleaseTask = null;
            }

            return latestRelease;
        }
        catch
        {
            lock (ReleaseCacheLock)
            {
                _latestReleaseTask = null;
            }

            throw;
        }
    }

    private static async Task DownloadArchiveAsync(string destinationPath, string archiveUrl)
    {
        using var response = await HttpClient.GetAsync(archiveUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var fileStream = File.Create(destinationPath);
        await response.Content.CopyToAsync(fileStream);
    }

    private static void ReplaceDirectoryContents(string sourceDir, string destinationDir)
    {
        TryDeleteDirectory(destinationDir);
        Directory.CreateDirectory(destinationDir);

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, file);
            var destinationPath = Path.Combine(destinationDir, relativePath);
            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDirectory))
                Directory.CreateDirectory(destinationDirectory);

            File.Copy(file, destinationPath, true);
        }
    }

    private static void TryDeleteFile(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void TryDeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, true);
    }
}
