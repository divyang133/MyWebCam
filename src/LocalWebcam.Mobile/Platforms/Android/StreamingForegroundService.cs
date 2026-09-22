using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using LocalWebcam.Mobile.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LocalWebcam.Mobile.Platforms.Android;

/// <summary>
/// Keeps the app's process alive and camera/network work running while the
/// screen is off or another app is in front — Android aggressively kills or
/// throttles background camera/network access otherwise (spec section 13).
/// Holds no camera or network state itself: <see cref="ForegroundServiceController"/>
/// starts/stops it purely in lockstep with <see cref="LocalWebcam.Video.ICameraService"/>
/// actually streaming, so this service's only job is the persistent
/// notification a foreground service is required to show.
/// </summary>
[Service(Exported = false, ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeCamera)]
public sealed class StreamingForegroundService : Service
{
    private const string ChannelId = "localwebcam_streaming";
    private const int NotificationId = 1;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var notification = BuildNotification();

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            StartForeground(NotificationId, notification, global::Android.Content.PM.ForegroundService.TypeCamera);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }

        // Not Sticky: this service exists purely to hold the notification
        // while ICameraService is actually streaming, and that state lives
        // in the main process. If Android kills the whole process, there is
        // no camera session left to protect by restarting an empty service.
        return StartCommandResult.NotSticky;
    }

    /// <summary>
    /// Fires when the user removes the app's task from Recents (swiping it
    /// away, or Back-ing out of the launcher activity) while this service -
    /// and so the camera - is running. Unlike backgrounding via Home (task
    /// stays in Recents, onPause/onStop only), removing the task is this
    /// app's definition of "closing" it, and streaming should actually stop
    /// then rather than silently continuing via the very mechanism
    /// (this foreground service) that exists to survive ordinary
    /// backgrounding. There's no equivalent hook for a force-stop or an OS
    /// low-memory kill - those end the process with no callback at all, but
    /// they also tear down the camera hardware handle with it either way.
    /// </summary>
    public override void OnTaskRemoved(Intent? rootIntent)
    {
        base.OnTaskRemoved(rootIntent);

        var connectionService = IPlatformApplication.Current?.Services.GetService<PhoneConnectionService>();
        _ = connectionService?.StopActiveSessionAsync();

        StopSelf();
    }

    private Notification BuildNotification()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            var manager = (NotificationManager)GetSystemService(NotificationService)!;
            if (manager.GetNotificationChannel(ChannelId) is null)
            {
                var channel = new NotificationChannel(ChannelId, "Streaming", NotificationImportance.Low)
                {
                    Description = "Shown while Local Webcam is streaming your camera to a computer.",
                };
                manager.CreateNotificationChannel(channel);
            }
        }

        var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);

        return builder
            .SetContentTitle("Local Webcam")
            .SetContentText("Streaming your camera to a computer on this network.")
            .SetSmallIcon(global::Android.Resource.Drawable.IcMenuCamera)
            .SetOngoing(true)
            .Build()!;
    }
}
