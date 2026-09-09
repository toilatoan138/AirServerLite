using System.Windows;

namespace AirServerLite.UI;

public partial class AndroidConnectDialog : Window
{
    public AndroidConnectDialog()
    {
        InitializeComponent();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
