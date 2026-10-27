namespace MauiRemoteAdb.Services;

/// <summary>
/// Điều phối toàn bộ vòng đời 1 phiên remote: connect ADB -> deploy server ->
/// forward port -> mở socket video + control -> phát sự kiện frame cho UI xử lý.
/// </summary>
public class RemoteSessionManager : IAsyncDisposable
{
    public AdbConnectionService Adb { get; } = new();
    private ScrcpyServerLauncher? _launcher;
    private ScrcpyVideoClient? _videoClient;
    private ScrcpyControlClient? _controlClient;
    private CancellationTokenSource? _serverCts;
    private CancellationTokenSource? _readCts;

    public DeviceMeta? DeviceMeta { get; private set; }

    /// <summary>
    /// SPS/PPS gần nhất server gửi. Server CHỈ gửi gói này 1 lần mỗi khi encoder khởi
    /// động — decoder tạo sau thời điểm đó phải lấy bản cache này, nếu không sẽ màn đen.
    /// </summary>
    public byte[]? LastConfigPacket => _lastConfigPacket;
    private volatile byte[]? _lastConfigPacket;
    public event EventHandler<VideoFrame>? FrameReceived;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<(int Width, int Height)>? ResolutionChanged;

    /// <summary>
    /// Chạy toàn bộ pipeline, có TỰ PHỤC HỒI: trước khi bắt đầu, tự dò và kill CHỈ những
    /// tiến trình server mang đúng scid mà CHÍNH APP NÀY đã từng tự sinh ra ở lần thử
    /// trước trong cùng phiên chạy (không đụng tới scrcpy thật hoặc phiên khác đang chạy
    /// song song — xem AdbConnectionService.KillStuckServerProcessesAsync). Nếu 1 lượt
    /// thử toàn bộ (push + start + connect) thất bại hoàn toàn, tự dọn dẹp rồi thử lại từ
    /// đầu (scid mới, script mới) thêm 1 lần nữa trước khi báo lỗi thật sự cho người dùng.
    /// </summary>
    public async Task StartAsync(int maxSize = 1280, int bitRate = 8_000_000)
    {
        if (Adb.ConnectedDevice is null)
            throw new InvalidOperationException("Phải Adb.ConnectAsync trước khi StartAsync.");

        const int maxFullAttempts = 2;
        Exception? lastFullError = null;
        string lastServerLog = string.Empty;

        for (int fullAttempt = 1; fullAttempt <= maxFullAttempts; fullAttempt++)
        {
            StatusChanged?.Invoke(this, "Đang kiểm tra & dọn tiến trình cũ trên thiết bị...");
            try
            {
                int killed = await Adb.KillStuckServerProcessesAsync();
                if (killed > 0)
                    StatusChanged?.Invoke(this, $"Đã dọn {killed} tiến trình server cũ còn kẹt lại.");
            }
            catch { /* không dọn được cũng không sao, cứ thử tiếp */ }

            try
            {
                await StartOnceAsync(maxSize, bitRate);
                return; // thành công — thoát hẳn, không cần thử thêm
            }
            catch (Exception ex)
            {
                lastFullError = ex;
                lastServerLog = _launcher is not null
                    ? await TryFetchLogAsync(_launcher)
                    : string.Empty;

                // Debug: show chi tiết lỗi + server log
                string debugMsg = $"Thử toàn bộ lần {fullAttempt} thất bại: {ex.Message}";
                if (!string.IsNullOrEmpty(lastServerLog))
                    debugMsg += $"\n[Server Log] {lastServerLog}";
                if (!string.IsNullOrEmpty(_launcher?.LastServerLog))
                    debugMsg += $"\n[Launcher Log] {_launcher.LastServerLog}";

                System.Diagnostics.Debug.WriteLine($"[RemoteSessionManager] {debugMsg}");
                StatusChanged?.Invoke(this, debugMsg);

                if (fullAttempt < maxFullAttempts)
                    StatusChanged?.Invoke(this, $"Thử lại lần {fullAttempt + 1}/{maxFullAttempts}...");
            }
        }

        throw new InvalidOperationException(
            $"{lastFullError?.Message} | Log server: {lastServerLog}", lastFullError);
    }

