using System.Collections.Generic;
using System.IO;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace winapp_GUI.Services;

public class PackageOptionsService
{
    public string? CertPath { get; private set; }
    public string CertPassword { get; private set; } = "password";
    private StackPanel? certPickerPanel;
    private TextBox? certPathBox;
    private TextBox? certPasswordBox;

    public void InitializeCertPicker(StackPanel certPickerPanel, TextBox certPathBox, TextBox certPasswordBox)
    {
        this.certPickerPanel = certPickerPanel;
        this.certPathBox = certPathBox;
        this.certPasswordBox = certPasswordBox;
        if (certPasswordBox != null)
        {
            certPasswordBox.Text = "password";
            certPasswordBox.IsEnabled = false;
        }
    }

    public void ShowCertPicker(bool show)
    {
        if (certPickerPanel != null)
        {
            certPickerPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show)
            {
                CertPath = null;
                if (certPathBox != null) certPathBox.Text = "";
                if (certPasswordBox != null)
                {
                    certPasswordBox.Text = "password";
                    certPasswordBox.IsEnabled = false;
                }
            }
        }
    }

    public void SetCertPath(string? path)
    {
        CertPath = path;
        if (certPathBox != null) certPathBox.Text = path ?? "";
        if (certPasswordBox != null)
        {
            certPasswordBox.IsEnabled = !string.IsNullOrWhiteSpace(path);
            if (string.IsNullOrWhiteSpace(path))
            {
                certPasswordBox.Text = "password";
            }
        }
    }

    public void SetCertPassword(string? password)
    {
        CertPassword = string.IsNullOrWhiteSpace(password) ? "password" : password!;
        if (certPasswordBox != null) certPasswordBox.Text = CertPassword;
    }

    public string? GetCertPassword()
    {
        if (certPasswordBox != null && certPasswordBox.IsEnabled)
        {
            return string.IsNullOrWhiteSpace(certPasswordBox.Text) ? "password" : certPasswordBox.Text;
        }
        return null;
    }

    public string? GetCertPath()
    {
        if (certPathBox != null)
        {
            return string.IsNullOrWhiteSpace(certPathBox.Text) ? null : certPathBox.Text;
        }
        return CertPath;
    }

    public string? GetPublisherName(string? input)
    {
        return string.IsNullOrWhiteSpace(input) ? null : input.Trim();
    }

    public string? GetAppName(string? input, string? exePath = null)
    {
        return string.IsNullOrWhiteSpace(input) ? null : input.Trim();
    }
}
