using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalWebcam.Diagnostics;
using LocalWebcam.Mobile.Platforms.Android;
using LocalWebcam.Mobile.Services;
using LocalWebcam.Shared.Video;
using LocalWebcam.Video;
using Microsoft.Extensions.Logging;

namespace LocalWebcam.Mobile.ViewModels;

/// <summary>
/// Backs <see cref="MainPage"/>. Talks only to <see cref="ICameraService"/>
/// and <see cref="PhoneConnectionService"/> (both platform-agnostic
/// contracts, or Mobile-local services); the one Android-specific
/// dependency, <see cref="FrameFileRecorder"/>, is Phase 1's throwaway
/// local-file acceptance-test harness. See docs/ANDROID.md.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ICameraService _cameraService;
    private readonly FrameFileRecorder _recorder;
    private readonly PhoneConnectionService _phoneConnectionService;
    private readonly ForegroundServiceController _foregroundServiceController;
    private readonly TetheringSettingsLauncher _tetheringSettingsLauncher;
    private readonly FileLoggerProvider _fileLoggerProvider;
    private readonly ILogger<MainViewModel> _logger;

    private object? _previewTarget;
    private CameraFacing _facing = CameraFacing.Front;
    private int _framesInWindow;
    private DateTime _fpsWindowStart = DateTime.UtcNow;
    private PairingApprovalRequest? _pendingApproval;

    [ObservableProperty]
    private string _statusText = "Idle";

    [ObservableProperty]
    private string _resolutionText = "-";

    [ObservableProperty]
    private string _fpsText = "-";

    [ObservableProperty]
    private bool _isStreaming;

    /// <summary>The single start/stop button's icon glyph - MAUI's Button has one Text, not layered content like WinUI's FontIcon overlay approach, so the glyph itself swaps instead.</summary>
    public string StreamingToggleGlyph => IsStreaming ? "" : ""; // stop : play_arrow

    partial void OnIsStreamingChanged(bool value) => OnPropertyChanged(nameof(StreamingToggleGlyph));

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private string _remoteStatusText = "Idle";

    [ObservableProperty]
    private bool _isPairingPromptVisible;

    [ObservableProperty]
    private string _pairingComputerName = string.Empty;

    [ObservableProperty]
    private string _pairingCode = string.Empty;

    /// <summary>
    /// The status/stats overlay, toggled by a bottom-bar icon now that the
    /// camera preview fills the whole screen. Starts hidden so it doesn't
    /// cover the preview by default.
    /// </summary>
    [ObservableProperty]
    private bool _isInfoPanelVisible;

    [RelayCommand]
    private void ToggleInfoPanel() => IsInfoPanelVisible = !IsInfoPanelVisible;

    public MainViewModel(ICameraService cameraService, FrameFileRecorder recorder, PhoneConnectionService phoneConnectionService, ForegroundServiceController foregroundServiceController, TetheringSettingsLauncher tetheringSettingsLauncher, FileLoggerProvider fileLoggerProvider, ILogger<MainViewModel> logger)
    {
        _cameraService = cameraService;
        _recorder = recorder;
        _phoneConnectionService = phoneConnectionService;
        _foregroundServiceController = foregroundServiceController;
        _tetheringSettingsLauncher = tetheringSettingsLauncher;
        _fileLoggerProvider = fileLoggerProvider;
        _logger = logger;

        _cameraService.StateChanged += OnStateChanged;
        _cameraService.FrameEncoded += OnFrameEncoded;
        _cameraService.Error += OnError;

        _phoneConnectionService.StateChanged += OnRemoteStateChanged;
        _phoneConnectionService.PairingApprovalRequested += OnPairingApprovalRequested;

        _ = InitializeRemoteConnectionAsync();
    }

    private async Task InitializeRemoteConnectionAsync()
    {
        try
        {
            await Permissions.RequestAsync<Permissions.Camera>();
            await _phoneConnectionService.StartAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start the remote connection service");
            MainThread.BeginInvokeOnMainThread(() => ErrorText = ex.Message);
        }
    }

    [RelayCommand]
    private void AllowPairing()
    {
        _pendingApproval?.Decision.TrySetResult(true);
        IsPairingPromptVisible = false;
    }

    [RelayCommand]
    private void RejectPairing()
    {
        _pendingApproval?.Decision.TrySetResult(false);
        IsPairingPromptVisible = false;
    }

    /// <summary>
    /// Connecting over USB requires USB tethering to already be turned on -
    /// this app can't enable it itself (see <see cref="TetheringSettingsLauncher"/>'s
    /// doc comment for why), only jump to where the user can.
    /// </summary>
    [RelayCommand]
    private void OpenTetheringSettings() => _tetheringSettingsLauncher.Open();

    private void OnRemoteStateChanged(object? sender, RemoteConnectionState state) =>
        MainThread.BeginInvokeOnMainThread(() => RemoteStatusText = state.ToString());

    private void OnPairingApprovalRequested(object? sender, PairingApprovalRequest request)
    {
        _pendingApproval = request;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            PairingComputerName = request.ComputerName;
            PairingCode = request.Code;
            IsPairingPromptVisible = true;
        });
    }

    /// <summary>Called by the page's code-behind once the preview surface exists (or is destroyed).</summary>
    public void AttachPreviewTarget(object? nativeSurface)
    {
        _previewTarget = nativeSurface;

        // A desktop-initiated connection starts the camera from
        // PhoneConnectionService directly, not through StartAsync() below -
        // it needs its own copy of the current preview target, kept in sync
        // here rather than only read once. See that property's doc comment.
        _phoneConnectionService.PreviewTarget = nativeSurface;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var status = await Permissions.RequestAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
        {
            ErrorText = "Camera permission was denied. Grant it in Android Settings to stream.";
            return;
        }

        ErrorText = null;

        try
        {
            var config = VideoStreamConfig.Default720p30;
            _recorder.Start();
            await _cameraService.StartAsync(_facing, config, _previewTarget);
            ResolutionText = $"{config.Resolution} @ {config.FrameRate} FPS";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start streaming");
            ErrorText = ex.Message;
            _recorder.Stop();
        }
    }

    private bool CanStart() => !IsStreaming;

    [RelayCommand(CanExecute = nameof(IsStreaming))]
    private async Task StopAsync()
    {
        await _cameraService.StopAsync();
        _recorder.Stop();
        _logger.LogInformation("Capture written to {Path}", _recorder.FilePath);
    }

    /// <summary>Single start/stop toggle for the bottom icon bar, instead of two separate dedicated buttons.</summary>
    [RelayCommand]
    private async Task ToggleStreamingAsync()
    {
        if (IsStreaming)
        {
            await StopAsync();
        }
        else
        {
            await StartAsync();
        }
    }

    /// <summary>Spec section 24's log export, for this phone side.</summary>
    [RelayCommand]
    private async Task ShareLogsAsync()
    {
        try
        {
            await Share.RequestAsync(new ShareFileRequest
            {
                Title = "Local Webcam logs",
                File = new ShareFile(_fileLoggerProvider.FilePath),
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to share logs");
            ErrorText = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(IsStreaming))]
    private async Task SwitchCameraAsync()
    {
        try
        {
            await _cameraService.SwitchCameraAsync();
            _facing = _facing == CameraFacing.Rear ? CameraFacing.Front : CameraFacing.Rear;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to switch camera");
            ErrorText = ex.Message;
        }
    }

    private void OnStateChanged(object? sender, StreamingState state)
    {
        // Started/stopped off the camera's own state, not the remote
        // connection's - the foreground-service notification's job is to
        // keep the camera itself alive in the background, which is needed
        // regardless of why streaming is happening.
        if (state == StreamingState.Streaming)
        {
            _foregroundServiceController.Start();
        }
        else if (state is StreamingState.Idle or StreamingState.Error)
        {
            _foregroundServiceController.Stop();
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusText = state.ToString();
            IsStreaming = state == StreamingState.Streaming;
            StartCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            SwitchCameraCommand.NotifyCanExecuteChanged();
        });
    }

    private void OnError(object? sender, CameraErrorEventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() => ErrorText = e.Message);

    private void OnFrameEncoded(object? sender, EncodedVideoFrame frame)
    {
        _recorder.Write(frame.Data);

        _framesInWindow++;
        var elapsed = DateTime.UtcNow - _fpsWindowStart;
        if (elapsed.TotalSeconds < 1)
        {
            return;
        }

        var fps = _framesInWindow / elapsed.TotalSeconds;
        _framesInWindow = 0;
        _fpsWindowStart = DateTime.UtcNow;
        MainThread.BeginInvokeOnMainThread(() => FpsText = fps.ToString("0.0"));
    }

    public void Dispose()
    {
        _cameraService.StateChanged -= OnStateChanged;
        _cameraService.FrameEncoded -= OnFrameEncoded;
        _cameraService.Error -= OnError;
        _phoneConnectionService.StateChanged -= OnRemoteStateChanged;
        _phoneConnectionService.PairingApprovalRequested -= OnPairingApprovalRequested;
    }
}
