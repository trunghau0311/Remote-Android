using System.Net.Sockets;

namespace MauiRemoteAdb.Services;

public record VideoFrame(byte[] Data, long PtsUs, bool IsConfigPacket, bool IsKeyFrame);

public record DeviceMeta(string DeviceName, int Width, int Height, string CodecFourCc);

/// <summary>
/// Kết nối tới cổng local đã được adb forward tới local socket "scrcpy_&lt;scid&gt;" (video).
/// Đọc theo đúng thứ tự byte mà scrcpy-server gửi:
///   1) 1 byte "dummy" (chỉ khi tunnel_forward=true, để xác nhận socket đã sẵn sàng)
///   2) 64 byte tên thiết bị (UTF-8, pad \0)
///   3) 4 byte codec id (fourcc, ví dụ "h264")
///   4) 4 byte width, 4 byte height (big-endian)
///   5) Lặp lại cho từng frame: [8 byte PTS+flags][4 byte packet size][packet size byte dữ liệu H264 Annex-B]
/// LƯU Ý: định dạng chính xác phụ thuộc version scrcpy-server — hãy đối chiếu
/// file server_socket doc trong repo scrcpy ứng với ScrcpyVersion đang dùng
/// trước khi build production (đặc biệt nếu đổi version khác 2.4).
/// </summary>
public class ScrcpyVideoClient : IAsyncDisposable
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private bool _tunnelForward;

    /// <summary>
    /// CHỈ mở kết nối TCP, KHÔNG đọc gì cả. Phải gọi hàm này cho CẢ socket video lẫn
    /// control TRƯỚC KHI gọi <see cref="ReadHandshakeAsync"/> — vì khi control=true,
    /// server đứng chờ ĐỦ CẢ 2 kết nối mở xong mới chịu gửi tiếp phần còn lại của
    /// handshake trên socket video (chỉ gửi đúng 1 byte dummy ngay lúc video connect,
    /// rồi im lặng chờ control). Đọc handshake video ngay sau khi video connect (mà
    /// chưa mở control) sẽ bị treo vĩnh viễn — đã xác nhận qua debug thực tế.
    /// </summary>
    public async Task ConnectAsync(string host, int port, bool tunnelForward, CancellationToken token)
    {
        _tunnelForward = tunnelForward;
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(host, port, token);
        _stream = _tcp.GetStream();
    }

    /// <summary>Đọc phần handshake (dummy byte, tên thiết bị, codec, kích thước) — gọi SAU khi cả 2 socket (video + control) đã ConnectAsync xong.</summary>
    public async Task<DeviceMeta> ReadHandshakeAsync(CancellationToken token)
    {
        if (_stream is null) throw new InvalidOperationException("Chưa ConnectAsync.");

        if (_tunnelForward)
        {
            // Server gửi 1 byte dummy đầu tiên khi dùng chế độ forward để báo "đã accept kết nối"
            var dummy = new byte[1];
            await ReadExactAsync(dummy, token);
        }

        var nameBuf = new byte[64];
        await ReadExactAsync(nameBuf, token);
        var deviceName = System.Text.Encoding.UTF8.GetString(nameBuf).TrimEnd('\0');

        // codec id: u32, ví dụ "h264" = 0x68323634 — đọc thẳng 4 byte ASCII cũng ra đúng chuỗi
        var codecBuf = new byte[4];
        await ReadExactAsync(codecBuf, token);
        var fourCc = System.Text.Encoding.ASCII.GetString(codecBuf);

        // QUAN TRỌNG (đã xác nhận qua tài liệu chính thức scrcpy v4.0/4.1 develop.md — không đổi ở v5.0.1):
        // đây là 1 "session packet" dài 12 BYTE, không phải 8 byte như bản cũ từng viết sai:
        //   byte 0-3: cờ (bit cao nhất = 1 đánh dấu "session packet", phân biệt với "media
        //             packet" sau này khi đọc frame — xem thêm ghi chú trong ReadFramesAsync)
        //             + bit thấp nhất = cờ "client resized" + phần còn lại padding
        //   byte 4-7: width
        //   byte 8-11: height
        // Bản cũ chỉ đọc 8 byte và lấy nhầm 4 byte cờ/padding làm width — khiến width/height
        // truyền vào MediaCodec.configure() là số rác, gây IllegalArgumentException.
        var sessionPacket = new byte[12];
        await ReadExactAsync(sessionPacket, token);
        int width = ReadBigEndianInt(sessionPacket, 4);
        int height = ReadBigEndianInt(sessionPacket, 8);

        return new DeviceMeta(deviceName, width, height, fourCc);
    }

    /// <summary>Bắn ra khi độ phân giải video đổi giữa chừng (thường do thiết bị B xoay màn hình).</summary>
    public event EventHandler<(int Width, int Height)>? ResolutionChanged;

    /// <summary>Đọc liên tục các frame H264 cho tới khi hủy hoặc mất kết nối.</summary>
    public async IAsyncEnumerable<VideoFrame> ReadFramesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        if (_stream is null) throw new InvalidOperationException("Chưa ConnectAsync.");

        var header = new byte[12];
        while (!token.IsCancellationRequested)
        {
            bool ok = await TryReadExactAsync(header, token);
            if (!ok) yield break; // socket đóng => thiết bị B ngắt hoặc server crash

            // Bit cao nhất của byte đầu tiên phân biệt "session packet" (đổi kích thước,
            // thường do xoay màn hình) với "media packet" (frame video bình thường) —
            // theo đúng tài liệu chính thức scrcpy v4.0/4.1 (giữ nguyên ở v5.0.1). Session packet KHÔNG có dữ
            // liệu payload theo sau — nếu đọc nhầm nó thành media packet, packet size sẽ
            // là số rác, làm hỏng toàn bộ việc đọc frame từ đó về sau.
            bool isSessionPacket = (header[0] & 0b1000_0000) != 0;
            if (isSessionPacket)
            {
                int newWidth = ReadBigEndianInt(header, 4);
                int newHeight = ReadBigEndianInt(header, 8);
                ResolutionChanged?.Invoke(this, (newWidth, newHeight));
                continue; // không có payload theo sau, đọc header tiếp theo luôn
            }

            long ptsAndFlags = ReadBigEndianLong(header, 0);
            int packetSize = ReadBigEndianInt(header, 8);

            // Theo đúng tài liệu chính thức (doc/develop.md, scrcpy v4.0):
            //   bit 63: cờ media/session packet (đã kiểm tra riêng ở trên qua header[0])
            //   bit 62: cờ "config packet" (C) — gói chỉ chứa SPS/PPS, không phải frame ảnh
            //   bit 61: cờ "key frame" (K)
            //   bit 0-60: PTS thật (61 bit) — bản cũ quên loại bỏ bit 61 (K) ra khỏi PTS,
            //             khiến giá trị PTS bị nhiễu bởi cờ key frame.
            bool isConfigPacket = (ptsAndFlags & (1L << 62)) != 0;
            bool isKeyFrame = (ptsAndFlags & (1L << 61)) != 0;
            long pts = ptsAndFlags & ((1L << 61) - 1);

            var payload = new byte[packetSize];
            ok = await TryReadExactAsync(payload, token);
            if (!ok) yield break;

            yield return new VideoFrame(payload, pts, isConfigPacket, isKeyFrame);
        }
    }

    private async Task ReadExactAsync(byte[] buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await _stream!.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), token);
            if (read == 0) throw new IOException("Socket video đóng đột ngột.");
            offset += read;
        }
    }

    private async Task<bool> TryReadExactAsync(byte[] buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read;
            try
            {
                read = await _stream!.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), token);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                return false;
            }
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    private static int ReadBigEndianInt(byte[] buf, int offset) =>
        (buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3];

    private static long ReadBigEndianLong(byte[] buf, int offset)
    {
        long v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | buf[offset + i];
        return v;
    }

    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        return ValueTask.CompletedTask;
    }
}
