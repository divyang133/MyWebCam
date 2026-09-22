using LocalWebcam.Android.Camera;
using LocalWebcam.Android.Discovery;
using LocalWebcam.Android.Encoding;
using LocalWebcam.Android.Security;
using LocalWebcam.Diagnostics;
using LocalWebcam.Mobile.Controls;
using LocalWebcam.Mobile.Platforms.Android;
using LocalWebcam.Mobile.Services;
using LocalWebcam.Mobile.ViewModels;
using LocalWebcam.Network;
using LocalWebcam.Network.Control;
using LocalWebcam.Network.Discovery;
using LocalWebcam.Security.Pairing;
using LocalWebcam.Video;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LocalWebcam.Mobile;

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
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                // Monochrome outline icons for the bottom icon bar, matching
                // the Desktop app's Segoe Fluent Icons look instead of
                // colorful platform emoji (Apache 2.0, from Google's
                // material-design-icons repo).
                fonts.AddFont("MaterialIconsOutlined-Regular.otf", "MaterialIconsOutlined");
            })
            .ConfigureMauiHandlers(handlers =>
            {
                handlers.AddHandler<CameraPreviewView, CameraPreviewViewHandler>();
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        // FileSystem.Current.AppDataDirectory is already exposed by MAUI's
        // own FileProvider (see AndroidManifest.xml's generated
        // microsoft.maui.essentials.fileProvider entry), so a log file
        // written here can be shared via Share.RequestAsync without any
        // extra provider configuration (spec section 24's log export).
        var logPath = Path.Combine(FileSystem.Current.AppDataDirectory, "mobile.log");
        var fileLoggerProvider = new FileLoggerProvider(logPath);
        builder.Services.AddSingleton(fileLoggerProvider);
        builder.Logging.AddProvider(fileLoggerProvider);

        builder.Services.AddSingleton(_ => global::Android.App.Application.Context);
        builder.Services.AddSingleton<FrameFileRecorder>();
        builder.Services.AddSingleton<ForegroundServiceController>();
        builder.Services.AddSingleton<TetheringSettingsLauncher>();

        builder.Services.AddSingleton<ICameraController, AndroidCameraController>();
        builder.Services.AddTransient<IVideoEncoder, AndroidVideoEncoder>();
        builder.Services.AddSingleton<Func<IVideoEncoder>>(sp => sp.GetRequiredService<IVideoEncoder>);
        builder.Services.AddSingleton<ICameraService, CameraService>();

        builder.Services.AddSingleton<ITrustedDeviceStore, AndroidTrustedDeviceStore>();
        builder.Services.AddSingleton<IServiceAdvertiser, NsdServiceAdvertiser>();
        builder.Services.AddSingleton<IServiceAdvertiser, UdpBroadcastAdvertiser>(sp =>
            new UdpBroadcastAdvertiser(DiscoveryConstants.UdpBroadcastPort, sp.GetRequiredService<ILogger<UdpBroadcastAdvertiser>>()));
        builder.Services.AddSingleton<IControlListener, TcpControlListener>();
        builder.Services.AddSingleton(sp =>
        {
            var (deviceId, displayName) = GetDeviceIdentity(sp.GetRequiredService<global::Android.Content.Context>());
            return new PhoneConnectionService(
                sp.GetServices<IServiceAdvertiser>(),
                sp.GetRequiredService<IControlListener>(),
                sp.GetRequiredService<ITrustedDeviceStore>(),
                sp.GetRequiredService<ICameraService>(),
                deviceId,
                displayName,
                sp.GetRequiredService<ILoggerFactory>());
        });

        builder.Services.AddTransient<MainViewModel>();
        builder.Services.AddTransient<MainPage>();

        return builder.Build();
    }

    private static (string DeviceId, string DisplayName) GetDeviceIdentity(global::Android.Content.Context context)
    {
        const string PreferencesName = "localwebcam_identity";
        const string DeviceIdKey = "device_id";

        var prefs = context.GetSharedPreferences(PreferencesName, global::Android.Content.FileCreationMode.Private)!;
        var deviceId = prefs.GetString(DeviceIdKey, null);
        if (deviceId is null)
        {
            deviceId = Guid.NewGuid().ToString("N");
            prefs.Edit()!.PutString(DeviceIdKey, deviceId)!.Apply();
        }

        var displayName = global::Android.OS.Build.Model ?? "Android Phone";
        return (deviceId, displayName);
    }
}
