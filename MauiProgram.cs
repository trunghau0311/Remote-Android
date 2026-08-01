using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Debug; // bắt buộc để AddDebug() được nhận diện là extension method
using MauiRemoteAdb.Views;
#if ANDROID
using MauiRemoteAdb.Platforms.Android;
#endif

namespace MauiRemoteAdb;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            })
            .ConfigureMauiHandlers(handlers =>
            {
#if ANDROID
                // Đăng ký handler để RemoteSurfaceView (view trừu tượng) render ra
                // Android.Views.SurfaceView thật trên nền tảng Android.
                handlers.AddHandler<RemoteSurfaceView, RemoteSurfaceViewHandler>();
#endif
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
