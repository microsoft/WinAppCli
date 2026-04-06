using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using Windows.Storage;
using winapp_GUI;

namespace winapp_GUI.Services
{
    public class MsixService
    {
        private readonly CliService cliService;
        private readonly ManifestService manifestService;
        private readonly PackageOptionsService optionsService;
        public MsixService(CliService cliService, ManifestService manifestService, PackageOptionsService optionsService)
        {
            this.cliService = cliService;
            this.manifestService = manifestService;
            this.optionsService = optionsService;
        }

        public (string AppName, string Publisher) ExtractIdentityFromMsix(string msixPath)
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(msixPath);
            var manifestEntry = archive.GetEntry("AppxManifest.xml");
            if (manifestEntry == null)
                throw new FileNotFoundException("AppxManifest.xml not found inside MSIX package.");
            using var manifestStream = manifestEntry.Open();
            var doc = XDocument.Load(manifestStream);
            var root = doc.Root ?? throw new InvalidDataException("Manifest XML root is null.");
            var ns = root.GetDefaultNamespace();
            var identity = root.Element(ns + "Identity");
            if (identity == null)
                throw new InvalidDataException("Identity element not found in AppxManifest.xml.");
            string appName = identity.Attribute("Name")?.Value ?? "UnknownApp";
            string publisher = identity.Attribute("Publisher")?.Value ?? "CN=UnknownPublisher";
            return (appName, publisher);
        }

        public X509Certificate2? LoadCertificate(string certPath, string password)
        {
            if (!File.Exists(certPath)) return null;
            try
            {
                return new X509Certificate2(certPath, password);
            }
            catch
            {
                return null;
            }
        }

        public void AddMsixOutputInfoToProgress(string folder, ObservableCollection<ProgressCard> progressCards)
        {
            var card = ProgressCard.Start("MSIX Output Info", progressCards);
            try
            {
                var msixFiles = Directory.GetFiles(folder, "*.msix", SearchOption.TopDirectoryOnly);
                if (msixFiles.Length > 0)
                {
                    card.AddSubItem($"MSIX file: {Path.GetFileName(msixFiles[0])}");
                }
                else
                {
                    card.AddSubItem("MSIX file not found in output folder.");
                }
            }
            catch (Exception ex)
            {
                card.AddSubItem($"Error locating output files: {ex.Message}");
            }
        }

        public MsixDropResult? HandleMsixDrop(IReadOnlyList<IStorageItem> items, ObservableCollection<ProgressCard> progressCards)
        {
            var result = new MsixDropResult();
            foreach (var item in items)
            {
                if (item is StorageFile file && file.FileType == ".msix")
                {
                    result.MsixPath = file.Path;
                    result.SelectedMsixName = Path.GetFileName(file.Path);
                    result.CurrentCard = ProgressCard.StartWithMessages("✅ MSIX Detection", progressCards, $"Detected MSIX: {file.Path}");
                    result.MsixFileNamePanelVisible = true;
                    result.RegisterInstallButtonEnabled = true;
                    return result;
                }
            }
            return null;
        }
        public async Task HandleMsixDropAsync(string msixPath, string? certPath = null, string? certPassword = null, ObservableCollection<ProgressCard>? progressCards = null, ProgressCard? currentCard=null)
        {
            var msixFolder = Path.GetDirectoryName(msixPath) ?? "";
            var certToUse = certPath;
            string appName, publisherName;
            try
            {
                (appName, publisherName) = ExtractIdentityFromMsix(msixPath);
            }
            catch (Exception ex)
            {
                ProgressCard.ShowError("❌ Error", progressCards, $"Failed to extract manifest from MSIX: {ex.Message}");
                return;
            }

            if (!string.IsNullOrEmpty(certToUse))
            {
                string cliCmd = $"cert install \"{certToUse}\"";
                if (!string.IsNullOrEmpty(certPassword))
                {
                    cliCmd += $" --password \"{certPassword}\"";
                }
                int installExit = await cliService.RunCliStandardizedAsync("Certificate Install", cliCmd, msixFolder, progressCards, currentCard, runAsAdmin: true);
                if (installExit != 0)
                {
                    return;
                }
            }
            else
            {
                int certExit = await cliService.RunCliStandardizedAsync("Certificate Creation", $"cert generate --publisher \"{publisherName}\" --output devcert.pfx", msixFolder, progressCards, currentCard);
                if (certExit != 0)
                {
                    return;
                }

                string autoCertPath = Path.Combine(msixFolder, "devcert.pfx");
                string cliCmd = $"cert install \"{autoCertPath}\"";
                if (!string.IsNullOrEmpty(certPassword))
                {
                    cliCmd += $" --password \"{certPassword}\"";
                }
                int installExit = await cliService.RunCliStandardizedAsync("Certificate Install", cliCmd, msixFolder, progressCards, currentCard, runAsAdmin: true);
                if (installExit != 0)
                {
                    return;
                }
                certToUse = autoCertPath;
            }

            var cert = LoadCertificate(certToUse ?? Path.Combine(msixFolder, "devcert.pfx"), certPassword ?? "password");

            ProgressCard.Start("✅ Success", progressCards);
        }
    }

    public class MsixDropResult
    {
        public string? MsixPath { get; set; }
        public string? SelectedMsixName { get; set; }
        public ProgressCard? CurrentCard { get; set; }
        public bool MsixFileNamePanelVisible { get; set; }
        public bool RegisterInstallButtonEnabled { get; set; }
    }
}
