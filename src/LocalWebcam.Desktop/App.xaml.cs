using System.Net;
using LocalWebcam.Desktop.Services;
using LocalWebcam.Desktop.ViewModels;
using LocalWebcam.Diagnostics;
using LocalWebcam.Network;
using LocalWebcam.Network.Discovery;
using LocalWebcam.Security.Pairing;
using LocalWebcam.Video;
using LocalWebcam.Windows.Discovery;
using LocalWebcam.Windows.Security;
using LocalWebcam.Windows.Video;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace LocalWebcam.Desktop;

/// <summary>Application entry point and composition root.</summary>
public partial class App : Application
{
    private Window? _window;
    private ServiceProvider? _services;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _services = BuildServiceProvider();

        _window = new MainWindow(_services.GetRequiredService<MainViewModel>());
        _window.Activate();
    }

    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalWebcam",
            "desktop.log");
        var fileLoggerProvider = new FileLoggerProvider(logPath);
        services.AddSingleton(fileLoggerProvider);

        services.AddLogging(builder =>
        {
            builder.AddDebug();
            builder.AddProvider(fileLoggerProvider);
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var (deviceId, displayName) = GetDeviceIdentity();

        services.AddSingleton<ITrustedDeviceStore, WindowsTrustedDeviceStore>();
        services.AddSingleton<AdbUsbBridge>();
        services.AddSingleton<IDeviceDiscovery>(sp => new CompositeDeviceDiscovery(
        [
            sp.GetRequiredService<MdnsDeviceDiscovery>(),
            new UdpBroadcastDiscovery(DiscoveryConstants.UdpBroadcastPort, sp.GetRequiredService<ILogger<UdpBroadcastDiscovery>>()),
            new UsbAdbDeviceDiscovery(sp.GetRequiredService<AdbUsbBridge>(), sp.GetRequiredService<ILogger<UsbAdbDeviceDiscovery>>()),
        ]));
        services.AddSingleton<MdnsDeviceDiscovery>();
        services.AddSingleton<IVirtualCamera, WindowsVirtualCamera>();
        services.AddSingleton(sp => new DesktopConnectionService(
            sp.GetRequiredService<IDeviceDiscovery>(),
            sp.GetRequiredService<ITrustedDeviceStore>(),
            sp.GetRequiredService<IVirtualCamera>(),
            sp.GetRequiredService<AdbUsbBridge>(),
            deviceId,
            displayName,
            sp.GetRequiredService<ILoggerFactory>()));

        services.AddTransient<MainViewModel>();

        return services.BuildServiceProvider();
    }

    private static (string DeviceId, string DisplayName) GetDeviceIdentity()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalWebcam",
            "device-id.txt");

        if (File.Exists(path))
        {
            return (File.ReadAllText(path).Trim(), Dns.GetHostName());
        }

        var deviceId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, deviceId);
        return (deviceId, Dns.GetHostName());
    }
}
