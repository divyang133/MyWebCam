namespace LocalWebcam.Android.Camera;

/// <summary>
/// The currently-open camera's actual capture buffer size, published by
/// <see cref="AndroidCameraController"/> whenever it starts a capture
/// session. <c>LocalWebcam.Mobile.Platforms.Android.CameraPreviewViewHandler</c>
/// reads this to correct the on-screen preview's rotation - a
/// <c>TextureView</c> has no orientation awareness of its own (Camera2
/// delivers buffers in the sensor's native orientation regardless of how
/// the phone is currently held), and it lives in a different project/class
/// with no existing reference to <see cref="AndroidCameraController"/>
/// (the preview surface it owns is handed *to* the controller as an output
/// target, not the other way around). A small shared static instead of a
/// larger DI/event-wiring change - there is exactly one camera open at a
/// time in this app, so "the current value" is all either side ever needs.
/// </summary>
public static class CameraOrientationState
{
    /// <summary>The active capture's requested resolution (<c>VideoStreamConfig.Resolution</c> - the same size for every output surface in this app's single capture request, preview included) - needed to scale the preview's rotation transform correctly, not just rotate it in place.</summary>
    public static int BufferWidth { get; set; }

    public static int BufferHeight { get; set; }
}
