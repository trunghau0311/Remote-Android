namespace MauiRemoteAdb.Views;

/// <summary>
/// Cross-platform placeholder view. Phần render thật (Android.Views.SurfaceView)
/// nằm trong RemoteSurfaceViewHandler.Android.cs — MAUI cho phép "drop xuống" API
/// native của từng platform thông qua Handler, đây là cách chuẩn để nhúng SurfaceView.
/// </summary>
public class RemoteSurfaceView : View
{
    /// <summary>Bắn ra khi Surface native đã sẵn sàng (đã có kích thước, có thể Configure decoder).</summary>
    public event EventHandler<SurfaceReadyEventArgs>? SurfaceReady;

    /// <summary>Bắn ra khi người dùng chạm lên bề mặt, tọa độ đã quy đổi sang 0..1 (tỉ lệ tương đối).</summary>
    public event EventHandler<RemoteTouchEventArgs>? Touched;

    internal void RaiseSurfaceReady(object platformSurface, int width, int height)
        => SurfaceReady?.Invoke(this, new SurfaceReadyEventArgs(platformSurface, width, height));

    internal void RaiseTouched(RemoteTouchEventArgs args) => Touched?.Invoke(this, args);
}

public class SurfaceReadyEventArgs : EventArgs
{
    public object PlatformSurface { get; }
    public int Width { get; }
    public int Height { get; }
    public SurfaceReadyEventArgs(object platformSurface, int width, int height)
    {
        PlatformSurface = platformSurface;
        Width = width;
        Height = height;
    }
}

public class RemoteTouchEventArgs : EventArgs
{
    public double RelativeX { get; }
    public double RelativeY { get; }
    public TouchPhase Phase { get; }
    public RemoteTouchEventArgs(double relativeX, double relativeY, TouchPhase phase)
    {
        RelativeX = relativeX;
        RelativeY = relativeY;
        Phase = phase;
    }
}

public enum TouchPhase { Down, Move, Up }
