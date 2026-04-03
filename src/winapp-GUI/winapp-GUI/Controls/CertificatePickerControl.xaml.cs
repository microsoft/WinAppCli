using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace winapp_GUI.Controls
{
    public sealed partial class CertificatePickerControl : UserControl
    {
        public CertificatePickerControl()
        {
            this.InitializeComponent();
        }

        public string CertificatePath
        {
            get => CertPathBox.Text;
            set => CertPathBox.Text = value;
        }

        public string CertificatePassword
        {
            get => CertPasswordBox.Text;
            set => CertPasswordBox.Text = value;
        }

        public event RoutedEventHandler BrowseClicked
        {
            add { CertBrowseButton.Click += value; }
            remove { CertBrowseButton.Click -= value; }
        }

        public string Label
        {
            get => CertLabel.Text;
            set => CertLabel.Text = value;
        }
    }
}
