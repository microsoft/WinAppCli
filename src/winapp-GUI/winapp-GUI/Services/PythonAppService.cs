using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using winapp_GUI;

namespace winapp_GUI.Services
{
    public class PythonAppService
    {
        private readonly CliService cliService;
        private readonly ManifestService manifestService;
        private readonly PackageOptionsService optionsService;

        public PythonAppService(CliService cliService, ManifestService manifestService, PackageOptionsService optionsService)
        {
            this.cliService = cliService;
            this.manifestService = manifestService;
            this.optionsService = optionsService;
        }

        public async Task<bool> AddIdentityAsync(string pyPath, ObservableCollection<ProgressCard> progressCards, ProgressCard? currentCard, string? publisherName, string? appName)
        {
            var pyFolder = Path.GetDirectoryName(pyPath);

            if (pyFolder == null)
            {
                ProgressCard.ShowError("Invalid Python Path", progressCards, "The provided Python script path is invalid.");
                return false;
            }

            bool manifestOk = await manifestService.EnsureManifestGenerated(cliService, pyFolder, pyPath, appName, publisherName, progressCards, currentCard, true);
            if (!manifestOk)
            {
                return false;
            }

            string debugCmd = $"create-debug-identity \"{pyPath}\"";
            int debugExit = await cliService.RunCliStandardizedAsync("Create Debug Identity", debugCmd, pyFolder, progressCards, currentCard);
            return debugExit == 0;
        }

        public DropResult HandlePythonDrop(IReadOnlyList<IStorageItem> items, ObservableCollection<ProgressCard> progressCards)
        {
            var result = new DropResult();
            foreach (var item in items)
            {
                if (item is StorageFile file && file.FileType == ".py")
                {
                    result.ExePath = file.Path;
                    result.SelectedFolderPath = null;
                    result.SelectedExeName = Path.GetFileName(file.Path);
                    result.CurrentCard = ProgressCard.StartWithMessages("✅ Python Script Detection", progressCards, $"Detected Python script: {file.Path}");
                    result.DotNetFileNamePanelVisible = false;
                    result.AddIdentityButtonEnabled = true;
                    result.PackageAppButtonEnabled = false;
                    result.AppNameBoxEnabled = false;
                    result.PublisherNameBoxEnabled = false;
                    result.ShowCertPicker = false;
                    // Ensure exePath is set and AddIdentityButton is enabled for Python
                    // This will be used by MainWindow to call AddIdentityAsync
                    return result;
                }
                else if (item is StorageFolder folder)
                {
                    result.SelectedFolderPath = folder.Path;
                    result.ExePath = string.Empty;
                    result.SelectedExeName = Path.GetFileName(folder.Path);
                    result.CurrentCard = ProgressCard.StartWithMessages("✅ Python Folder Detection", progressCards, $"Detected folder for packaging: {folder.Path}");
                    result.DotNetFileNamePanelVisible = false;
                    result.AddIdentityButtonEnabled = false;
                    result.PackageAppButtonEnabled = true;
                    result.AppNameBoxEnabled = false;
                    result.PublisherNameBoxEnabled = false;
                    result.ShowCertPicker = true;
                    return result;
                }
            }
            // If no valid .py file or folder, disable AddIdentityButton
            result.AddIdentityButtonEnabled = false;
            return result;
        }
    }
}
