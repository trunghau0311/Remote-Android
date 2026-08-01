using Android.Content;
using Android.Views;
using AndroidX.AppCompat.Widget;
using Microsoft.Maui.Handlers;
using MauiRemoteAdb.Views;

namespace MauiRemoteAdb.Platforms.Android;

/// <summary>
/// Handler ánh xạ RemoteSurfaceView (cross-platform) sang global::Android.Views.SurfaceView thật.
/// Đây là "cửa sau" chuẩn của MAUI để dùng API native khi control có sẵn không đủ
/// (SurfaceView không có tương đương built-in trong MAUI vì nó đặc thù Android).
/// </summary>
public class RemoteSurfaceViewHandler : ViewHandler<RemoteSurfaceView, global::Android.Views.SurfaceView>
{
    public static readonly PropertyMapper<RemoteSurfaceView, RemoteSurfaceViewHandler> Mapper = new(ViewHandler.ViewMapper);

    private int _videoWidth;
    private int _videoHeight;

    public RemoteSurfaceViewHandler() : base(Mapper) { }

    protected override global::Android.Views.SurfaceView CreatePlatformView()
    {
        var surfaceView = new global::Android.Views.SurfaceView(Context);
        surfaceView.Touch += OnPlatformTouch;

        surfaceView.Holder!.AddCallback(new SurfaceCallback(this, surfaceView));
        return surfaceView;
    }

    private void OnPlatformTouch(object? sender, global::Android.Views.View.TouchEventArgs e)
    {
        if (sender is not global::Android.Views.SurfaceView sv || e.Event is null) return;

        // Nếu kích thước video chưa được đặt, không xử lý touch
        if (_videoWidth <= 0 || _videoHeight <= 0) return;

        // Lấy kích thước thực tế của SurfaceView (sau khi bị layout)
        int containerWidth = sv.Width;
        int containerHeight = sv.Height;

        if (containerWidth <= 0 || containerHeight <= 0) return;

        // Tính toán offset và kích thước hiển thị dựa trên layout thực tế
        double scale = Math.Min((double)containerWidth / _videoWidth, (double)containerHeight / _videoHeight);
        int displayWidth = (int)(_videoWidth * scale);
        int displayHeight = (int)(_videoHeight * scale);
        double offsetX = (containerWidth - displayWidth) / 2.0;
        double offsetY = (containerHeight - displayHeight) / 2.0;

        // Tọa độ chạm từ Android (tuyệt đối trên SurfaceView)
        float touchX = e.Event.GetX();
        float touchY = e.Event.GetY();

        // Kiểm tra xem chạm có nằm trong vùng video hiển thị hay không
        // (bỏ qua các phần letterbox - viền đen ở các cạnh)
        if (touchX < offsetX || touchY < offsetY ||
            touchX >= offsetX + displayWidth || touchY >= offsetY + displayHeight)
        {
            e.Handled = true;
            return;
        }

        // Tính tọa độ tương đối so với vị trí bắt đầu của video hiển thị
        float relativeX = (touchX - (float)offsetX) / displayWidth;
        float relativeY = (touchY - (float)offsetY) / displayHeight;

        // Clamp vào khoảng [0, 1]
        relativeX = Math.Clamp(relativeX, 0f, 1f);
        relativeY = Math.Clamp(relativeY, 0f, 1f);

        TouchPhase phase = e.Event.Action switch
        {
            MotionEventActions.Down => TouchPhase.Down,
            MotionEventActions.Move => TouchPhase.Move,
            MotionEventActions.Up => TouchPhase.Up,
            _ => TouchPhase.Move,
        };

        VirtualView.RaiseTouched(new RemoteTouchEventArgs(relativeX, relativeY, phase));
        e.Handled = true;
    }

    private class SurfaceCallback : Java.Lang.Object, ISurfaceHolderCallback
    {
        private readonly RemoteSurfaceViewHandler _handler;
        private readonly global::Android.Views.SurfaceView _view;
        public SurfaceCallback(RemoteSurfaceViewHandler handler, global::Android.Views.SurfaceView view)
        {
            _handler = handler;
            _view = view;
        }

        public void SurfaceCreated(ISurfaceHolder holder)
        {
            // Kích thước thật lấy được ở SurfaceChanged; ở đây chỉ báo "đã tạo"
        }

        public void SurfaceChanged(ISurfaceHolder holder, global::Android.Graphics.Format format, int width, int height)
        {
            _handler._videoWidth = width;
            _handler._videoHeight = height;
            _handler.VirtualView.RaiseSurfaceReady(holder.Surface!, width, height);
        }

        public void SurfaceDestroyed(ISurfaceHolder holder) { }
    }
}
