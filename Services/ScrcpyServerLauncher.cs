namespace MauiRemoteAdb.Services;

/// <summary>
/// Đóng gói việc đưa scrcpy-server.jar lên thiết bị B và khởi chạy nó.
/// scrcpy-server.jar là mã nguồn mở (Apache-2.0) lấy từ:
/// https://github.com/Genymobile/scrcpy/releases (file server jar trong release assets)
/// hoặc tự build bằng Gradle từ thư mục /server trong repo.
/// Đặt file .jar vào Resources/Raw/scrcpy-server.jar trong project MAUI.
/// </summary>
public class ScrcpyServerLauncher
{
    private const string RemotePath = "/data/local/tmp/scrcpy-server.jar";
    private const string LogPath = "/data/local/tmp/scrcpy-server.log";
    private const string ScriptPath = "/data/local/tmp/start-scrcpy.sh";
    // PHẢI khớp CHÍNH XÁC với version file scrcpy-server.jar bạn nhúng vào Resources/Raw.
    // Ví dụ: nếu bạn tải "scrcpy-win64-v5.0.1.zip" từ
    // https://github.com/Genymobile/scrcpy/releases thì để "5.0.1".
    // SAI version này sẽ khiến server crash ngay khi start.
    private const string ScrcpyVersion = "5.0.1";

    private readonly AdbConnectionService _adb;

    public ScrcpyServerLauncher(AdbConnectionService adb) => _adb = adb;

    /// <summary>Push jar lên thiết bị B (chỉ cần làm 1 lần / mỗi khi đổi version, có thể cache bằng md5).</summary>
    public async Task DeployServerAsync()
    {
        await using var jarStream = await FileSystem.OpenAppPackageFileAsync("scrcpy-server.jar");
        await _adb.PushFileAsync(jarStream, RemotePath);
    }

    /// <summary>
    /// Khởi chạy server trên thiết bị B. Server sẽ mở 1 hoặc 2 local socket
    /// (video, và control nếu bật) dưới tên "scrcpy_&lt;scid&gt;".
    /// scid dùng để tránh đụng độ khi chạy nhiều phiên scrcpy cùng lúc.
    ///
    /// QUAN TRỌNG (đã xác nhận qua debug thực tế): gửi 1 dòng lệnh DÀI, nhiều tham số,
    /// nhiều ký tự đặc biệt (&lt;, &gt;, &amp;) qua ExecuteRemoteCommandAsync của thư viện
    /// C# đôi lúc không thực thi đúng như khi gõ tay qua adb.exe/CLI thật (dù cùng 1
    /// chuỗi) — nguyên nhân cụ thể chưa rõ (khả năng liên quan tới cách thư viện gửi
    /// request qua socket ADB), nhưng cách khắc phục chắc chắn hiệu quả: đóng gói toàn bộ
    /// lệnh phức tạp vào 1 FILE SCRIPT push lên thiết bị, rồi chỉ cần gửi qua thư viện
    /// đúng 1 lệnh CỰC NGẮN để chạy file đó (`sh /path/to/script.sh &amp;`).
    /// </summary>
    /// <param name="scid">8 ký tự hex ngẫu nhiên (phải nằm trong 00000000-7fffffff, xem RemoteSessionManager)</param>
    /// <param name="maxSize">Giới hạn chiều dài cạnh dài nhất của frame (0 = giữ nguyên độ phân giải gốc)</param>
    /// <param name="bitRate">Bitrate video, đơn vị bit/s</param>
    /// <param name="enableControl">Bật/tắt khả năng gửi input ngược (touch, key...)</param>
    public string BuildScriptContent(string scid, int maxSize, int bitRate, bool enableControl)
    {
        // app_process chạy trực tiếp JVM của Android bằng UID "shell" — đủ quyền
        // đọc màn hình (MediaProjection) và bơm input (InputManager) mà không cần root.
        string scrcpyArgs = $"{ScrcpyVersion} " +
               $"scid={scid} " +
               $"log_level=info " +
               $"max_size={maxSize} " +
               $"video_bit_rate={bitRate} " +
               $"video_source=display " +
               $"audio=false " +              // QUAN TRỌNG: tắt audio, nếu không server v3+ sẽ mở
                                                // thêm 1 socket audio và làm lệch thứ tự accept() mà
                                                // ScrcpyVideoClient/ScrcpyControlClient đang giả định
                                                // (chỉ video rồi tới control)
               $"tunnel_forward=true " +      // dùng adb forward (không dùng adb reverse) — phù hợp app client-side
               $"control={(enableControl ? "true" : "false")} " +
               $"cleanup=false " +             // QUAN TRỌNG: để true sẽ khiến server tự dọn dẹp (kể cả
                                                // xóa /data/local/tmp/scrcpy-server.jar) sau khi tiến
                                                // trình kết thúc — gây lỗi ClassNotFoundException ở lần
                                                // chạy kế tiếp nếu không push lại file. Xác nhận qua debug
                                                // thủ công thực tế, không phải suy đoán lý thuyết.
               $"raw_stream=false " +          // giữ frame-meta header để mình đọc PTS/size dễ hơn
               $"send_frame_meta=true " +
               $"downsize_on_error=true";

        // Nội dung file script — chạy y hệt lệnh đã kiểm chứng hoạt động đúng qua CLI,
        // chỉ khác là giờ nó nằm trong 1 file thay vì phải gửi nguyên văn qua thư viện.
        // \n dùng LF thuần (không phải \r\n) — bắt buộc, shell Android không parse được CRLF.
        // Thêm echo để debug — check xem script đã chạy được hay chưa qua log file.
        return "#!/system/bin/sh\n" +
               $"echo \"[$(date)] Script started\" >> {LogPath}\n" +
               $"CLASSPATH={RemotePath} nohup app_process / com.genymobile.scrcpy.Server {scrcpyArgs} " +
               $"</dev/null >>{LogPath} 2>&1 &\n" +
               $"echo \"[$(date)] Server PID: $!\" >> {LogPath}\n";
    }

