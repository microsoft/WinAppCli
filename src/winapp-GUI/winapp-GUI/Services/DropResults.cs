using winapp_GUI;
using winapp_GUI.Services;

public class DropResult
{
    public string ExePath { get; set; } = string.Empty;
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

public class MsixDropResult
{
    public string MsixPath { get; set; } = string.Empty;
    public string? SelectedMsixName { get; set; }
    public ProgressCard? CurrentCard { get; set; }
    public bool MsixFileNamePanelVisible { get; set; }
    public bool RegisterInstallButtonEnabled { get; set; }
}
