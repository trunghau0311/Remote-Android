using Android.App;
using Android.Runtime;

namespace MauiRemoteAdb;

/// <summary>
/// LƯU Ý: nếu project của bạn ĐÃ CÓ sẵn file MainApplication.cs (thường được Visual
/// Studio/dotnet new maui tự tạo khi khởi tạo project), đừng ghi đè cả file này —
/// chỉ copy phần constructor bắt exception bên dưới vào file có sẵn của bạn.
///
/// Bộ bắt lỗi toàn cục này ghi crash log ra 1 file trong bộ nhớ riêng của app, xem
/// được qua app Files (Android/data/com.example.mauiremoteadb/files/crashlog.txt)
/// mà không cần cắm dây + adb logcat mỗi lần muốn debug.
/// </summary>
[Application]
public class MainApplication : MauiApplication
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
        // Bắt exception xảy ra trên luồng .NET quản lý (async void, Task fire-and-forget...)
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            WriteCrashLog("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        // Bắt exception "unobserved" từ các Task chạy nền không được await/quan sát kết quả
        // (ví dụ PumpFramesAsync trong RemoteSessionManager) — nếu không bắt, có thể khiến
        // tiến trình bị kill âm thầm tùy cấu hình runtime.
        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            WriteCrashLog("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        // Bắt exception ở tầng Java/Android runtime (native crash phía Android không
        // qua được .NET AppDomain) — đây là lớp bắt "toàn diện" nhất trên Android.
        AndroidEnvironment.UnhandledExceptionRaiser += (sender, e) =>
        {
            WriteCrashLog("AndroidEnvironment.UnhandledExceptionRaiser", e.Exception);
            e.Handled = true; // đổi thành false nếu muốn app vẫn crash sau khi đã ghi log
        };
    }

    private static void WriteCrashLog(string source, Exception? ex)
    {
        try
        {
            string path = System.IO.Path.Combine(FileSystem.AppDataDirectory, "crashlog.txt");
            string content = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n{ex}\n\n";
            System.IO.File.AppendAllText(path, content);
        }
        catch { /* nếu chính việc ghi log cũng lỗi thì đành chịu, không có gì làm thêm được */ }
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
