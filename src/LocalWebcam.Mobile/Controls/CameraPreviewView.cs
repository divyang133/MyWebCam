namespace LocalWebcam.Mobile.Controls;

/// <summary>
/// Cross-platform camera preview surface. The platform handler
/// (<c>Platforms/Android/CameraPreviewViewHandler.cs</c>) owns the native
/// preview widget and raises <see cref="SurfaceAvailable"/> with the native
/// surface object (an Android <c>Surface</c>) once it exists, and
/// <see cref="SurfaceDestroyed"/> before it goes away.
/// </summary>
public class CameraPreviewView : View
{
    public event EventHandler<object>? SurfaceAvailable;

    public event EventHandler? SurfaceDestroyed;

    public void RaiseSurfaceAvailable(object nativeSurface) => SurfaceAvailable?.Invoke(this, nativeSurface);

    public void RaiseSurfaceDestroyed() => SurfaceDestroyed?.Invoke(this, EventArgs.Empty);
}
