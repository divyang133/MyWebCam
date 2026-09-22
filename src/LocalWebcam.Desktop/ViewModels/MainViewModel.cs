using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalWebcam.Desktop.Services;
using LocalWebcam.Diagnostics;
using LocalWebcam.Protocol.Video;
using LocalWebcam.Shared.Network;
using LocalWebcam.Shared.Video;
using LocalWebcam.Video;
using LocalWebcam.Windows.Video;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace LocalWebcam.Desktop.ViewModels;

/// <summary>Backs <see cref="MainWindow"/>. Talks only to <see cref="DesktopConnectionService"/>.</summary>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DesktopConnectionService _connectionService;
    private readonly FileLoggerProvider _fileLoggerProvider;
    private readonly ILogger<MainViewModel> _logger;
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

    // Keyed by "deviceId|address", not just deviceId: the same phone can be
    // reachable over more than one transport at once (Wi-Fi and USB
    // tethering both up), each with a different address, and the point of
    // tracking them separately is so the user can see and pick between them
    // (spec update: "should be up to user to choose Wi-Fi or USB") rather
    // than the app silently keeping whichever one last happened to respond.
    private readonly Dictionary<string, DiscoveredDeviceEntry> _devicesByKey = [];

    private long _frameCount;
    private long _byteCount;
    private int _decodedFrameCountInWindow;
    private DateTime _decodedFpsWindowStart = DateTime.UtcNow;
    private DateTime _receivedStatsWindowStart = DateTime.UtcNow;
    private volatile bool _previewUpdateInFlight;
    private byte[]? _bgraBuffer;
    private DateTime _lastPreviewConversionAtUtc = DateTime.MinValue;

    // The on-screen preview is a cosmetic convenience - nobody needs it at
    // the stream's full 30/60fps to see themselves - but converting every
    // single decoded frame regardless was real, measurable CPU cost (the
    // NV12->BGRA math itself, on top of the per-frame allocation already
    // fixed separately): reported as "a lot of processing" even after that
    // fix. Halving the preview's own update rate halves this cost with no
    // effect on the actual streamed/virtual-camera output, which is pushed
    // from the raw decoded NV12 frame directly and never goes through this
    // conversion at all.
    private static readonly TimeSpan PreviewConversionInterval = TimeSpan.FromMilliseconds(1000.0 / 15);

    /// <summary>
    /// Set by <see cref="MainWindow"/> when the window is hidden (minimized
    /// to tray). The on-screen preview is pure display cost - nobody can
    /// see it while hidden - so skipping its per-frame NV12-to-BGRA
    /// conversion and bitmap update entirely (not just leaving it running
    /// off-screen) is a real, free CPU saving for exactly the
    /// run-in-the-background scenario the tray icon exists for. The
    /// streaming pipeline and virtual camera feed are completely unaffected
    /// either way - this only ever governed the on-screen preview.
    /// </summary>
    public volatile bool IsPreviewVisible = true;

    public ObservableCollection<DiscoveredDeviceEntry> Devices { get; } = [];

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Idle";

    [ObservableProperty]
    public partial string PairingCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsPairingCodeVisible { get; set; }

    [ObservableProperty]
    public partial string FramesReceivedText { get; set; } = "0";

    [ObservableProperty]
    public partial string PacketLossText { get; set; } = "-";

    [ObservableProperty]
    public partial string DecodedFpsText { get; set; } = "-";

    [ObservableProperty]
    public partial string LatencyText { get; set; } = "-";

    // Not SoftwareBitmapSource: its SetBitmapAsync re-associates a whole new
    // SoftwareBitmap with its underlying composition surface on every call,
    // which is fine for occasionally swapping a static image but not
    // designed for continuous ~30fps updates - confirmed on real hardware
    // as a rhythmic flash to black, present only while actively streaming.
    // Not WriteableBitmap either: still reported as flickering (info panel)
    // and slow (frame transitions) even after every other per-frame UI
    // update was throttled - a documented WriteableBitmap limitation (extra
    // CPU-to-GPU copy, no pacing of its own, competes with the rest of the
    // XAML compositor's tick). MainWindow.xaml.cs owns a
    // SwapChainVideoRenderer instead, fed via this event, which presents
    // through its own DXGI swap chain entirely outside XAML's imaging
    // pipeline - see that class's doc comment.
    public event Action<byte[], int, int>? PreviewFrameReady;

    [ObservableProperty]
    public partial string? ErrorText { get; set; }

    [ObservableProperty]
    public partial DiscoveredDeviceEntry? SelectedDevice { get; set; }

    /// <summary>Quality preset names for the picker; index-aligned with <see cref="VideoStreamConfig.Presets"/>.</summary>
    public ObservableCollection<string> QualityOptions { get; } = new(VideoStreamConfig.Presets.Select(p => p.Name));

    [ObservableProperty]
    public partial int SelectedQualityIndex { get; set; }

    /// <summary>Only safe to change before connecting - see <see cref="DesktopConnectionService.SetQualityAsync"/>.</summary>
    [ObservableProperty]
    public partial bool IsQualityChangeAllowed { get; set; } = true;

    /// <summary>
    /// The devices/quality/stats overlay panel, toggled by a bottom-bar icon
    /// now that the video preview fills the whole window. Starts hidden -
    /// the user opens it deliberately to pick a device rather than it
    /// covering the video by default.
    /// </summary>
    [ObservableProperty]
    public partial bool IsInfoPanelVisible { get; set; }

    [RelayCommand]
    private void ToggleInfoPanel() => IsInfoPanelVisible = !IsInfoPanelVisible;

    /// <summary>
    /// Backs the single connect/disconnect toggle button - true for any
    /// state beyond Idle/Failed, i.e. actively connecting, paired, or
    /// streaming. Replaces separate dedicated Connect/Disconnect buttons.
    /// </summary>
    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    [RelayCommand]
    private async Task ToggleConnectionAsync()
    {
        if (IsConnected)
        {
            await DisconnectAsync();
            return;
        }

        var device = SelectedDevice;
        if (device is null)
        {
            // By far the most common case is exactly one phone visible -
            // auto-select it so the single connect button "just works"
            // without the user needing to open the (default-hidden) info
            // panel and pick from its device list first. Silently doing
            // nothing here (the previous behavior) was reported as "I click
            // connect and nothing happens": technically correct - there was
            // nothing selected to connect to - but indistinguishable from a
            // bug to anyone who didn't already know a device has to be
            // selected first, with zero feedback either way.
            if (Devices.Count == 1)
            {
                device = Devices[0];
                SelectedDevice = device;
            }
            else
            {
                IsInfoPanelVisible = true;
                ErrorText = Devices.Count == 0
                    ? "No devices found yet - make sure the phone app is open and reachable (Wi-Fi or USB tethering)."
                    : "Select a device from the list first.";
                return;
            }
        }

        await ConnectAsync(device);
    }

    public MainViewModel(DesktopConnectionService connectionService, FileLoggerProvider fileLoggerProvider, ILogger<MainViewModel> logger)
    {
        _connectionService = connectionService;
        _fileLoggerProvider = fileLoggerProvider;
        _logger = logger;

        _connectionService.DeviceFound += OnDeviceFound;
        _connectionService.DeviceLost += OnDeviceLost;
        _connectionService.StateChanged += OnStateChanged;
        _connectionService.PairingCodeReady += OnPairingCodeReady;
        _connectionService.FrameReceived += OnFrameReceived;
        _connectionService.FrameDecoded += OnFrameDecoded;
        _connectionService.ErrorOccurred += OnErrorOccurred;

        // Reflects whatever InitializeVirtualCameraAsync already loaded (the
        // last-saved quality, or 720p30 the very first run). Selecting an
        // item in the Quality ComboBox does NOT auto-apply - see
        // ApplyQualityAsync/the "Apply" button in MainWindow.xaml. WinUI's
        // ComboBox has been observed (reproducibly, on real hardware) to
        // push its own transient internal selection state back through the
        // TwoWay SelectedIndex binding on its own - at window load, and
        // again around IsEnabled toggling when a connection attempt
        // starts/ends - always landing on the last preset. Auto-applying on
        // every SelectedIndex change meant those framework-internal writes
        // silently switched the virtual camera/streaming quality without
        // any user action; requiring an explicit Apply click sidesteps the
        // whole bug class rather than trying to distinguish "real" user
        // changes from framework ones.
        SelectedQualityIndex = VideoStreamConfig.Presets.ToList().FindIndex(p => p.Config == _connectionService.SelectedQuality);

        _ = StartDiscoveryAsync();
    }

    /// <summary>
    /// Re-syncs the ComboBox's displayed selection with the quality that's
    /// actually active, correcting whatever WinUI's own binding quirk (see
    /// the constructor's doc comment) may have set it to. Called after the
    /// window loads and whenever quality changes become (dis)allowed, i.e.
    /// whenever that quirk has been observed to fire.
    /// </summary>
    public void ReapplyPersistedQualitySelection()
    {
        var correctIndex = VideoStreamConfig.Presets.ToList().FindIndex(p => p.Config == _connectionService.SelectedQuality);
        if (SelectedQualityIndex == correctIndex)
        {
            return;
        }

        _logger.LogInformation("Correcting a spurious SelectedQualityIndex change ({Wrong} -> {Correct})", SelectedQualityIndex, correctIndex);
        SelectedQualityIndex = correctIndex;
    }

    [RelayCommand]
    private async Task ApplyQualityAsync()
    {
        var index = SelectedQualityIndex;
        if (index < 0 || index >= VideoStreamConfig.Presets.Count)
        {
            return;
        }

        await ChangeQualityAsync(VideoStreamConfig.Presets[index].Config);
    }

    private async Task ChangeQualityAsync(VideoStreamConfig config)
    {
        try
        {
            await _connectionService.SetQualityAsync(config);
            ErrorText = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to change streaming quality");
            ErrorText = ex.Message;
        }
    }

    private async Task StartDiscoveryAsync()
    {
        try
        {
            await _connectionService.StartDiscoveryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start discovery");
            ErrorText = ex.Message;
        }
    }

    [RelayCommand]
    private Task ConnectAsync(DiscoveredDeviceEntry? device)
    {
        device ??= SelectedDevice;
        if (device is null)
        {
            return Task.CompletedTask;
        }

        ErrorText = null;
        IsPairingCodeVisible = false;
        _frameCount = 0;
        _byteCount = 0;

        // Fire-and-forget: DesktopConnectionService.ConnectAsync's Task
        // doesn't complete until the session ends (it runs the whole
        // protocol/streaming loop internally) - awaiting it here would keep
        // this RelayCommand "running" for the entire connected duration,
        // since CommunityToolkit.Mvvm's AsyncRelayCommand disables itself
        // while its task is in flight. Confirmed on real hardware: with the
        // connect/disconnect toggle button sharing this command, that
        // permanently disabled the button the moment a connection
        // succeeded, with no way to click it again to disconnect. Progress
        // and errors are already surfaced via StateChanged/ErrorOccurred,
        // which this ViewModel already subscribes to, so nothing is lost by
        // not awaiting.
        _ = _connectionService.ConnectAsync(device.Device);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task DisconnectAsync() => await _connectionService.DisconnectAsync();

    [RelayCommand]
    private async Task SwitchCameraAsync() => await _connectionService.SwitchCameraAsync();

    /// <summary>Spec section 24's log export, for this desktop side: reveal the (rotating) log file so it can be attached to a bug report.</summary>
    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Process.Start("explorer.exe", $"/select,\"{_fileLoggerProvider.FilePath}\"");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open the log folder");
            ErrorText = $"Could not open the log folder: {ex.Message}";
        }
    }

    private void OnDeviceFound(object? sender, DiscoveredDevice device) =>
        _dispatcherQueue.TryEnqueue(() =>
        {
            var entry = DiscoveredDeviceEntry.Create(device);
            _devicesByKey[entry.Key] = entry;
            var existingIndex = IndexOfKey(entry.Key);
            if (existingIndex >= 0)
            {
                Devices[existingIndex] = entry;
            }
            else
            {
                Devices.Add(entry);
            }
        });

    /// <summary>
    /// Removes every reachable-path entry for this device: the underlying
    /// discovery sources track "last seen" per device id, not per address,
    /// so there is no finer-grained signal for "just the Wi-Fi path, not
    /// the USB one, went stale" - losing the device at all means every path
    /// to it stopped responding.
    /// </summary>
    private void OnDeviceLost(object? sender, string deviceId) =>
        _dispatcherQueue.TryEnqueue(() =>
        {
            var staleKeys = _devicesByKey.Where(kvp => kvp.Value.Device.DeviceId == deviceId).Select(kvp => kvp.Key).ToList();
            foreach (var key in staleKeys)
            {
                _devicesByKey.Remove(key);
                var index = IndexOfKey(key);
                if (index >= 0)
                {
                    Devices.RemoveAt(index);
                }
            }
        });

    private int IndexOfKey(string key)
    {
        for (var i = 0; i < Devices.Count; i++)
        {
            if (Devices[i].Key == key)
            {
                return i;
            }
        }

        return -1;
    }

    private void OnStateChanged(object? sender, RemoteConnectionState state) =>
        _dispatcherQueue.TryEnqueue(() =>
        {
            StatusText = state.ToString();
            IsConnected = state is not (RemoteConnectionState.Idle or RemoteConnectionState.Failed);

            // A stale error from an earlier failed reconnect attempt (e.g.
            // "connection attempt failed" while Wi-Fi was still down)
            // shouldn't keep showing once a later attempt actually
            // succeeds and streaming resumes.
            if (state is RemoteConnectionState.Paired or RemoteConnectionState.Streaming)
            {
                ErrorText = null;
            }

            if (state != RemoteConnectionState.AwaitingPairingCode)
            {
                IsPairingCodeVisible = false;
            }

            IsQualityChangeAllowed = state is RemoteConnectionState.Idle or RemoteConnectionState.Failed;

            // The ComboBox's IsEnabled toggling (driven by the line above)
            // is one of the points where WinUI has been observed to
            // spuriously reassert its own SelectedIndex - see
            // ReapplyPersistedQualitySelection's doc comment.
            ReapplyPersistedQualitySelection();
        });

    private void OnPairingCodeReady(object? sender, string code) =>
        _dispatcherQueue.TryEnqueue(() =>
        {
            PairingCode = code;
            IsPairingCodeVisible = true;
        });

    private void OnErrorOccurred(object? sender, string message) =>
        _dispatcherQueue.TryEnqueue(() => ErrorText = message);

    private void OnFrameReceived(object? sender, ReassembledVideoFrame frame)
    {
        Interlocked.Increment(ref _frameCount);
        Interlocked.Add(ref _byteCount, frame.Data.Length);

        // Throttled to ~1/sec, like DecodedFpsText below - this used to push
        // a property change (and so a text layout pass on the info panel)
        // on every single received frame, ~30x/sec while streaming. That
        // was independent of the video preview's own flicker (see
        // UpdatePreview's doc comment) but produced the same symptom on the
        // panel: reported as still flickering even after the preview's
        // WriteableBitmap fix landed.
        if ((DateTime.UtcNow - _receivedStatsWindowStart).TotalSeconds < 1)
        {
            return;
        }

        _receivedStatsWindowStart = DateTime.UtcNow;
        _dispatcherQueue.TryEnqueue(() =>
        {
            FramesReceivedText = $"{_frameCount} frames, {_byteCount / 1024.0:0.0} KB";
            PacketLossText = $"{_connectionService.PacketLossRatio:P1}";
        });
    }

    private void OnFrameDecoded(object? sender, DecodedVideoFrame frame)
    {
        Interlocked.Increment(ref _decodedFrameCountInWindow);

        if (!IsPreviewVisible)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now - _lastPreviewConversionAtUtc < PreviewConversionInterval)
        {
            return;
        }

        _lastPreviewConversionAtUtc = now;

        // Drop this frame if the previous one hasn't finished rendering yet,
        // rather than letting a burst of frames queue up multiple overlapping
        // preview updates. Matches the project's general drop-stale-data
        // policy: the streaming pipeline and virtual camera feed are
        // unaffected either way, this only governs the on-screen preview.
        if (_previewUpdateInFlight)
        {
            return;
        }

        _previewUpdateInFlight = true;

        // Reused across frames instead of a fresh ~3.5 MB (720p) allocation
        // every single one (~30x/sec) - that was real, measurable GC
        // pressure (reported as "a lot of processing, machine gets hung"),
        // not just a theoretical concern: this generated roughly 100 MB/sec
        // of garbage while streaming. Safe to reuse unconditionally -
        // _previewUpdateInFlight above already guarantees at most one frame
        // is ever being converted/read at a time, so there's no risk of
        // UpdatePreview (running on the UI thread, dispatched below) still
        // reading this buffer while a later call here overwrites it.
        // Resized only on the rare case dimensions actually change (a
        // quality/resolution switch), not every frame.
        var requiredLength = frame.Width * frame.Height * 4;
        if (_bgraBuffer is null || _bgraBuffer.Length != requiredLength)
        {
            _bgraBuffer = new byte[requiredLength];
        }

        var bgra = _bgraBuffer;
        Nv12ToBgraConverter.Convert(frame.Nv12Data, frame.Width, frame.Height, bgra);

        _dispatcherQueue.TryEnqueue(() => UpdatePreview(bgra, frame.Width, frame.Height));
    }

    private void UpdatePreview(byte[] bgra, int width, int height)
    {
        try
        {
            PreviewFrameReady?.Invoke(bgra, width, height);

            // Throttled to ~1/sec along with DecodedFpsText, not updated on
            // every decoded frame (~30x/sec) - this ran a text layout pass
            // on the info panel in lockstep with every single video frame
            // update, which was still visible as panel flicker even after
            // the preview itself moved to WriteableBitmap.
            var elapsed = DateTime.UtcNow - _decodedFpsWindowStart;
            if (elapsed.TotalSeconds >= 1)
            {
                LatencyText = $"{_connectionService.LastDecodeLatencyMs:0.0} ms";
                DecodedFpsText = $"{_decodedFrameCountInWindow / elapsed.TotalSeconds:0.0}";
                _decodedFrameCountInWindow = 0;
                _decodedFpsWindowStart = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update the preview image");
            ErrorText = $"Preview update failed: {ex.Message}";
        }
        finally
        {
            _previewUpdateInFlight = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _connectionService.DeviceFound -= OnDeviceFound;
        _connectionService.DeviceLost -= OnDeviceLost;
        _connectionService.StateChanged -= OnStateChanged;
        _connectionService.PairingCodeReady -= OnPairingCodeReady;
        _connectionService.FrameReceived -= OnFrameReceived;
        _connectionService.FrameDecoded -= OnFrameDecoded;
        _connectionService.ErrorOccurred -= OnErrorOccurred;

        await _connectionService.DisposeAsync();
    }
}
