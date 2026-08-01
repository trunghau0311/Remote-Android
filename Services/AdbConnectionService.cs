using System.Net;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;

namespace MauiRemoteAdb.Services;

/// <summary>
/// Quản lý toàn bộ vòng đời kết nối ADB tới thiết bị B (thiết bị mục tiêu).
/// </summary>
public class AdbConnectionService
{
    private readonly AdbClient _client = new();
    private static bool _serverStarted;
    public DeviceData? ConnectedDevice { get; private set; }

    /// <summary>
    /// QUAN TRỌNG: AdvancedSharpAdbClient KHÔNG tự làm ADB server — nó chỉ là client nói
    /// chuyện với 1 ADB Host Server thật đang lắng nghe ở 127.0.0.1:5037 (bình thường do
    /// adb.exe cung cấp trên PC). Vì app chạy trên điện thoại, không có adb.exe nào cả,
    /// nên phải tự bundle 1 binary "adb" biên dịch cho ARM/Android và khởi động nó ngầm
    /// làm server trước khi gọi bất kỳ API nào khác.
    ///
    /// LƯU Ý QUAN TRỌNG THỨ 2: hàm <see cref="AdbServer.StartServer"/> có sẵn của thư viện
    /// KHÔNG dùng được trên Android — nó gọi xuống <c>IAdbCommandLineClient.StartServer()</c>,
    /// bên trong có đoạn kiểm tra hệ điều hành chỉ chấp nhận Windows/macOS/Linux (theo cách
    /// .NET runtime nhận diện OS), và Android không được xem là "Linux" trong kiểm tra đó dù
    /// chạy chung nhân Linux — nên luôn báo lỗi "SharpAdbClient only supports launching adb
    /// on Windows, Mac OS and Linux". Vì vậy ở đây mình tự chạy binary bằng
    /// System.Diagnostics.Process trực tiếp, KHÔNG gọi qua AdbServer.StartServer(), rồi chỉ
    /// dùng AdbServer.GetStatus() (hàm này không kiểm tra OS, chỉ mở socket TCP thuần) để
    /// biết khi nào server đã sẵn sàng.
    ///
    /// Cách lấy binary + các .so phụ thuộc: xem README.md mục "Bước 0" (qua Termux
    /// android-tools). Đặt vào: Platforms/Android/Resources/lib/arm64-v8a/libadb.so
    /// </summary>
    public async Task EnsureLocalAdbServerStartedAsync()
    {
        if (_serverStarted) return;

#if ANDROID
        var context = global::Android.App.Application.Context;
        string nativeLibDir = context.ApplicationInfo!.NativeLibraryDir!;
        string adbBinaryPath = System.IO.Path.Combine(nativeLibDir, "libadb.so");

        if (!System.IO.File.Exists(adbBinaryPath))
        {
            // Tự liệt kê những gì THỰC SỰ có trong nativeLibraryDir — nếu thư mục rỗng
            // hoàn toàn hoặc thiếu đúng các file .so đã bundle, đây là dấu hiệu build
            // Release bị lỗi đóng gói (ví dụ cài nhầm APK không khớp kiến trúc máy) chứ
            // không phải do thiếu file trong source.
            string actualFiles;
            try
            {
                var files = System.IO.Directory.GetFiles(nativeLibDir);
                actualFiles = files.Length == 0
                    ? "(thư mục rỗng — app có thể đã cài sai kiến trúc/APK)"
                    : string.Join(", ", files.Select(System.IO.Path.GetFileName));
            }
            catch (Exception ex) { actualFiles = $"(không đọc được thư mục: {ex.Message})"; }

            throw new FileNotFoundException(
                $"Không tìm thấy binary adb đã bundle tại '{adbBinaryPath}'. " +
                $"Nội dung thực tế của nativeLibraryDir: {actualFiles}. " +
                "Xem README.md mục 'Bước 0' để lấy và đặt file.",
                adbBinaryPath);
        }

        var envVars = new System.Collections.Generic.Dictionary<string, string?>
        {
            // adb build từ Termux (android-tools) phụ thuộc các .so khác (libc++_shared.so,
            // libprotobuf.so, libz.so.1...) cũng được đặt cùng nativeLibDir.
            ["LD_LIBRARY_PATH"] = nativeLibDir,
            ["ANDROID_ADB_SERVER_PORT"] = "5037",
            ["HOME"] = context.FilesDir!.AbsolutePath, // adb cần $HOME để lưu adbkey
        };

        // QUAN TRỌNG: "adb start-server" tự fork thành 1 tiến trình nền ĐỘC LẬP khỏi
        // tiến trình app — nghĩa là nếu app bị tắt/restart, tiến trình server này CÓ THỂ
        // VẪN CÒN SỐNG SÓT (không bị Android kill theo app), nhưng ở trạng thái hỏng/stale
        // từ phiên app trước (ví dụ transport tới thiết bị B đã chết). App mới khởi động
        // lại sẽ thấy cổng 5037 "đã có người dùng" và bỏ qua việc khởi động server mới,
        // rồi cố dùng lại đúng con server hỏng đó — gây lỗi "Broken pipe" không rõ nguyên
        // nhân. Để tránh việc này, LUÔN dọn sạch (kill-server) trước, đảm bảo mỗi lần app
        // khởi động lại đều có 1 server hoàn toàn mới, sạch sẽ.
        await RunAdbBinaryAsync(adbBinaryPath, "kill-server", envVars, waitForExit: true);

        var server = new AdbServer();
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = adbBinaryPath,
            Arguments = "start-server", // adb tự fork thành tiến trình nền độc lập, không cần chờ nó "chạy mãi"
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var (key, value) in envVars) psi.EnvironmentVariables[key] = value;

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Không khởi động được tiến trình adb.");

