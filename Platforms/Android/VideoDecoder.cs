using Android.Media;
using Android.OS;
using Android.Views;
using MauiRemoteAdb.Services;

namespace MauiRemoteAdb.Platforms.Android;

/// <summary>
/// Bọc Android.Media.MediaCodec để giải mã H264 (Annex-B) nhận từ ScrcpyVideoClient
/// và render trực tiếp lên một Surface (SurfaceView).
///
/// Thiết kế:
///  - Input (Feed) chạy trên thread đọc socket; output được 1 thread RIÊNG xả liên tục
///    -> frame hiện ngay khi giải mã xong, không phải chờ tới khi frame kế tiếp tới
///    (bản cũ chỉ xả output ngay sau khi queue input với timeout 0 => luôn trễ ≥ 1 frame,
///    và frame cuối của màn hình tĩnh có thể không bao giờ được hiển thị).
///  - Nhận SPS/PPS (config packet) đã cache sẵn ngay lúc Configure — server scrcpy CHỈ
///    gửi config packet 1 lần khi encoder khởi động; nếu decoder được tạo muộn hơn (do
///    chờ SurfaceView layout) mà không có SPS/PPS thì sẽ không bao giờ ra hình (màn đen).
///  - Bỏ các frame P/B cho tới khi gặp keyframe đầu tiên -> không bị hình vỡ khối rác.
/// </summary>
public class VideoDecoder : IDisposable
{
    private const string MimeAvc = "video/avc";

    private readonly object _inputLock = new();
    private MediaCodec? _codec;
    private Thread? _outputThread;
    private volatile bool _disposed;
    private bool _waitingForKeyFrame = true;

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Số frame đã render thật lên Surface (dùng để debug/kiểm tra "có hình chưa").</summary>
    public long RenderedFrames => Interlocked.Read(ref _renderedFrames);
    private long _renderedFrames;

    /// <param name="configPacket">SPS/PPS đã cache từ server (có thể null nếu chưa nhận được).</param>
    public void Configure(Surface outputSurface, int width, int height, byte[]? configPacket)
    {
        Width = width;
        Height = height;

        _codec = MediaCodec.CreateDecoderByType(MimeAvc);
        var format = MediaFormat.CreateVideoFormat(MimeAvc, width, height);

        // Giảm độ trễ: yêu cầu decoder chạy ở chế độ realtime / low-latency nếu hỗ trợ.
        format.SetInteger(MediaFormat.KeyPriority, 0); // 0 = realtime
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            format.SetInteger(MediaFormat.KeyLowLatency, 1);

        try
        {
            _codec.Configure(format, outputSurface, null, MediaCodecConfigFlags.None);
            _codec.Start();
        }
        catch
        {
            // Configure lỗi -> nhả MediaCodec ngay, không để rò rỉ/giữ chiếm Surface
            try { _codec.Release(); } catch { }
            _codec.Dispose();
            _codec = null;
            _disposed = true;
            throw;
        }

        _outputThread = new Thread(OutputLoop) { IsBackground = true, Name = "VideoDecoderOutput" };
        _outputThread.Start();

        if (configPacket is { Length: > 0 })
            QueueInput(configPacket, 0, MediaCodecBufferFlags.CodecConfig);
    }

    /// <summary>Đưa 1 packet H264 Annex-B vào decoder (gọi từ thread đọc socket).</summary>
    public void Feed(VideoFrame frame)
    {
        if (_disposed || _codec is null) return;

        if (frame.IsConfigPacket)
        {
            // SPS/PPS mới (encoder vừa khởi động lại) -> keyframe sẽ theo ngay sau
            _waitingForKeyFrame = true;
            QueueInput(frame.Data, 0, MediaCodecBufferFlags.CodecConfig);
            return;
        }

        if (_waitingForKeyFrame)
        {
            if (!frame.IsKeyFrame) return; // chưa có keyframe thì decode cũng chỉ ra rác
            _waitingForKeyFrame = false;
        }

        QueueInput(frame.Data, frame.PtsUs, MediaCodecBufferFlags.None);
    }

    private void QueueInput(byte[] data, long ptsUs, MediaCodecBufferFlags flags)
    {
        lock (_inputLock)
        {
            if (_disposed || _codec is null) return;
            try
            {
                int inputIndex = -1;
                for (int i = 0; i < 5 && !_disposed; i++)
                {
                    inputIndex = _codec.DequeueInputBuffer(20_000); // 20ms mỗi lần thử
                    if (inputIndex >= 0) break;
                }
                if (inputIndex < 0)
                {
                    // Decoder quá tải -> buộc phải bỏ frame; chờ keyframe tiếp theo để tránh hình rác
                    if (flags != MediaCodecBufferFlags.CodecConfig) _waitingForKeyFrame = true;
                    return;
                }

                var inputBuffer = _codec.GetInputBuffer(inputIndex);
                if (inputBuffer is null) return;
                inputBuffer.Clear();
                inputBuffer.Put(data);
                _codec.QueueInputBuffer(inputIndex, 0, data.Length, ptsUs, flags);
            }
            catch (Java.Lang.IllegalStateException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VideoDecoder] QueueInput lỗi: {ex.Message}");
            }
        }
    }

    private void OutputLoop()
    {
        var info = new MediaCodec.BufferInfo();
        while (!_disposed)
        {
            try
            {
                var codec = _codec;
                if (codec is null) break;
                int index = codec.DequeueOutputBuffer(info, 10_000); // 10ms
                if (index >= 0)
                {
                    codec.ReleaseOutputBuffer(index, render: !_disposed);
                    Interlocked.Increment(ref _renderedFrames);
                }
                // index < 0: TRY_AGAIN_LATER / OUTPUT_FORMAT_CHANGED / ... -> chỉ cần lặp tiếp
            }
            catch (Exception ex)
            {
                if (!_disposed)
                    System.Diagnostics.Debug.WriteLine($"[VideoDecoder] OutputLoop lỗi: {ex.Message}");
                break;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Chờ thread output thoát (nó chỉ block tối đa 10ms mỗi vòng) TRƯỚC khi Release,
        // tránh gọi vào MediaCodec đã release => crash native không bắt được.
        try { _outputThread?.Join(500); } catch { }

        lock (_inputLock)
        {
            try { _codec?.Stop(); } catch { /* codec có thể đã ở trạng thái lỗi */ }
            try { _codec?.Release(); } catch { }
            _codec?.Dispose();
            _codec = null;
        }
    }
}
