using MauiRemoteAdb.Services;
#if ANDROID
using MauiRemoteAdb.Platforms.Android;
using Android.Content.PM;
#endif

namespace MauiRemoteAdb.Views;

public partial class RemoteScreenPage : ContentPage
{
    private readonly RemoteSessionManager _session;
    // Hướng video hiện tại của máy B (null = chưa biết). App A xoay theo MỖI LẦN giá trị này đổi.
    private bool? _videoIsLandscape;
#if ANDROID
    // Hướng màn hình app trước khi vào trang này — khôi phục lại khi thoát.
    private ScreenOrientation? _orientationBeforePage;
#endif
#if ANDROID
    private volatile VideoDecoder? _decoder; // đọc từ thread socket, ghi từ UI thread
    private global::Android.Views.Surface? _lastSurface;
#endif

    public RemoteScreenPage(RemoteSessionManager session)
    {
        InitializeComponent();
        _session = session;

        // Ẩn thanh tiêu đề trên cùng (mũi tên Back + chữ "Remote Screen") để tối đa diện
        // tích hiển thị video — dùng nút Back vật lý/hệ thống của thiết bị A để thoát
        // trang thay thế (xem OnBackButtonPressed bên dưới).
        NavigationPage.SetHasNavigationBar(this, false);

        Surface.SurfaceReady += OnSurfaceReady;
        Surface.SurfaceDestroyed += OnSurfaceDestroyed;
        Surface.Touched += OnSurfaceTouched;
        _session.FrameReceived += OnFrameReceived;
        _session.ResolutionChanged += OnResolutionChanged;
        SurfaceContainer.SizeChanged += (_, _) =>
        {
            // Chỉ tính lại kích thước hiển thị — việc xoay app do FollowVideoOrientation
            // quyết định (gọi từ OnSurfaceReady/OnResolutionChanged), tránh dao động.
            UpdateSurfaceAspectRatio();
        };
        RootGrid.SizeChanged += (_, _) => UpdateButtonBarLayout();
    }

    /// <summary>
    /// Vì đã ẩn thanh tiêu đề (không còn mũi tên Back trên UI), đảm bảo nút Back vật lý/
    /// hệ thống của thiết bị A vẫn thoát trang đúng cách (quay lại ConnectPage) — mặc
    /// định MAUI/Android đã tự làm việc này khi back button được nhấn trong 1
    /// NavigationPage, override ở đây chỉ để chắc chắn hành vi rõ ràng, tường minh.
    /// </summary>
    protected override bool OnBackButtonPressed()
    {
        Navigation.PopAsync();
        return true; // đã tự xử lý, không cần hành vi mặc định khác xử lý thêm
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
#if ANDROID
        if (Platform.CurrentActivity is Android.App.Activity activity)
            _orientationBeforePage ??= activity.RequestedOrientation;
#endif
        UpdateButtonBarLayout(); // đặt đúng layout ngay từ đầu, không chỉ chờ SizeChanged bắn ra
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        CleanupOnDisappearing();
#if ANDROID
        // Khôi phục đúng hướng màn hình như trước khi vào trang (không ép cứng Portrait)
        if (Platform.CurrentActivity is Android.App.Activity activity)
            activity.RequestedOrientation = _orientationBeforePage ?? ScreenOrientation.Unspecified;
#endif
    }

    /// <summary>
    /// SurfaceView mặc định bị kéo giãn lấp đầy toàn bộ vùng chứa (Grid Row), làm méo
    /// hình nếu tỉ lệ khung hình thật của thiết bị B khác tỉ lệ vùng hiển thị trên app.
    /// Tính lại kích thước đúng theo tỉ lệ gốc (letterbox — có thể có viền đen 2 bên
    /// hoặc trên/dưới, còn hơn hình bị méo) rồi căn giữa.
    /// </summary>
    private void UpdateSurfaceAspectRatio()
    {
        if (_session.DeviceMeta is null) return;
        if (SurfaceContainer.Width <= 0 || SurfaceContainer.Height <= 0) return;

        double videoW = _session.DeviceMeta.Width;
        double videoH = _session.DeviceMeta.Height;
        if (videoW <= 0 || videoH <= 0) return;

        double availW = SurfaceContainer.Width;
        double availH = SurfaceContainer.Height;

        double scale = Math.Min(availW / videoW, availH / videoH);

        // Đặt kích thước để giữ tỉ lệ khung hình gốc
        Surface.WidthRequest = videoW * scale;
        Surface.HeightRequest = videoH * scale;
    }

    /// <summary>
    /// Xoay app A theo hướng video của máy B — gọi MỖI KHI kích thước video đổi (lúc kết nối
    /// và mỗi lần máy B xoay), không chỉ 1 lần như bản cũ. Chỉ xoay khi hướng THỰC SỰ đổi
    /// (dọc ↔ ngang) nên không bị dao động khi layout thay đổi.
    /// Dùng Sensor* để vẫn xoay 180° theo cách người dùng cầm máy (không bị lộn ngược).
    /// </summary>
    private void FollowVideoOrientation(int videoW, int videoH)
    {
        if (videoW <= 0 || videoH <= 0) return;
        bool landscape = videoW > videoH;
        if (_videoIsLandscape == landscape) return;
        _videoIsLandscape = landscape;
        SetOrientation(landscape ? DisplayOrientation.Landscape : DisplayOrientation.Portrait);
    }

