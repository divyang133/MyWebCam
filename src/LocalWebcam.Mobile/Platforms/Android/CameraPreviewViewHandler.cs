using Android.Content;
using Android.Graphics;
using Android.Views;
using LocalWebcam.Android.Camera;
using LocalWebcam.Mobile.Controls;
using Microsoft.Maui.Handlers;

namespace LocalWebcam.Mobile.Platforms.Android;

/// <summary>
/// Wraps an Android <see cref="TextureView"/> so Camera2 can render a live
/// preview into it. The resulting <see cref="Surface"/> is handed back to
/// <see cref="CameraPreviewView"/> as one of the capture session's output
/// targets (spec section 14/16 preview requirement).
///
/// Also corrects the preview's rotation via <see cref="TextureView.SetTransform"/>
/// when the device isn't in its natural (portrait) orientation - a
/// <c>TextureView</c> has no orientation awareness of its own, and Camera2
/// always delivers buffers in the sensor's native orientation regardless of
/// how the phone is currently held. Without this, rotating to landscape
/// doesn't rotate the preview's content at all - the TextureView just
/// stretches the still-portrait-shaped image to fill the now landscape-
/// shaped box, which is exactly the reported "squished" look. Uses the
/// standard Camera2 <c>configureTransform</c> pattern (Google's own
/// camera2basic sample) rather than anything bespoke - this exact rotation
/// math is notoriously easy to get subtly wrong, so it's not worth
/// reinventing. This only corrects the local, on-screen preview; the
/// actual encoded video sent to the desktop is a separate output surface
/// this transform never touches.
/// </summary>
public sealed class CameraPreviewViewHandler : ViewHandler<CameraPreviewView, TextureView>
{
    public static readonly IPropertyMapper<CameraPreviewView, CameraPreviewViewHandler> Mapper =
        new PropertyMapper<CameraPreviewView, CameraPreviewViewHandler>(ViewMapper)
        {
            // Android's TextureView throws UnsupportedOperationException if a
            // background drawable is ever applied to it (platform limitation,
            // not a bug to work around with a wrapper view). Ignore Background/
            // BackgroundColor here rather than letting a future XAML change
            // crash the app; use a parent layout's background for letterboxing.
            [nameof(IView.Background)] = (_, _) => { },
        };

    private Surface? _surface;

    public CameraPreviewViewHandler() : base(Mapper)
    {
    }

    protected override TextureView CreatePlatformView()
    {
        var textureView = new TextureView(Context);
        textureView.SurfaceTextureListener = new Listener(this);
        return textureView;
    }

    private void OnSurfaceAvailable(SurfaceTexture surfaceTexture, int width, int height)
    {
        _surface = new Surface(surfaceTexture);
        VirtualView?.RaiseSurfaceAvailable(_surface);
        UpdateTransform(width, height);
    }

    private void OnSurfaceDestroyed()
    {
        VirtualView?.RaiseSurfaceDestroyed();
        _surface?.Release();
        _surface?.Dispose();
        _surface = null;
    }

    protected override void DisconnectHandler(TextureView platformView)
    {
        platformView.SurfaceTextureListener = null;
        base.DisconnectHandler(platformView);
    }

    /// <summary>
    /// Google's camera2basic sample's <c>configureTransform</c>, verbatim
    /// (translated to C#): rotates and scales the TextureView's content so
    /// it stays upright and fills <paramref name="viewWidth"/> x
    /// <paramref name="viewHeight"/> regardless of the device's current
    /// rotation, given the actual camera buffer size published in
    /// <see cref="CameraOrientationState"/>. At rotation 0 (portrait, this
    /// app's natural orientation) this is a no-op identity matrix -
    /// matching the already-correct behavior portrait had before this
    /// transform existed at all.
    /// </summary>
    private void UpdateTransform(int viewWidth, int viewHeight)
    {
        if (PlatformView is not { } textureView || viewWidth <= 0 || viewHeight <= 0)
        {
            return;
        }

        var bufferWidth = CameraOrientationState.BufferWidth;
        var bufferHeight = CameraOrientationState.BufferHeight;
        if (bufferWidth <= 0 || bufferHeight <= 0)
        {
            return;
        }

        var rotation = GetDisplayRotation();
        var matrix = new Matrix();

        if (rotation is SurfaceOrientation.Rotation90 or SurfaceOrientation.Rotation270)
        {
            var viewRect = new global::Android.Graphics.RectF(0, 0, viewWidth, viewHeight);
            var bufferRect = new global::Android.Graphics.RectF(0, 0, bufferHeight, bufferWidth); // swapped: see class doc comment
            var centerX = viewRect.CenterX();
            var centerY = viewRect.CenterY();

            bufferRect.Offset(centerX - bufferRect.CenterX(), centerY - bufferRect.CenterY());
            matrix.SetRectToRect(viewRect, bufferRect, Matrix.ScaleToFit.Fill);

            var scale = Math.Max((float)viewHeight / bufferHeight, (float)viewWidth / bufferWidth);
            matrix.PostScale(scale, scale, centerX, centerY);
            matrix.PostRotate(90 * ((int)rotation - 2), centerX, centerY);
        }
        else if (rotation == SurfaceOrientation.Rotation180)
        {
            matrix.PostRotate(180, viewWidth / 2f, viewHeight / 2f);
        }

        textureView.SetTransform(matrix);
    }

    private SurfaceOrientation GetDisplayRotation()
    {
#pragma warning disable CS0618, CA1422 // DefaultDisplay is deprecated in favor of Context.Display (API 30+); this app's minimum API predates that.
        var windowManager = Context.GetSystemService(Context.WindowService) as IWindowManager;
        return windowManager?.DefaultDisplay?.Rotation ?? SurfaceOrientation.Rotation0;
#pragma warning restore CS0618, CA1422
    }

    private sealed class Listener(CameraPreviewViewHandler handler) : Java.Lang.Object, TextureView.ISurfaceTextureListener
    {
        public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height) =>
            handler.OnSurfaceAvailable(surface, width, height);

        public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) =>
            handler.UpdateTransform(width, height);

        public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
        {
            handler.OnSurfaceDestroyed();
            return true;
        }

        public void OnSurfaceTextureUpdated(SurfaceTexture surface)
        {
        }
    }
}
