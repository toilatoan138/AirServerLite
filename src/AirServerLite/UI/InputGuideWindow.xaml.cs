using System.Windows;
using System.Windows.Media;

namespace AirServerLite.UI;

public partial class InputGuideWindow : Window
{
    private readonly List<string> _probeUrls;
    private readonly Func<Task<bool>> _onRetry;

    public InputGuideWindow(IEnumerable<string> probeUrls, Func<Task<bool>> onRetry)
    {
        InitializeComponent();
        _probeUrls = probeUrls.ToList();
        _onRetry = onRetry;

        var urlList = string.Join("\n• ", _probeUrls);
        TxtProbeSummary.Text = $"Đã dò tìm:\n• {urlList}\n(Không có dịch vụ WDA phản hồi trên cổng 8100)";
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e)
    {
        BtnRetry.IsEnabled = false;
        TxtStatus.Text = "Đang dò tìm cổng WDA (8100)...";
        TxtStatus.Foreground = (Brush)FindResource("Accent");

        try
        {
            var success = await _onRetry();
            if (success)
            {
                TxtStatus.Text = "✓ Đã tìm thấy và kết nối WDA thành công!";
                TxtStatus.Foreground = (Brush)FindResource("Good");
                await Task.Delay(500);
                DialogResult = true;
                Close();
                return;
            }
            else
            {
                TxtStatus.Text = "✗ Không tìm thấy WDA. Hãy bật WDA hoặc dùng Bluetooth (Cách 1).";
                TxtStatus.Foreground = (Brush)FindResource("Bad");
            }
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "Lỗi khi dò tìm: " + ex.Message;
            TxtStatus.Foreground = (Brush)FindResource("Bad");
        }
        finally
        {
            BtnRetry.IsEnabled = true;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
