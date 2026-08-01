using Android.Media;
using Android.Views;
using MauiRemoteAdb.Services;
using Java.Nio;

namespace MauiRemoteAdb.Platforms.Android;

/// <summary>
/// Bọc Android.Media.MediaCodec để giải mã H264 (Annex-B) nhận từ ScrcpyVideoClient
/// và render trực tiếp lên một Surface (thường lấy từ SurfaceView/TextureView trong XAML).
/// Dùng decode phần cứng => độ trễ thấp hơn nhiều so với decode phần mềm (FFmpeg-sharp...).
/// </summary>
public class VideoDecoder : IDisposable
{
    private MediaCodec? _codec;
    private bool _configured;
    private volatile bool _disposed;

    public void Configure(Surface outputSurface, int width, int height)
    {
        _codec = MediaCodec.CreateDecoderByType("video/avc"); // avc = H264
        var format = MediaFormat.CreateVideoFormat("video/avc", width, height);
        _codec.Configure(format, outputSurface, null, MediaCodecConfigFlags.None);
        _codec.Start();
        _configured = true;
    }

    /// <summary>
    /// Đưa 1 frame H264 Annex-B (bao gồm cả frame cấu hình SPS/PPS đầu tiên) vào decoder.
    /// Không cần tự parse NAL — MediaCodec ở chế độ Surface tự render khi ReleaseOutputBuffer(true).
    /// </summary>
    public void Feed(VideoFrame frame)
    {
        // QUAN TRỌNG: PumpFramesAsync chạy trên background thread khác với UI thread —
        // nếu OnDisappearing() (UI thread) gọi Dispose() đúng lúc background thread đang
        // Feed(), gọi vào MediaCodec đã Release() sẽ crash NATIVE (không bắt được bằng
        // try/catch C#, làm văng cả app ngay lập tức) — chặn sớm bằng cờ _disposed.
        if (_disposed || !_configured || _codec is null) return;

        // QUAN TRỌNG: KHÔNG được bỏ frame một cách âm thầm — đặc biệt là frame SPS/PPS
        // hoặc keyframe đầu tiên, thiếu nó decoder sẽ giải mã sai hoàn toàn (hình ảnh vỡ
        // vụn, nhiều khối màu rác — đúng hiện tượng quan sát được thực tế) cho tới khi có
        // keyframe kế tiếp (có thể rất lâu sau). Thử lại vài lần với timeout dài hơn thay
        // vì bỏ ngay ở lần thử đầu.
        int inputIndex = -1;
        for (int i = 0; i < 5 && !_disposed; i++)
        {
            inputIndex = _codec.DequeueInputBuffer(20_000); // timeout 20ms mỗi lần thử
            if (inputIndex >= 0) break;
        }
        if (inputIndex < 0) return; // decoder vẫn bận sau nhiều lần thử — thực sự phải bỏ frame này

        var inputBuffer = _codec.GetInputBuffer(inputIndex);
        inputBuffer?.Clear();
        inputBuffer?.Put(frame.Data);

        var flags = frame.IsConfigPacket ? MediaCodecBufferFlags.CodecConfig : MediaCodecBufferFlags.None;
        _codec.QueueInputBuffer(inputIndex, 0, frame.Data.Length, frame.PtsUs, flags);

        var bufferInfo = new MediaCodec.BufferInfo();
        int outputIndex = _codec.DequeueOutputBuffer(bufferInfo, 0);
        while (outputIndex >= 0)
        {
            if (_disposed) return; // vừa bị Dispose() giữa chừng — không ReleaseOutputBuffer nữa
            // true => render thẳng lên Surface đã Configure, không cần đọc byte ra tay
            _codec.ReleaseOutputBuffer(outputIndex, render: true);
            outputIndex = _codec.DequeueOutputBuffer(bufferInfo, 0);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try
        {
            _codec?.Stop();
        }
        catch { /* codec có thể đã ở trạng thái lỗi, bỏ qua khi dispose */ }
        _codec?.Release();
        _codec?.Dispose();
    }
}
