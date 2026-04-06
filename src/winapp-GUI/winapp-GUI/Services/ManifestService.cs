using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace winapp_GUI.Services;

public class ManifestService
{
    public async Task<bool> EnsureManifestGenerated(CliService cliService, string folder, string? exeName = null, string? appName = null, string? publisherName = null, ObservableCollection<ProgressCard>? progressCards = null, ProgressCard? currentCard = null, bool? hostedApp = null)
    {
        string manifestPath = Path.Combine(folder, "AppxManifest.xml");
        if (!File.Exists(manifestPath))
        {
            var cards = progressCards ?? new ObservableCollection<ProgressCard>();
            var manifestCard = ProgressCard.Start("Manifest Generation", cards);

            var manifestCmd = $"manifest generate \"{folder}\" --yes";
            if (!string.IsNullOrEmpty(appName))
                manifestCmd += $" --package-name \"{appName}\"";
            if (!string.IsNullOrEmpty(publisherName))
                manifestCmd += $" --publisher-name \"{publisherName}\"";
            if (!string.IsNullOrEmpty(exeName))
                manifestCmd += $" --entrypoint \"{exeName}\"";
            if (hostedApp.HasValue && hostedApp.Value)
                manifestCmd += $" --template hostedapp";
            int exit = await cliService.RunCliStandardizedAsync("Manifest Generation", manifestCmd, folder, cards, manifestCard);
            if (exit == 0)
            {
                manifestCard.MarkSuccess();
                return true;
            }
            else
            {
                ProgressCard.ShowError("Manifest Generation Failed", cards, new string[] { "Could not generate manifest." });
                return false;
            }
        }
        // No progress card if manifest already exists
        return true;
    }
}
