using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace winapp_GUI.Controls
{
    public sealed partial class FileNamePanelControl : UserControl
    {
        public FileNamePanelControl()
        {
            this.InitializeComponent();
        }

        public string FileName
        {
            get => FileNameText.Text;
            set => FileNameText.Text = value;
        }

        public event RoutedEventHandler CancelClicked
        {
            add { CancelButton.Click += value; }
            remove { CancelButton.Click -= value; }
        }
    }
}