    /// <summary>
    /// Chuyển thanh nút giữa 2 dạng: ngang ở dưới (khi app đang portrait) hoặc dọc bên
    /// cạnh phải (khi app đang landscape) — tận dụng tối đa diện tích hiển thị video khi
    /// xoay ngang, thay vì để thanh nút ngang chiếm mất 1 dải ngang phía dưới.
    /// </summary>
    private void UpdateButtonBarLayout()
    {
        bool isLandscape = RootGrid.Width > RootGrid.Height;

        // Đổi kích thước nút theo hướng để thanh nút chỉ dày 36dp ở cả 2 dạng:
        // dọc -> nút 56x36 (bấm dễ theo chiều ngang), ngang -> nút 36x56.
        double thick = 36, wide = 56;
        if (isLandscape)
        {
            Grid.SetRow(ButtonBar, 0);
            Grid.SetColumn(ButtonBar, 1);
            ButtonBar.Direction = Microsoft.Maui.Layouts.FlexDirection.Column;
            ButtonBar.WidthRequest = thick;
            ButtonBar.HeightRequest = -1;
        }
        else
        {
            Grid.SetRow(ButtonBar, 1);
            Grid.SetColumn(ButtonBar, 0);
            ButtonBar.Direction = Microsoft.Maui.Layouts.FlexDirection.Row;
            ButtonBar.WidthRequest = -1;
            ButtonBar.HeightRequest = thick;
        }
        foreach (var child in ButtonBar.Children)
        {
            if (child is Button b)
            {
                b.WidthRequest = isLandscape ? thick : wide;
                b.HeightRequest = isLandscape ? wide : thick;
            }
        }
    }

    private void SetOrientation(DisplayOrientation orientation)
    {
        #if ANDROID
        var activity = Platform.CurrentActivity as Android.App.Activity;
        if (activity != null)
        {
            activity.RequestedOrientation = orientation == DisplayOrientation.Landscape
                ? ScreenOrientation.SensorLandscape
                : ScreenOrientation.SensorPortrait;
        }
        #endif
    }

    private void OnResolutionChanged(object? sender, (int Width, int Height) size)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            System.Diagnostics.Debug.WriteLine(
                $"[OnResolutionChanged] Server báo resolution mới: {size.Width}x{size.Height}");

            FollowVideoOrientation(size.Width, size.Height);
            UpdateSurfaceAspectRatio();

#if ANDROID
            // Thiết bị B xoay màn hình -> encoder khởi động lại với kích thước mới.
            // Tạo lại decoder theo kích thước mới (bản cũ không làm việc này => hình méo/đen sau khi xoay).
            if (_lastSurface is not null && _decoder is not null &&
                (_decoder.Width != size.Width || _decoder.Height != size.Height))
            {
                RecreateDecoder(_lastSurface, size.Width, size.Height);
            }
#endif
        });
    }

    private void OnSurfaceReady(object? sender, SurfaceReadyEventArgs e)
    {
#if ANDROID
        try
        {
            // Page chỉ được push SAU khi StartAsync() xong nên DeviceMeta luôn có ở đây.
            if (_session.DeviceMeta is null) return;

            var androidSurface = (global::Android.Views.Surface)e.PlatformSurface;
            int videoW = _session.DeviceMeta.Width;
            int videoH = _session.DeviceMeta.Height;

            bool surfaceChanged = !ReferenceEquals(_lastSurface, androidSurface);
            bool sizeChanged = _decoder is null || _decoder.Width != videoW || _decoder.Height != videoH;

            // SurfaceChanged cũng bắn ra khi chỉ đổi kích thước layout (letterbox) —
            // lúc đó MediaCodec tự scale, KHÔNG cần tạo lại decoder.
            if (!surfaceChanged && !sizeChanged)
                return;

            _lastSurface = androidSurface;
            RecreateDecoder(androidSurface, videoW, videoH);

            FollowVideoOrientation(videoW, videoH);
            MainThread.BeginInvokeOnMainThread(UpdateSurfaceAspectRatio);
        }
        catch (Exception ex)
        {
            // KHÔNG để exception thoát khỏi event handler này — trên Android, exception
            // chưa bắt trong 1 callback native (surface callback) có thể làm tiến trình
            // bị kill ngay lập tức mà không hiện thông báo gì ("văng app không rõ lý do").
            System.Diagnostics.Debug.WriteLine($"[OnSurfaceReady] Exception: {ex}");
            MainThread.BeginInvokeOnMainThread(() =>
                DisplayAlert("Lỗi khởi tạo decoder", ex.ToString(), "Đóng"));
        }
#endif
    }

