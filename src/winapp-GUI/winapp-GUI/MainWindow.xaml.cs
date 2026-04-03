using winapp_GUI;
using winapp_GUI.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using WinRT.Interop;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace winapp_GUI
{
    public enum AppType
    {
        DotNet,
        Python,
        Msix
    }

    public sealed partial class MainWindow : Window
    {
        private readonly CliService cliService = new();
        private readonly ManifestService manifestService = new();
        private readonly PackageOptionsService optionsService = new();
        private readonly NetAppService netAppService;
        private readonly MsixService msixService;
        private readonly PythonAppService pythonAppService;

        private string exePath = string.Empty;
        private string? selectedFolderPath;

        // Add new fields for MSIX tab
        private string msixPath = string.Empty;
        public string? SelectedMsixName { get; set; }

        public ObservableCollection<ProgressCard> progressCards { get; set; } = new();
        private ProgressCard? currentCard;

        public string? SelectedExeName { get; set; }

        private AppType selectedAppType = AppType.DotNet;
        public MainWindow()
        {
            InitializeComponent();
            SetupWindow();

            DotNetProgressPanel.ProgressCards = progressCards;
            PythonProgressPanel.ProgressCards = progressCards;
            MsixProgressPanel.ProgressCards = progressCards;

            DotNetFileNamePanel.CancelClicked += DotNetFileNamePanel_CancelClicked;
            PythonFileNamePanel.CancelClicked += PythonFileNamePanel_CancelClicked;
            MsixFileNamePanel.CancelClicked += MsixFileNamePanel_CancelClicked;

            DotNetCertPicker.BrowseClicked += CertBrowseButton_Click;
            PythonCertPicker.BrowseClicked += CertBrowseButton_Click;
            MsixCertPicker.BrowseClicked += MsixCertBrowseButton_Click;

            netAppService = new NetAppService(cliService, manifestService, optionsService);
            msixService = new MsixService(cliService, manifestService, optionsService);
            pythonAppService = new PythonAppService(cliService, manifestService, optionsService);
        }

        private void SetupWindow()
        {
            if (AppWindowTitleBar.IsCustomizationSupported() is true)
            {
                IntPtr hWnd = WindowNative.GetWindowHandle(this);
                WindowId wndId = Win32Interop.GetWindowIdFromWindow(hWnd);
                AppWindow appWindow = AppWindow.GetFromWindowId(wndId);
                appWindow.SetIcon(@"Assets\AppIcon.ico");
            }

            var hwnd = WindowNative.GetWindowHandle(this);
            var window = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
            window.Resize(new Windows.Graphics.SizeInt32 { Width = 1000, Height = 1000 });
        }

        private void SetExe(string? path)
        {
            exePath = path ?? string.Empty;
            SelectedExeName = path != null ? Path.GetFileName(path) : null;
            DotNetFileNamePanel.Visibility = SelectedExeName == null ? Visibility.Collapsed : Visibility.Visible;
            DotNetFileNamePanel.FileName = SelectedExeName ?? "";
        }

        private void BindExeName()
        {
            DotNetFileNamePanel.Visibility = SelectedExeName != null ? Visibility.Visible : Visibility.Collapsed;
            DotNetFileNamePanel.FileName = SelectedExeName ?? "";
        }

        // --- ProgressCard logic ---
        private void ShowErrorCard(string title, params string[] messages)
        {
            currentCard = ProgressCard.ShowError(title, progressCards, messages);
        }

        private void StartProgressCard(string title)
        {
            currentCard = ProgressCard.Start(title, progressCards);
        }

        private void MarkCurrentCardSuccess()
        {
            currentCard?.MarkSuccess();
        }
        private void AddProgressSubItem(string message)
        {
            currentCard?.AddSubItem(message);
        }

        private void StartProgressCardWithMessages(string title, params string[] messages)
        {
            currentCard = ProgressCard.StartWithMessages(title, progressCards, messages);
        }

        // --- Certificate Picker Logic ---
        private void CertBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.List;
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(".pfx");
            picker.FileTypeFilter.Add(".cer");
            picker.FileTypeFilter.Add(".crt");
            picker.FileTypeFilter.Add(".pem");
            picker.FileTypeFilter.Add(".cert");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            _ = PickCertFileAsync(picker);
        }

        private async Task PickCertFileAsync(Windows.Storage.Pickers.FileOpenPicker picker)
        {
            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                optionsService.SetCertPath(file.Path);
                // Show filename in the active certificate picker control
                if (DotNetCertPicker.Visibility == Visibility.Visible)
                    DotNetCertPicker.CertificatePath = file.Path;
                if (PythonCertPicker.Visibility == Visibility.Visible)
                    PythonCertPicker.CertificatePath = file.Path;
            }
        }

        private void ShowCertPicker(bool show)
        {
            DotNetCertPicker.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        // --- Drag & Drop Logic ---
        private void MsixDropZone_Drop(object sender, DragEventArgs e)
        {
            progressCards.Clear();
            currentCard = null;
            msixPath = string.Empty;
            SelectedMsixName = null;
            MsixFileNamePanel.Visibility = Visibility.Collapsed;
            RegisterInstallButton.IsEnabled = false;
            try
            {
                if (e.DataView.Contains(StandardDataFormats.StorageItems))
                {
                    var itemsTask = e.DataView.GetStorageItemsAsync().AsTask();
                    itemsTask.Wait();
                    var items = itemsTask.Result;
                    var result = msixService.HandleMsixDrop(items, progressCards);

                    if (result != null && result.MsixPath != null)
                    {
                        msixPath = result.MsixPath;
                        SelectedMsixName = result.SelectedMsixName;
                        currentCard = result.CurrentCard;
                        // Update UI
                        MsixFileNamePanel.Visibility = result.MsixFileNamePanelVisible ? Visibility.Visible : Visibility.Collapsed;
                        MsixFileNamePanel.FileName = SelectedMsixName ?? "";
                        RegisterInstallButton.IsEnabled = result.RegisterInstallButtonEnabled;
                    }
                    else
                    {
                        ShowErrorCard("File Detection", "❌ Please drop a valid .msix file.");
                        MsixFileNamePanel.Visibility = Visibility.Collapsed;
                        RegisterInstallButton.IsEnabled = false;
                    }
                }
            }
            catch (Exception ex)
            {
                ShowErrorCard("❌ Error", $"Exception: {ex.Message}");
                MsixFileNamePanel.Visibility = Visibility.Collapsed;
                RegisterInstallButton.IsEnabled = false;
            }
        }

        private void DotNetDropZone_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.Handled = true;
        }

        private async void AddIdentityButton_ClickAsync(object sender, RoutedEventArgs e)
        {
            OptionalParametersExpander.IsExpanded = false;
            currentCard = null;

            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                ShowErrorAndResetUI("❌ Error", "No file selected.", "Process failed: No file selected.");
                return;
            }

            bool success = false;
            var publisherName = optionsService.GetPublisherName(PublisherNameBox.Text);
            var appName = optionsService.GetAppName(AppNameBox.Text);

            if (selectedAppType == AppType.DotNet)
            {
                success = await netAppService.AddIdentityAsync(exePath, progressCards, currentCard, publisherName, appName);
            }
            else if (selectedAppType == AppType.Python)
            {
                success = await pythonAppService.AddIdentityAsync(exePath, progressCards, currentCard, publisherName, appName);
            }

            if (success)
            {
                StartProgressCardWithMessages("✅ Success", "Debug identity added and registered successfully!");
            }
        }

        // --- Package App Button ---
        private async void PackageAppButton_ClickAsync(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(selectedFolderPath))
            {
                ShowErrorAndResetUI("❌ Error", "No folder selected for packaging.");
                return;
            }

            progressCards.Clear();
            currentCard = null;

            string manifestPath = Path.Combine(selectedFolderPath, "AppxManifest.xml");
            var publisherName = optionsService.GetPublisherName(PublisherNameBox.Text);
            var appName = optionsService.GetAppName(AppNameBox.Text, exePath);

            // Use manifestService for manifest generation
            bool manifestOk = await manifestService.EnsureManifestGenerated(cliService, selectedFolderPath, null, appName, publisherName, progressCards, currentCard);
            if (!manifestOk)
            {
                return;
            }

            StartProgressCard("Package App");

            var certPassword = optionsService.GetCertPassword();
            var certPath = optionsService.GetCertPath();

            // Only add --generate-cert if no certPath is provided
            string packageCmd = $"package \"{selectedFolderPath}\" --install-cert";
            if (string.IsNullOrEmpty(certPath))
            {
                packageCmd = $"package \"{selectedFolderPath}\" --generate-cert --install-cert";
            }
            if (!string.IsNullOrEmpty(publisherName))
            {
                packageCmd += $" --publisher \"{publisherName}\"";
            }
            if (!string.IsNullOrEmpty(appName))
            {
                packageCmd += $" --name \"{appName}\"";

            }
            if (!string.IsNullOrEmpty(certPath))
            {
                packageCmd += $" --cert \"{certPath}\"";
                if (!string.IsNullOrEmpty(certPassword))
                {
                    packageCmd += $" --cert-password \"{certPassword}\"";
                }
            }

            int exitCode;
            try
            {
                exitCode = await cliService.RunCliStandardizedAsync("Package App", packageCmd, selectedFolderPath, progressCards, currentCard, runAsAdmin: true);
                if (exitCode != 0)
                {
                    return;
                }
                MarkCurrentCardSuccess();
            }
            catch (Exception ex)
            {
                AddProgressSubItem($"Error: {ex.Message}");
                ShowErrorCard("❌ Error", ex.Message);
                return;
            }

            StartProgressCardWithMessages("✅ Success", "App packaged, signed, and certificate installed successfully!");

            // Output MSIX file name and certificate name in the progress card
            msixService.AddMsixOutputInfoToProgress(selectedFolderPath, progressCards);
        }

        // --- MSIX tab logic ---
        private void MsixDropZone_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.Handled = true;
        }

        private void MsixCancelExeButton_Click(object sender, RoutedEventArgs e)
        {
            msixPath = string.Empty;
            SelectedMsixName = null;
            MsixFileNamePanel.Visibility = Visibility.Collapsed;
            RegisterInstallButton.IsEnabled = false;
        }

        // --- MSIX certificate browse button logic ---
        private void MsixCertBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.List;
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(".pfx");
            picker.FileTypeFilter.Add(".cer");
            picker.FileTypeFilter.Add(".crt");
            picker.FileTypeFilter.Add(".pem");
            picker.FileTypeFilter.Add(".cert");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            _ = PickMsixCertFileAsync(picker);
        }

        private async Task PickMsixCertFileAsync(Windows.Storage.Pickers.FileOpenPicker picker)
        {
            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                MsixCertPicker.CertificatePath = file.Path;
            }
        }

        private async void RegisterInstallButton_ClickAsync(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(msixPath) || !File.Exists(msixPath))
            {
                ShowErrorCard("❌ Error", "No MSIX file selected.");
                return;
            }
            progressCards.Clear();
            currentCard = null;

            // Get cert path and password from MSIX tab controls
            var certPath = string.IsNullOrWhiteSpace(MsixCertPicker.CertificatePath) ? null : MsixCertPicker.CertificatePath;
            string certPassword = MsixCertPicker.CertificatePassword;
            await msixService.HandleMsixDropAsync(msixPath, certPath, certPassword, progressCards, currentCard);
        }

        private void DotNetDropZone_Drop(object sender, DragEventArgs e)
        {
            // Clear previous progress cards
            progressCards.Clear();
            currentCard = null;
            exePath = string.Empty;
            selectedFolderPath = null;
            SelectedExeName = null;
            BindExeName();
            ShowCertPicker(false);
            try
            {
                if (e.DataView.Contains(StandardDataFormats.StorageItems))
                {
                    var itemsTask = e.DataView.GetStorageItemsAsync().AsTask();
                    itemsTask.Wait();
                    var items = itemsTask.Result;
                    foreach (var item in items)
                    {
                        if (item is StorageFile file && file.FileType == ".exe")
                        {
                            SetExe(file.Path);
                            selectedFolderPath = null;
                            StartProgressCardWithMessages("✅ EXE Detection", $"Detected EXE: {exePath}");
                            AddIdentityButton.IsEnabled = true;
                            PackageAppButton.IsEnabled = false;
                            DotNetFileNamePanel.Visibility = SelectedExeName != null ? Visibility.Visible : Visibility.Collapsed;
                            DotNetFileNamePanel.FileName = SelectedExeName ?? "";
                            ShowCertPicker(false);
                            AppNameBox.IsEnabled = true;
                            PublisherNameBox.IsEnabled = true;
                            return;
                        }
                        else if (item is StorageFolder folder)
                        {
                            selectedFolderPath = folder.Path;
                            exePath = string.Empty;
                            SelectedExeName = Path.GetFileName(folder.Path);
                            BindExeName();
                            StartProgressCardWithMessages("✅ Folder Detection", $"Detected folder for packaging: {selectedFolderPath}");
                            AddIdentityButton.IsEnabled = false;
                            PackageAppButton.IsEnabled = true;
                            DotNetFileNamePanel.Visibility = SelectedExeName != null ? Visibility.Visible : Visibility.Collapsed;
                            DotNetFileNamePanel.FileName = SelectedExeName ?? "";
                            ShowCertPicker(true);
                            AppNameBox.IsEnabled = true;
                            PublisherNameBox.IsEnabled = true;
                            return;
                        }
                    }
                    ShowErrorAndResetUI("File/Folder Detection", "❌ Please drop a valid .exe file or folder.");
                    return;
                }
            }
            catch (Exception ex)
            {
                ShowErrorAndResetUI("❌ Error", $"Exception: {ex.Message}");
            }
        }

        // --- Helper Methods for Logging and Manifest Common Code ---
        private void ShowErrorAndResetUI(string title, params string[] messages)
        {
            ShowErrorCard(title, messages);
            AddIdentityButton.IsEnabled = false;
            PackageAppButton.IsEnabled = false;
            DotNetFileNamePanel.Visibility = Visibility.Collapsed;
        }

        private void ResetUIState()
        {
            SetExe(null);
            selectedFolderPath = null;
            DotNetFileNamePanel.Visibility = Visibility.Collapsed;
            AddIdentityButton.IsEnabled = false;
            PackageAppButton.IsEnabled = false;
        }


        // --- Python Tab Logic ---
        private void PythonDropZone_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.Handled = true;
        }

        private void PythonDropZone_Drop(object sender, DragEventArgs e)
        {
            progressCards.Clear();
            currentCard = null;
            exePath = string.Empty;
            selectedFolderPath = null;
            SelectedExeName = null;
            BindExeName();
            ShowCertPicker(false);
            try
            {
                if (e.DataView.Contains(StandardDataFormats.StorageItems))
                {
                    var itemsTask = e.DataView.GetStorageItemsAsync().AsTask();
                    itemsTask.Wait();
                    var items = itemsTask.Result;
                    var result = pythonAppService.HandlePythonDrop(items, progressCards);
                    if (result != null && result.ExePath != null)
                    {
                        exePath = result.ExePath;
                        selectedFolderPath = result.SelectedFolderPath;
                        SelectedExeName = result.SelectedExeName;
                        currentCard = result.CurrentCard;
                        BindExeName();
                        PythonFileNamePanel.Visibility = SelectedExeName != null ? Visibility.Visible : Visibility.Collapsed;
                        PythonFileNamePanel.FileName = SelectedExeName ?? "";
                        AddIdentityButton.IsEnabled = result.AddIdentityButtonEnabled;
                        PackageAppButton.IsEnabled = result.PackageAppButtonEnabled;
                        AppNameBox.IsEnabled = result.AppNameBoxEnabled;
                        PublisherNameBox.IsEnabled = result.PublisherNameBoxEnabled;
                        ShowCertPicker(result.ShowCertPicker);
                    }
                    else
                    {
                        ShowErrorAndResetUI("File/Folder Detection", "❌ Please drop a valid .py file or folder.");
                    }
                }
            }
            catch (Exception ex)
            {
                ShowErrorAndResetUI("❌ Error", $"Exception: {ex.Message}");
            }
        }

        private void AppTypePivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            progressCards.Clear();
            // Update selectedAppType based on selected tab
            if (AppTypePivot.SelectedIndex == 0)
                selectedAppType = AppType.DotNet;
            else if (AppTypePivot.SelectedIndex == 1)
                selectedAppType = AppType.Python;
            else if (AppTypePivot.SelectedIndex == 2)
                selectedAppType = AppType.Msix;
        }

        private void DotNetFileNamePanel_CancelClicked(object sender, RoutedEventArgs e)
        {
            exePath = string.Empty;
            SelectedExeName = null;
            DotNetFileNamePanel.Visibility = Visibility.Collapsed;
            progressCards.Clear();
            AddIdentityButton.IsEnabled = false;
            PackageAppButton.IsEnabled = false;
        }

        private void PythonFileNamePanel_CancelClicked(object sender, RoutedEventArgs e)
        {
            exePath = string.Empty;
            SelectedExeName = null;
            PythonFileNamePanel.Visibility = Visibility.Collapsed;
            progressCards.Clear();
            AddIdentityButton.IsEnabled = false;
            PackageAppButton.IsEnabled = false;
        }

        private void MsixFileNamePanel_CancelClicked(object sender, RoutedEventArgs e)
        {
            msixPath = string.Empty;
            SelectedMsixName = null;
            MsixFileNamePanel.Visibility = Visibility.Collapsed;
            progressCards.Clear();
            RegisterInstallButton.IsEnabled = false;
        }

        private void DotNetClearOptionalButton_Click(object sender, RoutedEventArgs e)
        {
            PublisherNameBox.Text = "";
            AppNameBox.Text = "";
            optionsService.SetCertPath(null);
            DotNetCertPicker.CertificatePath = "";
            DotNetCertPicker.CertificatePassword = "password";
        }

        private void PythonClearOptionalButton_Click(object sender, RoutedEventArgs e)
        {
            PythonPublisherNameBox.Text = "";
            PythonAppNameBox.Text = "";
            optionsService.SetCertPath(null);
            PythonCertPicker.CertificatePath = "";
            PythonCertPicker.CertificatePassword = "password";
        }

        private void MSIXClearOptionalButton_Click(object sender, RoutedEventArgs e)
        {
            optionsService.SetCertPath(null);
            MsixCertPicker.CertificatePath = "";
            MsixCertPicker.CertificatePassword = "password";
        }
    }
}
