namespace LocalWebcam.Video;

/// <summary>Carries an actionable error from the camera or encoder pipeline (spec section 27: never fail silently).</summary>
public sealed class CameraErrorEventArgs(string message, Exception? exception = null) : EventArgs
{
    public string Message { get; } = message;

    public Exception? Exception { get; } = exception;
}