    private static async Task<string> TryFetchLogAsync(ScrcpyServerLauncher launcher)
    {
        try { return await launcher.FetchServerLogAsync(); }
        catch { return "(không đọc được)"; }
    }

    /// <summary>1 lượt thử đầy đủ: push server, khởi chạy, mở socket, đọc handshake. Ném exception nếu thất bại.</summary>
    private async Task StartOnceAsync(int maxSize, int bitRate)
    {
        // Dọn dẹp trạng thái phiên TRƯỚC (nếu có) — nếu lần thử trước bị bỏ dở giữa
        // chừng (ví dụ mất kết nối rồi bấm Kết nối lại), tránh để sót socket/token cũ
        // gây xung đột hoặc lỗi "Broken pipe" khi tái sử dụng nhầm đối tượng đã hỏng.
        if (_videoClient is not null) { try { await _videoClient.DisposeAsync(); } catch { } }
        if (_controlClient is not null) { try { await _controlClient.DisposeAsync(); } catch { } }
        _serverCts?.Cancel();
        _readCts?.Cancel();
        _videoClient = null;
        _controlClient = null;
        DeviceMeta = null;
        _lastConfigPacket = null;

        StatusChanged?.Invoke(this, "Đang đẩy scrcpy-server.jar lên thiết bị...");
        _launcher = new ScrcpyServerLauncher(Adb);
        await _launcher.DeployServerAsync();

        // scrcpy-server parse scid bằng Integer.parseInt(scid, 16) — hàm này trả về số
        // nguyên CÓ DẤU 32-bit, chỉ chấp nhận hex từ 00000000 đến 7fffffff (ký tự đầu
        // phải là 0-7). Cắt trực tiếp từ GUID ngẫu nhiên có ~50% khả năng ký tự đầu rơi
        // vào 8-f, khiến server crash ngay khi parse tham số (NumberFormatException) —
        // ép bit cao nhất về 0 để luôn nằm trong phạm vi hợp lệ.
        // Dùng byte[] thường (không stackalloc) vì đây là async method — C# 12 không
        // cho phép stackalloc/Span trong async/iterator method (cần C# 13 trở lên).
        var scidBytes = new byte[4];
        Random.Shared.NextBytes(scidBytes);
        scidBytes[0] &= 0x7F;
        uint scidValue = (uint)(scidBytes[0] << 24 | scidBytes[1] << 16 | scidBytes[2] << 8 | scidBytes[3]);
        var scid = scidValue.ToString("x8");
        Adb.RememberOwnScid(scid);

        StatusChanged?.Invoke(this, "Đang khởi chạy server trên thiết bị...");
        _serverCts = await _launcher.StartAsync(scid, maxSize, bitRate, enableControl: true);
        if (!string.IsNullOrEmpty(_launcher.LastServerLog))
            StatusChanged?.Invoke(this, $"Cảnh báo lúc khởi chạy: {_launcher.LastServerLog}");

        StatusChanged?.Invoke(this, "Đang thiết lập port forward...");
        int localPort = await Adb.ForwardToLocalAbstract($"scrcpy_{scid}");

        _readCts = new CancellationTokenSource();

        // Server accept() 2 kết nối theo thứ tự: video trước, control sau — NHƯNG khi
        // control=true, server KHÔNG gửi phần còn lại của handshake video (tên thiết bị,
        // codec...) cho tới khi CẢ 2 kết nối đều đã mở xong (chỉ gửi đúng 1 byte dummy
        // ngay lúc video connect, rồi im lặng chờ control). Vì vậy phải mở CẢ 2 socket
        // TCP trước, rồi MỚI đọc handshake — nếu đọc handshake video ngay sau khi mới mở
        // 1 mình socket video (chưa mở control), sẽ bị treo vĩnh viễn. Xác nhận qua debug
        // thực tế: test tay chỉ mở 1 socket video luôn bị "đứng" sau đúng 1 byte dummy.
        StatusChanged?.Invoke(this, "Đang kết nối socket video + control...");
        _videoClient = new ScrcpyVideoClient();
        _controlClient = new ScrcpyControlClient();

        const int maxAttempts = 6;
        const int perAttemptTimeoutSeconds = 20; // đủ chỗ cho: mở TCP video + mở TCP control +
                                                   // đọc handshake, qua đường truyền internet có
                                                   // độ trễ cao — 5s trước đây quá ngắn, khiến
                                                   // timeout kích hoạt đúng lúc server đang ghi dữ
                                                   // liệu, gây EPIPE phía server (xác nhận qua debug
                                                   // thực tế, không phải suy đoán).
        Exception? lastError = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // QUAN TRỌNG: mỗi lần thử phải có timeout RIÊNG — nếu không, 1 lần thử bị
            // mạng "im lặng" (không đóng kết nối, không báo lỗi, chỉ treo) sẽ làm cả vòng
            // lặp retry đứng yên vô thời hạn ở đúng lần đó, không bao giờ chuyển sang lần
            // tiếp theo (đây chính là hiện tượng "treo ở lần 2/8" quan sát được thực tế).
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(_readCts.Token);
            attemptCts.CancelAfter(TimeSpan.FromSeconds(perAttemptTimeoutSeconds));

            try
            {
                // Mở cả 2 kết nối TCP trước — CHƯA đọc gì cả.
                await _videoClient.ConnectAsync("127.0.0.1", localPort, tunnelForward: true, attemptCts.Token);
                await _controlClient.ConnectAsync("127.0.0.1", localPort, attemptCts.Token);
                // Giờ cả 2 đã kết nối, server sẽ tiếp tục gửi handshake — đọc nó.
                DeviceMeta = await _videoClient.ReadHandshakeAsync(attemptCts.Token);
                lastError = null;
                break;
            }
            catch (Exception ex)
            {
                // Nếu người dùng thực sự đóng phiên (StopAsync gọi _readCts.Cancel()),
                // dừng hẳn ngay, không retry tiếp — tránh lãng phí thời gian chờ vô ích.
                if (_readCts.IsCancellationRequested) throw;

                lastError = ex;

                // QUAN TRỌNG: dispose TƯỜNG MINH kết nối cũ trước khi tạo mới — nếu chỉ
                // gán biến mới mà bỏ rơi kết nối cũ, socket đó có thể vẫn đang "sống"
                // thật sự phía server (server đã accept() thành công, chỉ là code C#
                // coi là thất bại vì lý do khác như hết giờ). Garbage Collector sẽ tự
                // đóng nó vào 1 thời điểm ngẫu nhiên về sau — nếu đúng lúc đó server
                // đang ghi dữ liệu vào, gây EPIPE và làm CHẾT HẲN cả tiến trình server
                // (exception không bắt được, lan tới Server.main() và crash toàn bộ
                // JVM) — xác nhận qua debug thực tế, đây chính là nguyên nhân server
                // chết sau lần thử đầu dù đã tăng timeout.
                try { await _videoClient.DisposeAsync(); } catch { /* ignore */ }
                try { await _controlClient.DisposeAsync(); } catch { /* ignore */ }
                _videoClient = new ScrcpyVideoClient();
                _controlClient = new ScrcpyControlClient();

                StatusChanged?.Invoke(this, $"Chưa kết nối được (lần {attempt}/{maxAttempts}), thử lại...");
                await Task.Delay(attempt * 2000, _readCts.Token); // backoff tăng dần: 2s, 4s, 6s...
            }
        }

