using LocalWebcam.Mobile.ViewModels;

namespace LocalWebcam.Mobile;

public partial class MainPage : ContentPage
{
    // All of VideoStreamConfig's current presets (720p, 1080p30, 1080p60)
    // are 16:9 - see LocalWebcam.Shared.Video.VideoStreamConfig. This is the
    // *content's* aspect ratio in landscape terms; which screen axis it maps
    // to depends on orientation (see below).
    private const double ContentAspectRatio = 16.0 / 9.0;

    private readonly MainViewModel _viewModel;

    public MainPage(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = viewModel;

        Preview.SurfaceAvailable += (_, surface) => _viewModel.AttachPreviewTarget(surface);
        Preview.SurfaceDestroyed += (_, _) => _viewModel.AttachPreviewTarget(null);

        RootLayoutGrid.SizeChanged += OnRootLayoutGridSizeChanged;
    }

    /// <summary>
    /// Fills the whole screen with the camera preview - cropping the edges
    /// as needed to match the device's actual aspect ratio (confirmed via
    /// the OnePlus 7 Pro's published spec: 1440x3120, 19.5:9), the same way
    /// virtually every camera app (stock Camera, Instagram, Snapchat) fills
    /// the screen, rather than letterboxing down to a small 16:9 strip with
    /// large black bars - that read as "squished" against a screen this
    /// much taller/narrower than 16:9.
    ///
    /// An earlier version of this letterboxed (fit inside, preserving the
    /// whole frame with black bars) using a fixed 16:9 width:height
    /// assumption regardless of orientation - correct for landscape, but
    /// for portrait (this app's normal orientation) the effectively-
    /// displayed content is tall (~9:16), not wide (~16:9), so that
    /// produced a small horizontal strip centered in a tall screen. This
    /// picks the content's orientation to match the screen's, then covers
    /// (fill outside, cropping the overflow) instead of fitting.
    ///
    /// The native TextureView has no aspect-ratio or orientation awareness
    /// of its own (see CameraPreviewViewHandler); it just fills whatever
    /// box it's given, so the box itself has to be the right shape, sized
    /// larger than the screen on one axis and centered so the parent
    /// clips the overflow (standard Android ViewGroup behavior - no extra
    /// clipping code needed). Recomputed on every size change so a device
    /// rotation keeps it correct.
    ///
    /// NOT yet re-verified live after this change (written while the test
    /// phone was disconnected) - watch specifically for the image
    /// appearing rotated 90 degrees, which would mean the capture buffer
    /// isn't pre-rotated for portrait display the way this assumes (no
    /// SENSOR_ORIENTATION/TextureView transform handling exists anywhere
    /// in AndroidCameraController/CameraPreviewViewHandler - this relies on
    /// the buffer already being correctly oriented, consistent with every
    /// screenshot taken so far showing upright, not sideways, content).
    /// </summary>
    private void OnRootLayoutGridSizeChanged(object? sender, EventArgs e)
    {
        var availableWidth = RootLayoutGrid.Width;
        var availableHeight = RootLayoutGrid.Height;
        if (availableWidth <= 0 || availableHeight <= 0)
        {
            return;
        }

        var isPortrait = availableHeight >= availableWidth;
        var contentAspect = isPortrait ? 1.0 / ContentAspectRatio : ContentAspectRatio;

        // Cover: try filling the width first: if the resulting height
        // still reaches the screen's height, that's already a full cover.
        // Otherwise fill the height instead (which by construction now
        // gives a width that reaches the screen's width).
        var widthIfFillingWidth = availableWidth;
        var heightIfFillingWidth = availableWidth / contentAspect;

        if (heightIfFillingWidth >= availableHeight)
        {
            Preview.WidthRequest = widthIfFillingWidth;
            Preview.HeightRequest = heightIfFillingWidth;
        }
        else
        {
            Preview.HeightRequest = availableHeight;
            Preview.WidthRequest = availableHeight * contentAspect;
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.Dispose();
    }
}
