using MauiRemoteAdb.Services;
#if ANDROID
using MauiRemoteAdb.Platforms.Android;
using Android.Content.PM;
#endif

namespace MauiRemoteAdb.Views;

public partial class RemoteScreenPage : ContentPage
{
    private readonly RemoteSessionManager _session;
    private double _rotationDegrees;
    private int _lastVideoWidth;
    private int _lastVideoHeight;
    private bool _hasAutoDetectedRotation;  // Flag để tránh re-trigger auto rotation
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
            // Không gọi AutoDetectRotation ở đây để tránh dao động
            // AutoDetectRotation chỉ được gọi từ OnSurfaceReady và OnResolutionChanged
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
        UpdateButtonBarLayout(); // đặt đúng layout ngay từ đầu, không chỉ chờ SizeChanged bắn ra
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        CleanupOnDisappearing();
        // Khôi phục orientation về portrait khi rời khỏi trang
        SetOrientation(DisplayOrientation.Portrait);
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
    /// Tự động xoay app khi phát hiện video ở landscape để tận dụng toàn bộ màn hình.
    /// Chỉ gọi một lần lúc đầu kết nối, không gọi lại khi layout thay đổi để tránh dao động.
    /// </summary>
    private void AutoDetectRotation(int videoW, int videoH)
    {
        if (SurfaceContainer.Width <= 0 || SurfaceContainer.Height <= 0) return;

        // Nếu đã auto detect rồi thì không làm lại
        if (_hasAutoDetectedRotation) return;

        bool videoIsLandscape = videoW > videoH;
        bool containerIsLandscape = SurfaceContainer.Width > SurfaceContainer.Height;

        // Nếu video ở landscape nhưng container ở portrait, cần xoay app
        if (videoIsLandscape && !containerIsLandscape)
        {
            // Video ngang nhưng app dọc → xoay app sang landscape
            _rotationDegrees = 0; // Không cần xoay video vì app đã xoay rồi
            Surface.Rotation = _rotationDegrees;
            SetOrientation(DisplayOrientation.Landscape);
            _hasAutoDetectedRotation = true;
        }
        else if (!videoIsLandscape && containerIsLandscape)
        {
            // Video dọc nhưng app ngang → xoay app về portrait
            _rotationDegrees = 0;
            Surface.Rotation = _rotationDegrees;
            SetOrientation(DisplayOrientation.Portrait);
            _hasAutoDetectedRotation = true;
        }
        else
        {
            // Video và app cùng hướng → không cần xoay, nhưng vẫn mark là đã detect
            _rotationDegrees = 0;
            Surface.Rotation = _rotationDegrees;
            _hasAutoDetectedRotation = true;
        }

        _lastVideoWidth = videoW;
        _lastVideoHeight = videoH;
    }

    /// <summary>
    /// Chuyển thanh nút giữa 2 dạng: ngang ở dưới (khi app đang portrait) hoặc dọc bên
    /// cạnh phải (khi app đang landscape) — tận dụng tối đa diện tích hiển thị video khi
    /// xoay ngang, thay vì để thanh nút ngang chiếm mất 1 dải ngang phía dưới.
    /// </summary>
    private void UpdateButtonBarLayout()
    {
        bool isLandscape = RootGrid.Width > RootGrid.Height;

        if (isLandscape)
        {
            Grid.SetRow(ButtonBar, 0);
            Grid.SetColumn(ButtonBar, 1);
            ButtonBar.Direction = Microsoft.Maui.Layouts.FlexDirection.Column;
        }
        else
        {
            Grid.SetRow(ButtonBar, 1);
            Grid.SetColumn(ButtonBar, 0);
            ButtonBar.Direction = Microsoft.Maui.Layouts.FlexDirection.Row;
        }
    }

    private DisplayOrientation GetCurrentOrientation()
    {
        #if ANDROID
        var activity = Platform.CurrentActivity as Android.App.Activity;
        if (activity != null)
        {
            return activity.RequestedOrientation == ScreenOrientation.Landscape
                ? DisplayOrientation.Landscape
                : DisplayOrientation.Portrait;
        }
        #endif
        return DisplayOrientation.Unknown;
    }

    private void SetOrientation(DisplayOrientation orientation)
    {
        #if ANDROID
        var activity = Platform.CurrentActivity as Android.App.Activity;
        if (activity != null)
        {
            activity.RequestedOrientation = orientation == DisplayOrientation.Landscape
                ? ScreenOrientation.Landscape
                : ScreenOrientation.Portrait;
        }
        #endif
    }

    private void OnResolutionChanged(object? sender, (int Width, int Height) size)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // Ghi nhận resolution mới từ server
            // (không dùng DeviceMeta.Width/Height trực tiếp vì DeviceMeta là read-only property của session)
            // Giữ trong biến _lastVideoWidth/Height để OnSurfaceReady() có thể lấy khi reconfigure decoder
            System.Diagnostics.Debug.WriteLine(
                $"[OnResolutionChanged] Server báo resolution mới: {size.Width}x{size.Height}");

            // Chỉ auto-detect lần đầu tiên, không gọi lại khi video resize
            if (!_hasAutoDetectedRotation)
            {
                AutoDetectRotation(size.Width, size.Height);
            }
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

            AutoDetectRotation(videoW, videoH);
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
    private void RecreateDecoder(global::Android.Views.Surface surface, int width, int height)
    {
        System.Diagnostics.Debug.WriteLine($"[RemoteScreenPage] Tạo decoder {width}x{height}");

        var newDecoder = new VideoDecoder();
        newDecoder.Configure(surface, width, height, _session.LastConfigPacket);

        var old = _decoder;
        _decoder = newDecoder;
        old?.Dispose();

        _ = _session.RequestKeyFrameAsync();
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