        if (lastError is not null)
        {
            // Chủ động đọc log file thật trên thiết bị (server giờ chạy nền độc lập,
            // ghi log ra file thay vì qua kênh shell) — đây mới là nguồn thông tin
            // đáng tin cậy nhất để biết lý do thất bại thật sự.
            string serverLog = await TryFetchLogAsync(_launcher);
            throw new InvalidOperationException(
                $"{lastError.Message} | Log server: {serverLog}", lastError);
        }

        StatusChanged?.Invoke(this, $"Đã kết nối: {DeviceMeta!.DeviceName} ({DeviceMeta.Width}x{DeviceMeta.Height})");

        // Khi server báo resolution đổi (do xoay màn hình hoặc khác), cập nhật DeviceMeta
        _videoClient!.ResolutionChanged += (_, size) =>
        {
            // Tạo DeviceMeta mới với resolution updated, giữ các thông tin khác
            DeviceMeta = new DeviceMeta(DeviceMeta.DeviceName, size.Width, size.Height, DeviceMeta.CodecFourCc);
            ResolutionChanged?.Invoke(this, size);
        };

        // QUAN TRỌNG: chạy vòng đọc frame trên thread pool. StartAsync được await từ UI
        // thread, nếu gọi thẳng PumpFramesAsync thì mọi continuation của await foreach
        // (đọc socket + decoder.Feed chờ input buffer) đều chạy trên UI thread — khi socket
        // có sẵn dữ liệu (game fps cao) vòng lặp chiếm UI thread liên tục nhiều giây => ANR.
        var readToken = _readCts.Token;
        _ = Task.Run(() => PumpFramesAsync(readToken));
    }

    private async Task PumpFramesAsync(CancellationToken token)
    {
        if (_videoClient is null) return;
        try
        {
            await foreach (var frame in _videoClient.ReadFramesAsync(token))
            {
                if (frame.IsConfigPacket) _lastConfigPacket = frame.Data;
                FrameReceived?.Invoke(this, frame);
            }
        }
        catch (OperationCanceledException) { /* dừng bình thường khi Stop() */ }
        finally
        {
            StatusChanged?.Invoke(this, "Mất kết nối video.");
        }
    }

    /// <summary>Gửi 1 sự kiện chạm (tọa độ tương đối 0..1) — quy đổi sang pixel thật của thiết bị B.</summary>
    public Task SendTouchAsync(AndroidMotionAction action, double relX, double relY)
    {
        if (_controlClient is null || DeviceMeta is null) return Task.CompletedTask;
        float x = (float)(relX * DeviceMeta.Width);
        float y = (float)(relY * DeviceMeta.Height);
        return _controlClient.SendTouchAsync(action, x, y, DeviceMeta.Width, DeviceMeta.Height);
    }

    /// <summary>Yêu cầu server gửi lại SPS/PPS + keyframe ngay (dùng khi vừa tạo decoder mới).</summary>
    public async Task RequestKeyFrameAsync()
    {
        if (_controlClient is null) return;
        try { await _controlClient.SendResetVideoAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[RemoteSessionManager] Reset video lỗi: {ex.Message}"); }
    }

    public Task SendKeyAsync(int androidKeyCode, AndroidMotionAction action)
        => _controlClient?.SendKeyAsync(androidKeyCode, action) ?? Task.CompletedTask;

    public async Task StopAsync()
    {
        _readCts?.Cancel();
        _serverCts?.Cancel();

        if (_videoClient is not null) await _videoClient.DisposeAsync();
        if (_controlClient is not null) await _controlClient.DisposeAsync();

        // Chủ động kill tiến trình server THẬT trên thiết bị B — nếu chỉ hủy token/đóng
        // socket phía app mà không làm việc này, tiến trình nền (đã tách khỏi kênh shell
        // bằng nohup) vẫn tiếp tục sống, trở thành "rác" gây xung đột cho phiên kế tiếp.
        try { await Adb.KillStuckServerProcessesAsync(); } catch { /* thiết bị có thể đã mất kết nối, bỏ qua */ }

        Adb.Disconnect();
        StatusChanged?.Invoke(this, "Đã ngắt kết nối.");
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
