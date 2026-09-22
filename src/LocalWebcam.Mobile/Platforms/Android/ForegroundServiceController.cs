using Android.Content;

namespace LocalWebcam.Mobile.Platforms.Android;

/// <summary>
/// Starts/stops <see cref="StreamingForegroundService"/> in lockstep with
/// <see cref="LocalWebcam.Video.ICameraService"/>'s actual streaming state
/// (wired up in <c>MainViewModel.OnStateChanged</c>). A thin wrapper so that
/// call site doesn't need to build <see cref="Intent"/>s itself.
/// </summary>
public sealed class ForegroundServiceController(Context context)
{
    public void Start()
    {
        var intent = new Intent(context, typeof(StreamingForegroundService));
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            context.StartForegroundService(intent);
        }
        else
        {
            context.StartService(intent);
        }
    }

    public void Stop() => context.StopService(new Intent(context, typeof(StreamingForegroundService)));
}
