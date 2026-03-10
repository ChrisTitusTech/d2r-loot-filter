using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.Versioning;
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

public readonly record struct InstallResult(bool Succeeded, string? InstalledPath, string? ErrorMessage)
{
    public static InstallResult Success(string installedPath) => new(true, installedPath, null);

    public static InstallResult Failure(string errorMessage) => new(false, null, errorMessage);
}

[SupportedOSPlatform("windows")]
public static class Installer
{
    public const string GitRepoZipUrl = "https://github.com/ChrisTitusTech/d2r-loot-filter/archive/refs/heads/main.zip";
    public const string ModFolderName = "lootfilter";
    public const string ModSourceSubfolder = "d2r-loot-filter-main/lootfilter/lootfilter.mpq";
    public const string GameRelativePath = "Diablo II Resurrected";
    public const string ModsSubfolder = "mods";
    public const string LaunchOptionsHint = "-mod lootfilter -txt";

    private static readonly HttpClient HttpClient = new();

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
        if (steamPath is null)
            return PathResolution.Failure("Steam installation not found in registry.");

        foreach (var lib in GetSteamLibraryFolders(steamPath))
        {
            var gamePath = Path.Combine(lib, "common", GameRelativePath);
            if (Directory.Exists(gamePath))
                return PathResolution.Success(gamePath);
        }

        return PathResolution.Failure("Diablo II: Resurrected installation not found in any Steam library.");
    }

    public static async Task<InstallResult> InstallLatestLootFilterAsync(
        Action<InstallProgress> progress, string gamePath)
    {
        var installPath = Path.Combine(gamePath, ModsSubfolder, ModFolderName);
        var tempFileId = Guid.NewGuid().ToString("N");
        var tempZipPath = Path.Combine(Path.GetTempPath(), $"d2r-loot-filter-{tempFileId}.zip");
        var tempExtractPath = Path.Combine(Path.GetTempPath(), $"d2r-loot-filter-{tempFileId}");

        try
        {
            progress(new InstallProgress(InstallStep.DownloadingMod, "Downloading loot filter...", 10));
            await DownloadArchiveAsync(tempZipPath);

            progress(new InstallProgress(InstallStep.ExtractingFiles, "Extracting files...", 50));
            TryDeleteDirectory(tempExtractPath);
            ZipFile.ExtractToDirectory(tempZipPath, tempExtractPath);

            progress(new InstallProgress(InstallStep.CopyingFiles, "Copying mod files...", 75));
            var sourceDir = Path.Combine(tempExtractPath, ModSourceSubfolder);
            if (!Directory.Exists(sourceDir))
                return InstallResult.Failure($"Expected mod folder not found in archive: {ModSourceSubfolder}");

            ReplaceDirectoryContents(sourceDir, installPath);

            progress(new InstallProgress(InstallStep.Done, "Loot filter installed successfully!", 100));
            return InstallResult.Success(installPath);
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

    private static async Task DownloadArchiveAsync(string destinationPath)
    {
        using var response = await HttpClient.GetAsync(GitRepoZipUrl, HttpCompletionOption.ResponseHeadersRead);
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