    /// <summary>Push script khởi chạy lên thiết bị B.</summary>
    public async Task DeployScriptAsync(string scid, int maxSize, int bitRate, bool enableControl)
    {
        string content = BuildScriptContent(scid, maxSize, bitRate, enableControl);
        using var stream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(content));
        await _adb.PushFileAsync(stream, ScriptPath);
    }

    /// <summary>Đọc nội dung log file trên thiết bị B — dùng để debug khi kết nối socket thất bại.</summary>
    public Task<string> FetchServerLogAsync() => _adb.ExecuteShellAsync($"cat {LogPath}");

    /// <summary>Log gần nhất từ tiến trình scrcpy-server trên thiết bị B (để debug khi crash).</summary>
    public string LastServerLog { get; private set; } = string.Empty;

    public async Task<CancellationTokenSource> StartAsync(string scid, int maxSize = 1280, int bitRate = 8_000_000, bool enableControl = true)
    {
        var cts = new CancellationTokenSource();

        await DeployScriptAsync(scid, maxSize, bitRate, enableControl);

        // Set quyền execute cho script — bắt buộc trước khi chạy
        try
        {
            await _adb.ExecuteShellAsync($"chmod +x {ScriptPath}");
        }
        catch (Exception ex)
        {
            LastServerLog = $"Cảnh báo: không thể chmod script: {ex.Message}";
        }

        // Lệnh gửi qua thư viện giờ CỰC NGẮN, không còn dấu <, >, & phức tạp trong 1 dòng
        // dài — chỉ đơn giản là gọi "sh" chạy file script vừa push lên.
        string runCommand = $"sh {ScriptPath}";

        using var startCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        startCts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            string output = await _adb.RunServerProcessAsync(runCommand, startCts.Token);
            if (!string.IsNullOrEmpty(output))
                LastServerLog = $"Output khi khởi chạy script: {output}";
        }
        catch (Exception ex)
        {
            LastServerLog = $"Lỗi khi gửi lệnh khởi chạy: {ex.Message}";
        }

        // Cho server một khoảng thời gian ngắn để bind local socket trước khi mình forward + connect
        await Task.Delay(500); // Tăng từ 300ms lên 500ms để chắc chắn server đã startup

        return cts;
    }
}
