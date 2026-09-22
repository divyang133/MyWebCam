using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using LocalWebcam.Desktop.Rendering;
using LocalWebcam.Desktop.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace LocalWebcam.Desktop;

/// <summary>
/// Main window. UI code stays thin — all logic lives in <see cref="MainViewModel"/>/<c>DesktopConnectionService</c>,
/// except tray-icon/minimize-to-tray behavior, which is inherently a window
/// lifecycle concern rather than something the ViewModel should need to
/// know about.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    /// <summary>Bound to the tray icon's LeftClickCommand via x:Bind (a plain routed event's exact name/availability isn't stable across package versions - this DependencyProperty is).</summary>
    public ICommand ShowWindowCommand { get; }

    private readonly AppWindow _appWindow;
    private readonly SwapChainVideoRenderer _videoRenderer;
    private bool _isExiting;

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        ShowWindowCommand = new RelayCommand(ShowWindow);
        InitializeComponent();
        Title = "Local Webcam";
        RootGrid.DataContext = viewModel;
        RootGrid.Loaded += RootGrid_Loaded;

        // See SwapChainVideoRenderer's doc comment for why the preview
        // renders through its own DXGI swap chain instead of a XAML
        // Image/WriteableBitmap.
        _videoRenderer = new SwapChainVideoRenderer(PreviewSwapChainPanel);
        ViewModel.PreviewFrameReady += _videoRenderer.UpdateFrame;

        var windowId = Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        _appWindow = AppWindow.GetFromWindowId(windowId);

        // Without this, the window could be resized (or could default to a
        // size) narrower than the three-column layout's actual content
        // needs, pushing the rightmost column's controls (e.g. the Quality
        // row's "Apply" button) past the window's own right edge instead of
        // just looking cramped - confirmed on real hardware.
        if (_appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 900;
            presenter.PreferredMinimumHeight = 500;
        }

        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");

        // <ApplicationIcon> in the csproj only sets the .exe file's own icon
        // resource (Explorer, "Open with" dialogs) - the window's titlebar/
        // taskbar icon is a separate, runtime-only setting that needs this
        // explicit AppWindow.SetIcon call, or it falls back to the generic
        // WinUI icon while running.
        _appWindow.SetIcon(iconPath);

        // Deliberately not TaskbarIcon.IconSource (an ImageSource, set via
        // an "/Assets/..." XAML path) - that resolves through ms-appx://
        // package-URI semantics, which only exist for MSIX-packaged apps.
        // This app is unpackaged (WindowsPackageType=None): confirmed on
        // real hardware that IconSource crashed the whole process with the
        // same native XAML fail-fast (0xc000027b) the MUI-resource-deletion
        // bug hit earlier - a different trigger of the same failure class.
        // A plain System.Drawing.Icon loaded from the physical file path
        // sidesteps package-URI resolution entirely.
        TrayIcon.Icon = new System.Drawing.Icon(iconPath);

        // Minimize-to-tray: the close (X) button hides the window instead of
        // exiting, so the app keeps streaming/serving the virtual camera in
        // the background - matching how OBS's own virtual camera behaves.
        // Real exit only happens via the tray icon's "Exit" menu item
        // (ExitMenuItem_Click), which sets _isExiting first.
        _appWindow.Closing += AppWindow_Closing;
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isExiting)
        {
            return;
        }

        args.Cancel = true;
        _appWindow.Hide();

        // Nobody can see the on-screen preview while hidden - skip its
        // per-frame conversion/render cost entirely rather than just
        // continuing to draw off-screen. Streaming/virtual camera output
        // are unaffected.
        ViewModel.IsPreviewVisible = false;
    }

    private void ShowMenuItem_Click(object sender, RoutedEventArgs e)
    {
        LogDiagnostic("Tray menu 'Show' clicked");
        ShowWindow();
    }

    private void ShowWindow()
    {
        _appWindow.Show();
        Activate();
        ViewModel.IsPreviewVisible = true;
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        LogDiagnostic("Tray menu 'Exit' clicked");
        _ = ExitAppAsync();
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        // A guaranteed, always-reachable way to exit that doesn't depend on
        // the tray icon's context menu - added after the tray menu's Exit
        // item was reported doing nothing, with no way yet confirmed
        // whether the click was even reaching ExitMenuItem_Click (a plain
        // WinUI Button in the always-tested main window is not in doubt the
        // way the tray's H.NotifyIcon-hosted "second window" popup menu is).
        LogDiagnostic("Main window 'Exit Local Webcam' button clicked");
        _ = ExitAppAsync();
    }

    /// <summary>
    /// Writes directly to the same log file the rest of the app uses,
    /// bypassing DI (MainWindow doesn't currently receive an ILogger) -
    /// specifically so a click that appears to "do nothing" is
    /// unambiguously distinguishable from a click that never reached this
    /// code at all, which a UI-only symptom can't tell apart on its own.
    /// </summary>
    private static void LogDiagnostic(string message)
    {
        try
        {
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LocalWebcam",
                "desktop.log");
            System.IO.File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} [Information] LocalWebcam.Desktop.MainWindow: {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Best-effort diagnostic only.
        }
    }

    private async Task ExitAppAsync()
    {
        _isExiting = true;
        LogDiagnostic("Exit requested; disposing services");

        // Best-effort, bounded: releases the phone connection, virtual
        // camera device, and pipe server cleanly rather than letting the OS
        // tear the process down mid-stream. Bounded with a timeout - not
        // just try/catch - because a real bug here previously made "Exit"
        // do nothing at all: NamedPipeServerStream.WaitForConnectionAsync's
        // cancellation is unreliable (see WindowsVirtualCamera's
        // _pendingPipe fix), so DisposeAsync could hang forever with no
        // exception ever thrown for a plain try/catch to catch. Clicking
        // Exit must always actually exit, even if some other disposal path
        // turns out to have the same class of problem later.
        try
        {
            // 5s, not something tighter: a real, legitimate (non-hung)
            // dispose - removing the OS virtual camera device via Frame
            // Server - was observed taking ~3.3s on its own on real
            // hardware, so a shorter bound risked cutting off normal
            // cleanup rather than only a genuine hang.
            var disposeTask = ViewModel.DisposeAsync().AsTask();
            await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(5)));
        }
        catch (Exception)
        {
            // Best-effort - the process is exiting either way.
        }

        LogDiagnostic("Dispose complete (or timed out); terminating process now");
        TrayIcon.Dispose();
        _videoRenderer.Dispose();

        // Environment.Exit rather than Application.Current.Exit(): a
        // guaranteed, immediate process termination regardless of whether
        // every background thread/task has wound down cleanly, rather than
        // relying on the dispatcher/message loop unwinding naturally.
        Environment.Exit(0);
    }

    // See MainViewModel.ReapplyPersistedQualitySelection's doc comment: the
    // Quality ComboBox has been observed to clobber its correct initial
    // value shortly after the visual tree loads. Correcting it once more
    // here, after Loaded, is the fix.
    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= RootGrid_Loaded;
        await System.Threading.Tasks.Task.Delay(1000);
        ViewModel.ReapplyPersistedQualitySelection();
    }
}
