using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace D2RInstaller;

public sealed partial class MainWindow : Window
{
    private string? _installedPath;

    public MainWindow()
    {
        Title = "D2R Loot Filter Installer";
        InitializeComponent();
        LaunchArgsTextBox.Text = Installer.LaunchOptionsHint;
        LoadSuggestedGamePath();
    }

    private void LoadSuggestedGamePath()
    {
        var resolution = Installer.FindD2RInstallPath();
        if (resolution.Succeeded && resolution.GamePath is not null)
        {
            GamePathTextBox.Text = resolution.GamePath;
            ShowPathStatus(
                InfoBarSeverity.Success,
                "Game folder detected.",
                "Steam library detection found Diablo II: Resurrected automatically.");
            UpdateInstallDestination(resolution.GamePath);
            return;
        }

        ShowPathStatus(
            InfoBarSeverity.Warning,
            "Auto-detect unavailable.",
            resolution.ErrorMessage ?? "Select the Diablo II: Resurrected folder manually.");
        InstallDestinationText.Text = "Select a valid game folder to preview the install path.";
    }

    private void GamePathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshResolvedPath(showValidationError: false);
    }

    private void AutoDetectButton_Click(object sender, RoutedEventArgs e)
    {
        LoadSuggestedGamePath();
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
            return;

        GamePathTextBox.Text = folder.Path;
        RefreshResolvedPath(showValidationError: true);
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        var resolution = RefreshResolvedPath(showValidationError: true);
        if (!resolution.Succeeded || resolution.GamePath is null)
            return;

        SetInstallState(isInstalling: true);
        ShowInstallStatus(InfoBarSeverity.Informational, "Installing", "Downloading and extracting the latest loot filter files.");
        UpdateProgress(new InstallProgress(InstallStep.DownloadingMod, "Starting install...", 0));

        try
        {
            var result = await Installer.InstallLatestLootFilterAsync(UpdateProgress, resolution.GamePath);
            if (!result.Succeeded || result.InstalledPath is null)
            {
                ShowInstallStatus(
                    InfoBarSeverity.Error,
                    "Install failed",
                    result.ErrorMessage ?? "The installer stopped before completing.");
                return;
            }

            _installedPath = result.InstalledPath;
            OpenInstallFolderButton.IsEnabled = Directory.Exists(_installedPath);
            UpdateProgress(new InstallProgress(InstallStep.Done, "Loot filter installed successfully.", 100));
            ShowInstallStatus(
                InfoBarSeverity.Success,
                "Install complete",
                $"Installed to {_installedPath}");
            UpdateInstallDestination(resolution.GamePath);
        }
        catch (Exception ex)
        {
            ShowInstallStatus(InfoBarSeverity.Error, "Install failed", ex.Message);
        }
        finally
        {
            SetInstallState(isInstalling: false);
        }
    }

    private void CopyLaunchArgsButton_Click(object sender, RoutedEventArgs e)
    {
        var dataPackage = new DataPackage();
        dataPackage.SetText(Installer.LaunchOptionsHint);
        Clipboard.SetContent(dataPackage);
        ShowInstallStatus(InfoBarSeverity.Success, "Copied", "Launch arguments copied to the clipboard.");
    }

    private void OpenInstallFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_installedPath) || !Directory.Exists(_installedPath))
        {
            ShowInstallStatus(InfoBarSeverity.Warning, "Folder unavailable", "Run the installer successfully before opening the folder.");
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = _installedPath,
            UseShellExecute = true,
        });
    }

    private PathResolution RefreshResolvedPath(bool showValidationError)
    {
        var resolution = Installer.ResolveGamePath(GamePathTextBox.Text);
        if (!resolution.Succeeded || resolution.GamePath is null)
        {
            _installedPath = null;
            OpenInstallFolderButton.IsEnabled = false;
            InstallDestinationText.Text = "Select a valid game folder to preview the install path.";

            if (showValidationError)
            {
                ShowPathStatus(
                    InfoBarSeverity.Error,
                    "Invalid game folder",
                    resolution.ErrorMessage ?? "Select the Diablo II: Resurrected installation folder.");
            }

            return resolution;
        }

        UpdateInstallDestination(resolution.GamePath);
        ShowPathStatus(
            InfoBarSeverity.Success,
            "Path looks good.",
            "The installer will place the mod inside the Diablo II: Resurrected mods folder.");
        return resolution;
    }

    private void UpdateInstallDestination(string gamePath)
    {
        var normalizedInstallPath = Path.Combine(gamePath, Installer.ModsSubfolder, Installer.ModFolderName);
        InstallDestinationText.Text = normalizedInstallPath;
        if (_installedPath is null && Directory.Exists(normalizedInstallPath))
            _installedPath = normalizedInstallPath;
    }

    private void UpdateProgress(InstallProgress progress)
    {
        InstallProgressBar.Value = progress.Percent;
        InstallPercentText.Text = $"{progress.Percent}%";
        InstallStepText.Text = progress.Step switch
        {
            InstallStep.DownloadingMod => "Downloading archive",
            InstallStep.ExtractingFiles => "Extracting files",
            InstallStep.CopyingFiles => "Copying mod files",
            InstallStep.Done => "Complete",
            _ => "Installing"
        };
        InstallMessageText.Text = progress.Message;
    }

    private void SetInstallState(bool isInstalling)
    {
        InstallButton.IsEnabled = !isInstalling;
        BrowseButton.IsEnabled = !isInstalling;
        DetectButton.IsEnabled = !isInstalling;
        GamePathTextBox.IsEnabled = !isInstalling;
        InstallProgressBar.ShowPaused = false;
        InstallProgressBar.IsIndeterminate = false;
    }

    private void ShowPathStatus(InfoBarSeverity severity, string title, string message)
    {
        PathInfoBar.Severity = severity;
        PathInfoBar.Title = title;
        PathInfoBar.Message = message;
        PathInfoBar.IsOpen = true;
    }

    private void ShowInstallStatus(InfoBarSeverity severity, string title, string message)
    {
        InstallInfoBar.Severity = severity;
        InstallInfoBar.Title = title;
        InstallInfoBar.Message = message;
        InstallInfoBar.IsOpen = true;
    }
}
