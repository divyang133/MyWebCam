using Android.Content;
using Android.Provider;

namespace LocalWebcam.Mobile.Platforms.Android;

/// <summary>
/// Deep-links to the phone's tethering settings screen. A regular
/// (non-system) app cannot turn USB tethering on itself -
/// <c>ConnectivityManager.startTethering()</c> requires the signature-level
/// <c>TETHER_PRIVILEGED</c> permission, which only system/priv-app packages
/// can hold - so this only saves hunting through nested Settings menus; the
/// user still has to flip the toggle themselves. Used from
/// <see cref="LocalWebcam.Mobile.ViewModels.MainViewModel"/> when connecting
/// over USB rather than Wi-Fi (see docs/TROUBLESHOOTING.md).
/// </summary>
public sealed class TetheringSettingsLauncher(Context context)
{
    // Not a documented SDK constant, but a long-stable AOSP-internal action
    // string that many published apps already rely on for exactly this -
    // not guaranteed on every OEM skin/custom ROM, hence the fallback below.
    // NOTE: unverified on real hardware - written without an Android
    // toolchain available to build/deploy/test it; confirm both paths
    // actually resolve on-device before relying on this.
    private const string TetherSettingsAction = "com.android.settings.TETHER_SETTINGS";

    public void Open()
    {
        if (TryStart(TetherSettingsAction))
        {
            return;
        }

        // Falls back to the general wireless/network settings screen - one
        // level above tethering on most stock Android, but resolvable
        // wherever the more specific action above isn't.
        TryStart(Settings.ActionWirelessSettings);
    }

    private bool TryStart(string action)
    {
        try
        {
            var intent = new Intent(action);
            intent.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(intent);
            return true;
        }
        catch (ActivityNotFoundException)
        {
            return false;
        }
    }
}
