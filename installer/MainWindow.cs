using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace D2RInstaller;

public sealed partial class MainWindow : Window
{
    private string? _installedPath;
    private bool _canUninstall;
    private bool _isBusy;
    private int _statusRefreshVersion;

    public MainWindow()
    {
        Title = "D2R Mods Installer";
        InitializeComponent();
        SetWindowIcon();
        ConfigureWindowChrome();
        LaunchMaximized();
        LaunchArgsTextBox.Text = Installer.LaunchOptionsHint;
        ResetInstallStatusDetails();
        _ = LoadSuggestedGamePathAsync();
    }

    private void SetWindowIcon()
    {
        var windowId = Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        var appWindow = AppWindow.GetFromWindowId(windowId);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "images", "d2r-lod.ico");

        if (File.Exists(iconPath))
            appWindow.SetIcon(iconPath);
    }

    private void ConfigureWindowChrome()
    {
        var windowId = Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        var appWindow = AppWindow.GetFromWindowId(windowId);

        if (!AppWindowTitleBar.IsCustomizationSupported())
            return;

        var titleBar = appWindow.TitleBar;
        titleBar.BackgroundColor = ColorHelper.FromArgb(0xFF, 0x12, 0x10, 0x11);
        titleBar.ForegroundColor = ColorHelper.FromArgb(0xFF, 0xF2, 0xDE, 0xC2);
        titleBar.ButtonBackgroundColor = ColorHelper.FromArgb(0xFF, 0x12, 0x10, 0x11);
        titleBar.ButtonForegroundColor = ColorHelper.FromArgb(0xFF, 0xF2, 0xDE, 0xC2);
        titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(0xFF, 0x2B, 0x17, 0x0E);
        titleBar.ButtonHoverForegroundColor = Colors.White;
        titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(0xFF, 0x40, 0x24, 0x16);
        titleBar.ButtonPressedForegroundColor = Colors.White;
        titleBar.InactiveBackgroundColor = ColorHelper.FromArgb(0xFF, 0x19, 0x16, 0x17);
        titleBar.InactiveForegroundColor = ColorHelper.FromArgb(0xFF, 0xC7, 0xB0, 0x8E);
        titleBar.ButtonInactiveBackgroundColor = ColorHelper.FromArgb(0xFF, 0x19, 0x16, 0x17);
        titleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(0xFF, 0xC7, 0xB0, 0x8E);
    }

    private void LaunchMaximized()
    {
        var windowId = Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        var appWindow = AppWindow.GetFromWindowId(windowId);

        if (appWindow.Presenter is OverlappedPresenter overlappedPresenter)
        {
            overlappedPresenter.Maximize();
        }
    }

    private async Task LoadSuggestedGamePathAsync()
    {
        var resolution = Installer.FindD2RInstallPath();
        if (resolution.Succeeded && resolution.GamePath is not null)
        {
            GamePathTextBox.Text = resolution.GamePath;
            ShowPathStatus(
                InfoBarSeverity.Success,
                "Game folder detected.",
                "Auto-detect found Diablo II: Resurrected automatically.");
            UpdateInstallDestination(resolution.GamePath);
            await RefreshInstallStatusAsync(resolution.GamePath);
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
        _ = LoadSuggestedGamePathAsync();
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

        _isBusy = true;
        UpdateActionButtons();
        ShowInstallStatus(InfoBarSeverity.Informational, "Installing", "Downloading and extracting the latest loot filter files.");
        UpdateProgress(new InstallProgress(InstallStep.DownloadingMod, "Starting install...", 0));
        var installCompleted = false;

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
            installCompleted = true;
            UpdateProgress(new InstallProgress(InstallStep.Done, "Loot filter installed successfully.", 100));
            ShowInstallStatus(
                InfoBarSeverity.Success,
                "Install complete",
                $"Installed tag {result.InstalledTag} to {_installedPath}");
            UpdateInstallDestination(resolution.GamePath);
        }
        catch (Exception ex)
        {
            ShowInstallStatus(InfoBarSeverity.Error, "Install failed", ex.Message);
        }
        finally
        {
            _isBusy = false;
            UpdateActionButtons();
        }

        if (installCompleted)
            await RefreshInstallStatusAsync(resolution.GamePath);
    }

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        var resolution = RefreshResolvedPath(showValidationError: true);
        if (!resolution.Succeeded || resolution.GamePath is null)
            return;

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Remove installed mods?",
            Content = $"This deletes the installed mods folder at:{Environment.NewLine}{InstallDestinationText.Text}",
            PrimaryButtonText = "Uninstall",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        var dialogResult = await dialog.ShowAsync();
        if (dialogResult != ContentDialogResult.Primary)
            return;

        _isBusy = true;
        UpdateActionButtons();
        InstallProgressBar.IsIndeterminate = true;
        InstallStepText.Text = "Removing installed mods";
        InstallMessageText.Text = "Deleting the installed mods folder from the Diablo II: Resurrected mods directory.";
        InstallPercentText.Text = "...";
        ShowInstallStatus(InfoBarSeverity.Informational, "Uninstalling", "Removing the installed mods folder.");
        var uninstallCompleted = false;

        try
        {
            var result = await Task.Run(() => Installer.UninstallLootFilter(resolution.GamePath));
            if (!result.Succeeded || result.RemovedPath is null)
            {
                ShowInstallStatus(
                    InfoBarSeverity.Error,
                    "Uninstall failed",
                    result.ErrorMessage ?? "The installed mods folder could not be removed.");
                return;
            }

            _installedPath = null;
            _canUninstall = false;
            uninstallCompleted = true;
            ShowInstallStatus(InfoBarSeverity.Success, "Uninstall complete", $"Removed {result.RemovedPath}");
        }
        catch (Exception ex)
        {
            ShowInstallStatus(InfoBarSeverity.Error, "Uninstall failed", ex.Message);
        }
        finally
        {
            InstallProgressBar.IsIndeterminate = false;
            _isBusy = false;
            UpdateActionButtons();
        }

        if (uninstallCompleted)
            await RefreshInstallStatusAsync(resolution.GamePath);
    }

    private void CopyLaunchArgsButton_Click(object sender, RoutedEventArgs e)
    {
        var dataPackage = new DataPackage();
        dataPackage.SetText(Installer.LaunchOptionsHint);
        Clipboard.SetContent(dataPackage);
        ShowInstallStatus(InfoBarSeverity.Success, "Copied", "Launch arguments copied to the clipboard.");
    }

    private PathResolution RefreshResolvedPath(bool showValidationError)
    {
        var resolution = Installer.ResolveGamePath(GamePathTextBox.Text);
        if (!resolution.Succeeded || resolution.GamePath is null)
        {
            _installedPath = null;
            _canUninstall = false;
            InstallDestinationText.Text = "Select a valid game folder to preview the install path.";
            ResetInstallStatusDetails();

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
        _ = RefreshInstallStatusAsync(resolution.GamePath);
        return resolution;
    }

    private void UpdateInstallDestination(string gamePath)
    {
        var normalizedInstallPath = Path.Combine(gamePath, Installer.ModsSubfolder, Installer.ModFolderName);
        InstallDestinationText.Text = normalizedInstallPath;
        _canUninstall = Directory.Exists(normalizedInstallPath);
        if (_installedPath is null && _canUninstall)
            _installedPath = normalizedInstallPath;

        UpdateActionButtons();
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

    private async Task RefreshInstallStatusAsync(string gamePath)
    {
        var requestVersion = ++_statusRefreshVersion;

        InstalledTagText.Text = "Checking...";
        LatestTagText.Text = "Checking...";

        if (!_isBusy)
        {
            InstallStepText.Text = "Checking install status";
            InstallMessageText.Text = "Inspecting the installed files and the latest GitHub tag.";
        }

        var status = await Installer.GetInstalledModStatusAsync(gamePath);
        if (requestVersion != _statusRefreshVersion || _isBusy)
            return;

        ApplyInstallStatus(status);
    }

    private void ApplyInstallStatus(InstalledModStatus status)
    {
        _canUninstall = status.IsInstalled;
        UpdateActionButtons();
        InstalledTagText.Text = status.InstalledTag ?? (status.IsInstalled ? "Unknown" : "Not installed");
        LatestTagText.Text = status.LatestTag ?? "Unavailable";

        if (!string.IsNullOrWhiteSpace(status.ErrorMessage))
            ShowInstallStatus(InfoBarSeverity.Warning, "GitHub tag check unavailable", status.ErrorMessage);

        switch (status.State)
        {
            case InstalledModState.NotInstalled:
                InstallStepText.Text = "Not installed";
                InstallMessageText.Text = status.LatestTag is null
                    ? "No loot filter install was found for this game folder."
                    : $"No loot filter install was found. Latest GitHub tag: {status.LatestTag}.";
                InstallProgressBar.Value = 0;
                InstallPercentText.Text = "0%";
                break;
            case InstalledModState.InstalledUnknownVersion:
                InstallStepText.Text = "Installed version unknown";
                InstallMessageText.Text = status.LatestTag is null
                    ? "Loot filter files were found, but no installer tag metadata was found."
                    : $"Loot filter files were found, but no installer tag metadata was found. Latest GitHub tag: {status.LatestTag}.";
                InstallProgressBar.Value = 0;
                InstallPercentText.Text = "0%";
                break;
            case InstalledModState.InstalledOutdated:
                InstallStepText.Text = "Update available";
                InstallMessageText.Text = status.LatestTag is null
                    ? $"Loot filter tag {status.InstalledTag} is installed."
                    : $"Loot filter tag {status.InstalledTag} is installed. Latest GitHub tag: {status.LatestTag}.";
                InstallProgressBar.Value = 0;
                InstallPercentText.Text = "0%";
                break;
            case InstalledModState.InstalledCurrent:
                InstallStepText.Text = "Installed and current";
                InstallMessageText.Text = $"Loot filter tag {status.InstalledTag} is already installed and matches the latest GitHub tag.";
                InstallProgressBar.Value = 100;
                InstallPercentText.Text = "100%";
                break;
        }
    }

    private void ResetInstallStatusDetails()
    {
        _canUninstall = false;
        InstalledTagText.Text = "Not checked";
        LatestTagText.Text = "Not checked";
        InstallProgressBar.Value = 0;
        InstallProgressBar.IsIndeterminate = false;
        InstallPercentText.Text = "0%";
        InstallStepText.Text = "Ready to install";
        InstallMessageText.Text = "Use auto-detect or browse to select the game folder, then install the latest tagged loot filter release.";
        UpdateActionButtons();
    }

    private void UpdateActionButtons()
    {
        InstallButton.IsEnabled = !_isBusy;
        UninstallButton.IsEnabled = !_isBusy && _canUninstall;
        BrowseButton.IsEnabled = !_isBusy;
        DetectButton.IsEnabled = !_isBusy;
        GamePathTextBox.IsEnabled = !_isBusy;
        InstallProgressBar.ShowPaused = false;
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
