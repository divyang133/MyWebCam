namespace LocalWebcam.Windows.Video;

/// <summary>
/// Converts NV12 to BGRA32 for on-screen preview only — a display concern,
/// not part of the core decode/virtual-camera pipeline (which stays in
/// NV12 per spec section 17). Standard ITU-R BT.601 coefficients.
/// </summary>
public static class Nv12ToBgraConverter
{
    public static void Convert(ReadOnlySpan<byte> nv12, int width, int height, Span<byte> bgraDestination)
    {
        var ySize = width * height;
        var yPlane = nv12[..ySize];
        var uvPlane = nv12.Slice(ySize, ySize / 2);

        for (var row = 0; row < height; row++)
        {
            var yRow = yPlane.Slice(row * width, width);
            var uvRow = uvPlane.Slice((row / 2) * width, width);
            var destRow = bgraDestination.Slice(row * width * 4, width * 4);

            for (var col = 0; col < width; col++)
            {
                var y = yRow[col];
                var u = uvRow[(col / 2) * 2];
                var v = uvRow[(col / 2) * 2 + 1];

                var c = y - 16;
                var d = u - 128;
                var e = v - 128;

                var r = Clamp((298 * c + 409 * e + 128) >> 8);
                var g = Clamp((298 * c - 100 * d - 208 * e + 128) >> 8);
                var b = Clamp((298 * c + 516 * d + 128) >> 8);

                var destOffset = col * 4;
                destRow[destOffset] = b;
                destRow[destOffset + 1] = g;
                destRow[destOffset + 2] = r;
                destRow[destOffset + 3] = 255;
            }
        }
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}
