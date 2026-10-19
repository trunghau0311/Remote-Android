using System.Net.Sockets;

namespace MauiRemoteAdb.Services;

public enum AndroidMotionAction : byte
{
    Down = 0,
    Up = 1,
    Move = 2,
}

/// <summary>
/// Gửi các control message nhị phân qua socket "control" (kết nối thứ 2 tới cùng
/// local socket scrcpy_&lt;scid&gt;, sau khi socket video đã kết nối trước).
/// Định dạng message theo control_msg.h của scrcpy — mỗi message bắt đầu bằng 1 byte "type".
/// </summary>
public class ScrcpyControlClient : IAsyncDisposable
{
    private const byte TypeInjectKeycode = 0;
    private const byte TypeInjectTouchEvent = 2;
    private const byte TypeInjectScrollEvent = 3;
    private const byte TypeResetVideo = 17; // ControlMessage.TYPE_RESET_VIDEO (scrcpy v3+, vẫn đúng ở v5.0.1)

    private TcpClient? _tcp;
    private NetworkStream? _stream;

    public async Task ConnectAsync(string host, int port, CancellationToken token)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(host, port, token);
        _stream = _tcp.GetStream();
        // Không có handshake riêng cho control socket khi tunnel_forward=true — server
        // tự phân biệt kết nối thứ 2 là control dựa trên thứ tự accept().
    }

    /// <summary>
    /// Gửi sự kiện chạm. Tọa độ x,y tính theo pixel thực trên màn hình thiết bị B
    /// (không phải theo kích thước SurfaceView hiển thị trên app — cần quy đổi trước).
    /// </summary>
    public Task SendTouchAsync(AndroidMotionAction action, float x, float y,
        int screenWidth, int screenHeight, long pointerId = 0, float pressure = 1f, CancellationToken token = default)
    {
        var buf = new byte[32];
        int i = 0;
        buf[i++] = TypeInjectTouchEvent;
        buf[i++] = (byte)action;

        WriteBigEndianLong(buf, ref i, pointerId);
        WriteBigEndianInt(buf, ref i, (int)x);
        WriteBigEndianInt(buf, ref i, (int)y);
        WriteBigEndianShort(buf, ref i, (short)screenWidth);
        WriteBigEndianShort(buf, ref i, (short)screenHeight);

        ushort pressureFixed = (ushort)Math.Clamp(pressure * 0xFFFF, 0, 0xFFFF);
        WriteBigEndianShort(buf, ref i, (short)pressureFixed);

        WriteBigEndianInt(buf, ref i, 1); // action_button: BUTTON_PRIMARY
        WriteBigEndianInt(buf, ref i, 1); // buttons: BUTTON_PRIMARY

        return SendRawAsync(buf.AsMemory(0, i), token);
    }

    /// <summary>Gửi phím cứng (ví dụ: Back, Home, phím âm lượng) theo Android keycode.</summary>
    public Task SendKeyAsync(int androidKeyCode, AndroidMotionAction action, CancellationToken token = default)
    {
        var buf = new byte[14];
        int i = 0;
        buf[i++] = TypeInjectKeycode;
        buf[i++] = (byte)action;
        WriteBigEndianInt(buf, ref i, androidKeyCode);
        WriteBigEndianInt(buf, ref i, 0); // repeat
        WriteBigEndianInt(buf, ref i, 0); // metaState
        return SendRawAsync(buf.AsMemory(0, i), token);
    }

    /// <summary>Gửi sự kiện cuộn (ví dụ từ thao tác kéo hai ngón hoặc scroll wheel trên app).</summary>
    public Task SendScrollAsync(float x, float y, int screenWidth, int screenHeight,
        float hScroll, float vScroll, CancellationToken token = default)
    {
        var buf = new byte[21];
        int i = 0;
        buf[i++] = TypeInjectScrollEvent;
        WriteBigEndianInt(buf, ref i, (int)x);
        WriteBigEndianInt(buf, ref i, (int)y);
        WriteBigEndianShort(buf, ref i, (short)screenWidth);
        WriteBigEndianShort(buf, ref i, (short)screenHeight);
        WriteBigEndianShort(buf, ref i, (short)(hScroll * 0x7FFF));
        WriteBigEndianShort(buf, ref i, (short)(vScroll * 0x7FFF));
        WriteBigEndianInt(buf, ref i, 0); // buttons
        return SendRawAsync(buf.AsMemory(0, i), token);
    }

    /// <summary>
    /// Yêu cầu server khởi động lại encoder video -> server gửi ngay session packet +
    /// SPS/PPS (config) + keyframe mới. Dùng khi decoder phía app vừa được tạo/tạo lại
    /// để có hình ngay, thay vì chờ keyframe định kỳ (10 giây/lần).
    /// </summary>
    public Task SendResetVideoAsync(CancellationToken token = default)
        => SendRawAsync(new byte[] { TypeResetVideo }, token);

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private async Task SendRawAsync(ReadOnlyMemory<byte> data, CancellationToken token)
    {
        if (_stream is null) throw new InvalidOperationException("Chưa ConnectAsync.");
        // Nhiều nơi có thể gửi cùng lúc (touch từ UI thread, reset video từ nơi khác) —
        // khóa để 2 message không bị ghi xen kẽ byte vào nhau trên cùng 1 socket.
        await _writeLock.WaitAsync(token);
        try { await _stream.WriteAsync(data, token); }
        finally { _writeLock.Release(); }
    }

    private static void WriteBigEndianInt(byte[] buf, ref int i, int v)
    {
        buf[i++] = (byte)(v >> 24); buf[i++] = (byte)(v >> 16);
        buf[i++] = (byte)(v >> 8); buf[i++] = (byte)v;
    }
    private static void WriteBigEndianShort(byte[] buf, ref int i, short v)
    {
        buf[i++] = (byte)(v >> 8); buf[i++] = (byte)v;
    }
    private static void WriteBigEndianLong(byte[] buf, ref int i, long v)
    {
        for (int shift = 56; shift >= 0; shift -= 8) buf[i++] = (byte)(v >> shift);
    }

    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        return ValueTask.CompletedTask;
    }
}
