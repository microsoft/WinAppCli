using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using winapp_GUI;

namespace winapp_GUI.Services
{
    public class NetAppService
    {
        private readonly CliService cliService;
        private readonly ManifestService manifestService;
        private readonly PackageOptionsService optionsService;

        public NetAppService(CliService cliService, ManifestService manifestService, PackageOptionsService optionsService)
        {
            this.cliService = cliService;
            this.manifestService = manifestService;
            this.optionsService = optionsService;
        }

        // Helper: Traverse upwards to find nearest .csproj
        private string? FindNearestCsprojDirectory(string startPath)
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(startPath) ?? "");
            while (dir != null)
            {
                var csproj = dir.GetFiles("*.csproj").FirstOrDefault();
                if (csproj != null)
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            return null;
        }

        public async Task<bool> AddIdentityAsync(string exePath, ObservableCollection<ProgressCard> progressCards, ProgressCard? currentCard, string? publisherName, string? appName)
        {

            string exeFolder = Path.GetDirectoryName(exePath) ?? "";
            string exeName = Path.GetFileName(exePath);

            // Find nearest .csproj directory
            string? csprojDir = FindNearestCsprojDirectory(exePath);
            if (csprojDir == null)
            {
                // Fallback to exeFolder if no .csproj found
                csprojDir = exeFolder;
            }

            // Use csprojDir for manifest generation
            bool manifestOk = await manifestService.EnsureManifestGenerated(cliService, csprojDir, exePath, appName, publisherName, progressCards, currentCard);
            if (!manifestOk)
            {
                return false;
            }

            // Add debug identity
            string debugCmd = $"create-debug-identity \"{exePath}\"";
            int debugExit = await cliService.RunCliStandardizedAsync("Create Debug Identity", debugCmd, csprojDir, progressCards, currentCard);
            return debugExit == 0;
        }
    }

    public class DropResult
    {
        public string? ExePath { get; set; }
        public string? SelectedFolderPath { get; set; }
        public string? SelectedExeName { get; set; }
        public ProgressCard? CurrentCard { get; set; }
        public bool DotNetFileNamePanelVisible { get; set; }
        public bool AddIdentityButtonEnabled { get; set; }
        public bool PackageAppButtonEnabled { get; set; }
        public bool AppNameBoxEnabled { get; set; }
        public bool PublisherNameBoxEnabled { get; set; }
        public bool ShowCertPicker { get; set; }
    }
}
