using MauiRemoteAdb.Models;
using MauiRemoteAdb.Services;

namespace MauiRemoteAdb.Views;

public partial class ConnectPage : ContentPage
{
    private RemoteSessionManager? _session;
    private readonly ConnectionHistoryService _history = new();

    public ConnectPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        RefreshHistoryList();
    }

    private void RefreshHistoryList()
    {
        HistoryList.ItemsSource = _history.Load();
    }

    private async void OnConnectClicked(object? sender, EventArgs e)
    {
        await ConnectAsync(ConnectIpEntry.Text, ConnectPortEntry.Text);
    }

    /// <summary>Bấm vào 1 mục lịch sử — tự điền vào ô nhập rồi kết nối luôn, không cần bấm thêm nút Connect.</summary>
    private async void OnHistoryItemTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not ConnectionHistoryItem item) return;
        ConnectIpEntry.Text = item.Host;
        ConnectPortEntry.Text = item.Port;
        await ConnectAsync(item.Host, item.Port);
    }

    private void OnDeleteHistoryItemClicked(object? sender, EventArgs e)
    {
        if (sender is not Button button || button.CommandParameter is not ConnectionHistoryItem item) return;
        _history.Remove(item);
        RefreshHistoryList();
    }

    private async void OnClearHistoryClicked(object? sender, EventArgs e)
    {
        bool confirm = await DisplayAlert("Xóa hết lịch sử?", "Không thể hoàn tác.", "Xóa", "Hủy");
        if (!confirm) return;
        _history.Clear();
        RefreshHistoryList();
    }

    private async Task ConnectAsync(string? host, string? portText)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(host) || !int.TryParse(portText, out int port))
            {
                StatusLabel.Text = "Vui lòng nhập đúng IP/domain và port.";
                return;
            }

            // Tạo mới RemoteSessionManager mỗi lần connect để tránh tái sử dụng session cũ bị disconnect
            _session = new RemoteSessionManager();

            StatusLabel.Text = "Đang khởi động ADB server nội bộ...";
            await _session.Adb.EnsureLocalAdbServerStartedAsync();

            StatusLabel.Text = "Đang kết nối...";
            bool ok = await _session.Adb.ConnectAsync(host, port);
            if (!ok)
            {
                StatusLabel.Text = "Không kết nối được tới thiết bị.";
                return;
            }

            // Kết nối ADB thành công (bước quan trọng nhất) — lưu lại lịch sử ngay tại
            // đây, không cần chờ tới khi cả phiên scrcpy khởi động xong, vì bản thân
            // việc kết nối được ADB đã là thông tin hữu ích để lưu, kể cả nếu bước sau
            // (push server, mở socket video...) có lỡ thất bại.
            _history.AddOrUpdate(host, port.ToString());
            RefreshHistoryList();

            _session.StatusChanged += (_, msg) => MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = msg);

            await _session.StartAsync();

            await Navigation.PushAsync(new RemoteScreenPage(_session));
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Lỗi: {ex.Message}";
        }
    }
}
