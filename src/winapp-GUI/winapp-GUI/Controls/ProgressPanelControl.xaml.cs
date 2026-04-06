using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using winapp_GUI;

namespace winapp_GUI.Controls
{
    public sealed partial class ProgressPanelControl : UserControl
    {
        public ProgressPanelControl()
        {
            this.InitializeComponent();
        }

        public ObservableCollection<ProgressCard> ProgressCards
        {
            get => (ObservableCollection<ProgressCard>)ProgressItemsControl.ItemsSource;
            set => ProgressItemsControl.ItemsSource = value;
        }
    }
}