        bool ready = false;
        for (int i = 0; i < 20; i++) // tối đa ~4 giây
        {
            await Task.Delay(200);
            try
            {
                if (server.GetStatus().IsRunning) { ready = true; break; }
            }
            catch { /* server chưa kịp mở cổng, thử lại vòng sau */ }
        }

        if (!ready)
        {
            string stderr = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException(
                $"ADB server nội bộ không khởi động được sau 4 giây. Stderr: {stderr}");
        }
#endif
        _serverStarted = true;
    }

#if ANDROID
    /// <summary>Chạy binary adb với 1 lệnh đơn giản (vd "kill-server"), chờ thoát hẳn, bỏ qua lỗi nếu không có gì để kill.</summary>
    private static async Task RunAdbBinaryAsync(string adbBinaryPath, string arguments,
        System.Collections.Generic.Dictionary<string, string?> envVars, bool waitForExit)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = adbBinaryPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var (key, value) in envVars) psi.EnvironmentVariables[key] = value;

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return;
            if (waitForExit)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await process.WaitForExitAsync(cts.Token); } catch { /* timeout, kệ nó */ }
            }
        }
        catch { /* không có server nào để kill, hoặc lỗi khác — không quan trọng, cứ tiếp tục start mới */ }
    }
#endif

    /// <summary>
    /// Kết nối tới thiết bị B qua adb tcpip (đã cắm dây 1 lần chạy `adb tcpip 5555` trên đó).
    /// </summary>
    public async Task<bool> ConnectAsync(string ip, int port)
    {
        var endPoint = new DnsEndPoint(ip, port);

        // QUAN TRỌNG: nếu trước đó kết nối đã "chết" (thiết bị B mất mạng tạm thời, hoặc
        // bị OPPO ColorOS tự kill tiến trình nền), ADB server nội bộ vẫn có thể coi đó là
        // "đã connect" dù transport thật sự đã hỏng — khiến các thao tác sau (push, shell,
        // forward) đều thất bại với lỗi "Broken pipe" vì đang tái sử dụng 1 đường ống chết.
        // Chủ động Disconnect trước để ép ADB server dọn sạch, connect lại từ đầu hoàn
        // toàn mới mỗi lần bấm "Kết nối".
        try { await Task.Run(() => _client.Disconnect(endPoint)); } catch { /* chưa từng connect trước đó, bỏ qua */ }

        // Đảm bảo có adb server nội bộ chạy trong tiến trình (AdvancedSharpAdbClient
        // có thể tự khởi động adb server ẩn, không cần cài adb ngoài máy)
        await _client.ConnectAsync(endPoint);

        // QUAN TRỌNG: GetDevices() là lệnh ĐỒNG BỘ (blocking network I/O) — nếu gọi
        // trực tiếp ở đây, nó chạy ngay trên UI thread (MAUI tự quay lại UI thread sau
        // await phía trên), và nếu đường truyền chậm (qua DDNS, tranh chấp với 1 kết nối
        // scrcpy khác...) UI thread bị treo cứng => Android tự kill app do ANR. Bọc bằng
        // Task.Run để đẩy hẳn sang background thread.
        var devices = await Task.Run(() => _client.GetDevices());
        ConnectedDevice = devices.FirstOrDefault(d => d.Serial.Contains(ip));

        return ConnectedDevice is { State: DeviceState.Online };
    }

    public void Disconnect()
    {
        if (ConnectedDevice is not null)
            _client.Disconnect(new DnsEndPoint(
                ConnectedDevice.Serial.Split(':')[0],
                int.Parse(ConnectedDevice.Serial.Split(':')[1])));
        ConnectedDevice = null;
    }

    /// <summary>
    /// Danh sách scid mà CHÍNH APP NÀY đã từng tự sinh ra trong phiên chạy hiện tại
    /// (xem RemoteSessionManager) — dùng để nhận diện chính xác tiến trình nào là "của
    /// mình" trước khi kill, tránh kill nhầm.
    /// </summary>
    private readonly HashSet<string> _ownScids = new();
    public void RememberOwnScid(string scid) => _ownScids.Add(scid);

    /// <summary>
    /// Tự dò và kill CHỈ những tiến trình "app_process" mang đúng scid mà CHÍNH APP NÀY
    /// đã từng tự sinh ra (xem <see cref="RememberOwnScid"/>) — thường sót lại khi 1 phiên
    /// trước bị bỏ dở giữa chừng (crash, mất mạng...), để lại tiến trình treo chiếm giữ
    /// tài nguyên/socket, cản trở phiên mới khởi động đúng.
    ///
    /// QUAN TRỌNG: KHÔNG kill mù mọi tiến trình "app_process" tìm thấy — vì bạn hoàn toàn
    /// có thể đang mở song song 1 phiên `scrcpy` THẬT (từ PC) tới cùng thiết bị B, và
    /// scrcpy thật cũng khởi chạy server qua đúng cơ chế `app_process` giống app này. Kill
    /// mù sẽ giết luôn phiên quan sát thật của bạn. Vì vậy phải đọc thêm dòng lệnh đầy đủ
    /// của từng tiến trình (qua /proc/pid/cmdline) để xác nhận đúng là scid do chính app
    /// này sinh ra mới kill — an toàn hơn nhiều so với chỉ dựa vào tên tiến trình.
    /// (Về mặt kỹ thuật, các app cài đặt bình thường như game khi Android khởi chạy qua
    /// Zygote sẽ tự đổi tên tiến trình thành đúng package name, không còn giữ tên
    /// "app_process" nữa — nên lệnh này vốn đã không đụng tới game/app thường, chỉ có
    /// khả năng đụng nhầm phiên scrcpy thật khác đang chạy song song.)
    /// </summary>
    public async Task<int> KillStuckServerProcessesAsync()
    {
        EnsureConnected();
        if (_ownScids.Count == 0) return 0; // chưa từng tự khởi chạy phiên nào, không có gì để dọn

        string output;
        try { output = await ExecuteShellAsync("ps -A | grep app_process"); }
        catch { return 0; } // không lấy được danh sách tiến trình, coi như không có gì để dọn

        var pids = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Định dạng "ps -A" điển hình: USER  PID  PPID  VSZ  RSS  WCHAN  ADDR  S  NAME
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[1], out _))
                pids.Add(parts[1]);
        }

        int killedCount = 0;
        foreach (var pid in pids)
        {
            string cmdline;
            try
            {
                // /proc/<pid>/cmdline chứa nguyên văn dòng lệnh đã dùng để khởi chạy,
                // bao gồm cả tham số "scid=..." — đọc ra để xác nhận đúng là tiến trình
                // của chính app này trước khi kill.
                cmdline = await ExecuteShellAsync($"cat /proc/{pid}/cmdline");
            }
            catch { continue; } // tiến trình có thể đã tự thoát giữa lúc liệt kê và lúc đọc, bỏ qua

            bool isOwnProcess = _ownScids.Any(scid => cmdline.Contains($"scid={scid}"));
            if (!isOwnProcess) continue; // không phải của mình — có thể là scrcpy thật khác, KHÔNG đụng vào

            try { await ExecuteShellAsync($"kill -9 {pid}"); killedCount++; }
            catch { /* tiến trình có thể đã tự thoát, bỏ qua */ }
        }

        return killedCount;
    }

    /// <summary>Push một file (ví dụ scrcpy-server.jar) từ app lên /data/local/tmp/ của thiết bị B.</summary>
    public async Task PushFileAsync(Stream localStream, string remotePath)
    {
        EnsureConnected();
        using var sync = new SyncService(ConnectedDevice!);
        await sync.PushAsync(localStream, remotePath, UnixFileStatus.DefaultFileMode,
            DateTimeOffset.Now, null, CancellationToken.None);
    }

    /// <summary>Chạy lệnh shell trên thiết bị B, trả về output dạng text (dùng cho lệnh ngắn).</summary>
    public async Task<string> ExecuteShellAsync(string command)
    {
        EnsureConnected();
        var receiver = new ConsoleOutputReceiver();
        await _client.ExecuteRemoteCommandAsync(command, ConnectedDevice!, receiver, CancellationToken.None);
        return receiver.ToString();
    }

    /// <summary>
    /// Chạy scrcpy-server dưới dạng process nền, KHÔNG chờ kết thúc
    /// (server chạy liên tục và chỉ dừng khi mình đóng kết nối / kill process).
    /// Task trả về sẽ hoàn tất (kèm toàn bộ output console) khi tiến trình đó kết
    /// thúc — nếu server crash ngay sau khi start, đây chính là nơi lấy log lỗi.
    /// </summary>
    public async Task<string> RunServerProcessAsync(string command, CancellationToken token)
    {
        EnsureConnected();
        var receiver = new ConsoleOutputReceiver();
        await _client.ExecuteRemoteCommandAsync(command, ConnectedDevice!, receiver, token);
        return receiver.ToString();
    }

    /// <summary>
    /// Forward cổng local (trên máy A) tới local socket trên thiết bị B
    /// (localabstract:scrcpy_xxxx) — bắt buộc để app đọc được luồng video/control.
    /// </summary>
    public async Task<int> ForwardToLocalAbstract(string localAbstractName)
    {
        EnsureConnected();
        // CreateForward() cũng là lệnh đồng bộ (blocking network I/O) như GetDevices() —
        // bọc Task.Run tương tự để không chặn UI thread.
        // port 0 => để hệ thống tự chọn cổng trống, tránh xung đột
        return await Task.Run(() =>
            _client.CreateForward(ConnectedDevice!, "tcp:0", $"localabstract:{localAbstractName}", true));
    }

    public void RemoveForward(int localPort)
    {
        if (ConnectedDevice is not null)
            _client.RemoveForward(ConnectedDevice, localPort);
    }

    private void EnsureConnected()
    {
        if (ConnectedDevice is null)
            throw new InvalidOperationException("Chưa kết nối tới thiết bị B. Gọi ConnectAsync trước.");
    }
}