#if ANDROID
    /// <summary>
    /// Tạo decoder mới (kèm SPS/PPS đã cache), gắn vào trước rồi mới hủy cái cũ, sau đó
    /// yêu cầu server gửi lại keyframe ngay — có hình gần như tức thì thay vì chờ tới
    /// keyframe định kỳ (10 giây) hoặc đen màn vĩnh viễn nếu đã lỡ mất SPS/PPS.
    /// </summary>
    private void RecreateDecoder(global::Android.Views.Surface surface, int width, int height, int attempt = 1)
    {
        System.Diagnostics.Debug.WriteLine($"[RemoteScreenPage] Tạo decoder {width}x{height} (lần {attempt})");

        // QUAN TRỌNG: phải HỦY decoder cũ TRƯỚC rồi mới tạo cái mới. 1 Surface chỉ cho
        // đúng 1 MediaCodec gắn vào tại 1 thời điểm — tạo cái mới khi cái cũ còn gắn thì
        // MediaCodec.Configure() ném IllegalArgumentException (lỗi khi xoay màn hình).
        // Gán _decoder = null trước để thread socket bỏ qua frame trong lúc chuyển đổi.
        var old = _decoder;
        _decoder = null;
        old?.Dispose();

        if (!surface.IsValid)
        {
            // Surface đang bị hủy/tạo lại (đang xoay) — SurfaceChanged kế tiếp sẽ gọi lại OnSurfaceReady
            System.Diagnostics.Debug.WriteLine("[RemoteScreenPage] Surface chưa hợp lệ, chờ SurfaceChanged");
            return;
        }

        var decoder = new VideoDecoder();
        try
        {
            decoder.Configure(surface, width, height, _session.LastConfigPacket);
            _decoder = decoder;
            _ = _session.RequestKeyFrameAsync();
        }
        catch (Exception ex)
        {
            decoder.Dispose();
            System.Diagnostics.Debug.WriteLine($"[RemoteScreenPage] Configure decoder lỗi (lần {attempt}): {ex.Message}");
            // Surface có thể chưa được decoder cũ "nhả" kịp — thử lại sau chốc lát thay vì báo lỗi ngay
            if (attempt < 3)
            {
                Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(300), () =>
                {
                    if (ReferenceEquals(_lastSurface, surface) && _decoder is null && _session.DeviceMeta is not null)
                        RecreateDecoder(surface, _session.DeviceMeta.Width, _session.DeviceMeta.Height, attempt + 1);
                });
            }
            else
            {
                DisplayAlert("Lỗi khởi tạo decoder", ex.ToString(), "Đóng");
            }
        }
    }

    private void OnSurfaceDestroyed(object? sender, EventArgs e)
    {
        // Surface đã chết -> decoder render vào đó sẽ lỗi. Hủy luôn; khi Surface mới
        // được tạo (SurfaceChanged), OnSurfaceReady sẽ tạo decoder mới + xin keyframe.
        var old = _decoder;
        _decoder = null;
        _lastSurface = null;
        old?.Dispose();
    }
#else
    private void OnSurfaceDestroyed(object? sender, EventArgs e) { }
#endif

    private void OnFrameReceived(object? sender, VideoFrame frame)
    {
#if ANDROID
        try
        {
            _decoder?.Feed(frame);
        }
        catch (Exception ex)
        {
            // Lỗi decode 1 frame không nên làm sập cả app — chỉ log, bỏ qua frame đó,
            // các frame sau vẫn tiếp tục thử decode bình thường.
            System.Diagnostics.Debug.WriteLine($"[VideoDecoder] Lỗi decode frame: {ex}");
        }
#endif
    }

    private async void OnSurfaceTouched(object? sender, RemoteTouchEventArgs e)
    {
        var action = e.Phase switch
        {
            TouchPhase.Down => AndroidMotionAction.Down,
            TouchPhase.Move => AndroidMotionAction.Move,
            TouchPhase.Up => AndroidMotionAction.Up,
            _ => AndroidMotionAction.Move,
        };
        await _session.SendTouchAsync(action, e.RelativeX, e.RelativeY);
    }

    // Android keycode: 4 = KEYCODE_BACK, 3 = KEYCODE_HOME, 187 = KEYCODE_APP_SWITCH
    private async void OnBackKeyClicked(object? sender, EventArgs e) => await SendTap(4);
    private async void OnHomeKeyClicked(object? sender, EventArgs e) => await SendTap(3);
    private async void OnRecentsKeyClicked(object? sender, EventArgs e) => await SendTap(187);

    private async Task SendTap(int keyCode)
    {
        await _session.SendKeyAsync(keyCode, AndroidMotionAction.Down);
        await _session.SendKeyAsync(keyCode, AndroidMotionAction.Up);
    }

    private async void CleanupOnDisappearing()
    {
        // Gỡ đăng ký khỏi session — tránh page cũ vẫn nhận frame/sự kiện sau khi đã đóng.
        _session.FrameReceived -= OnFrameReceived;
        _session.ResolutionChanged -= OnResolutionChanged;
#if ANDROID
        var old = _decoder;
        _decoder = null;
        old?.Dispose();
#endif
        await _session.StopAsync();
    }
}
