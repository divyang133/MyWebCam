using Microsoft.UI.Xaml.Controls;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DCommon;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WinUI;

namespace LocalWebcam.Desktop.Rendering;

/// <summary>
/// Renders decoded video frames onto a <see cref="SwapChainPanel"/> via a
/// DXGI composition swap chain + Direct2D, instead of a XAML
/// <c>Image</c>/<see cref="Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap"/>.
///
/// The earlier <c>WriteableBitmap</c> approach was reported as still
/// flickering (info panel) and "slow" (frame transitions) even after
/// throttling every other per-frame UI update - both match Microsoft's own
/// documented limitation for that API: "WriteableBitmap requires an extra
/// copy, which increases peak memory and source-to-screen latency" (Windows
/// apps docs, "Optimize animations, media, and images"), and its
/// <c>Invalidate()</c> call has to fight for a slot on the same XAML
/// compositor tick as everything else in the visual tree - including the
/// info panel sitting right on top of it. A SwapChainPanel bypasses XAML's
/// imaging/compositor pipeline for this one region entirely: the video
/// presents directly through its own DXGI swap chain, vsync-paced, with no
/// XAML re-layout or retained-mode redraw involved - Microsoft's documented
/// recommendation for real-time 30fps+ content for exactly this reason.
///
/// Owns its own D3D11 device (separate from <c>WindowsVideoDecoder</c>'s -
/// that one is dedicated to DXVA decode and shouldn't also serve
/// presentation) plus the D2D1 device/context/bitmaps needed to draw into
/// it. All public methods are expected to be called from the same thread
/// (the UI dispatcher thread, matching where <c>MainViewModel</c> already
/// marshals decoded frames to).
/// </summary>
public sealed class SwapChainVideoRenderer : IDisposable
{
    private readonly SwapChainPanel _panel;
    private readonly ID3D11Device _d3dDevice;
    private readonly ID2D1Factory1 _d2dFactory;
    private readonly ID2D1Device _d2dDevice;
    private readonly ID2D1DeviceContext _d2dContext;
    private readonly IDXGIFactory2 _dxgiFactory;

    private IDXGISwapChain1? _swapChain;
    private ID2D1Bitmap1? _targetBitmap;
    private ID2D1Bitmap1? _frameBitmap;
    private int _frameWidth;
    private int _frameHeight;
    private int _swapChainWidth;
    private int _swapChainHeight;
    private bool _disposed;

    public SwapChainVideoRenderer(SwapChainPanel panel)
    {
        _panel = panel;

        // BgraSupport: required for a D3D11 device to interop with
        // Direct2D at all (D2D needs a BGRA-capable device to create its
        // own device/context on top of the same D3D device).
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            [Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0, Vortice.Direct3D.FeatureLevel.Level_10_1, Vortice.Direct3D.FeatureLevel.Level_10_0],
            out _d3dDevice!,
            out ID3D11DeviceContext immediateContext).CheckError();
        immediateContext.Dispose();

        using var dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
        _dxgiFactory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();

        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
        _d2dContext = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);

        var native = new Vortice.WinUI.ISwapChainPanelNative(panel);
        CreateSwapChain(GetPanelPixelSize());
        native.SetSwapChain(_swapChain).CheckError();

        panel.SizeChanged += (_, _) => Resize(GetPanelPixelSize());
        panel.CompositionScaleChanged += (_, _) => Resize(GetPanelPixelSize());
    }

    private (int Width, int Height) GetPanelPixelSize()
    {
        var width = Math.Max(1, (int)(_panel.ActualWidth * _panel.CompositionScaleX));
        var height = Math.Max(1, (int)(_panel.ActualHeight * _panel.CompositionScaleY));
        return (width, height);
    }

    private void CreateSwapChain((int Width, int Height) size)
    {
        _swapChainWidth = size.Width;
        _swapChainHeight = size.Height;

        var description = new SwapChainDescription1(
            (uint)size.Width,
            (uint)size.Height,
            Format.B8G8R8A8_UNorm,
            false,
            Usage.RenderTargetOutput,
            2,
            Scaling.Stretch,
            SwapEffect.FlipSequential,
            Vortice.DXGI.AlphaMode.Premultiplied,
            SwapChainFlags.None);

        _swapChain = _dxgiFactory.CreateSwapChainForComposition(_d3dDevice, description, null);

        // A freshly created swap chain has no explicit color space of its
        // own, leaving the compositor to guess. Declaring it explicitly as
        // plain full-range sRGB gamma 2.2 (matching this app's actual BGRA
        // content) removes that ambiguity regardless of the display's own
        // HDR/WCG state - cheap, standards-compliant insurance, though a
        // controlled test (a solid mid-gray Clear() call rendering back as
        // exactly RGB 128,128,128, with or without this call) showed this
        // pipeline was never actually misinterpreting color to begin with;
        // a reported "video looks dark" turned out to trace back to the
        // phone's camera capture, not anything here - see AndroidCameraController.
        using (var swapChain3 = _swapChain.QueryInterfaceOrNull<IDXGISwapChain3>())
        {
            swapChain3?.SetColorSpace1(ColorSpaceType.RgbFullG22NoneP709);
        }

        CreateTargetBitmap();
    }

    private void CreateTargetBitmap()
    {
        _targetBitmap?.Dispose();
        using var surface = _swapChain!.GetBuffer<IDXGISurface>(0);
        _targetBitmap = _d2dContext.CreateBitmapFromDxgiSurface(
            surface,
            new BitmapProperties1(
                new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96f,
                96f,
                BitmapOptions.Target | BitmapOptions.CannotDraw));
    }

    private void Resize((int Width, int Height) size)
    {
        if (size.Width == _swapChainWidth && size.Height == _swapChainHeight)
        {
            return;
        }

        _targetBitmap?.Dispose();
        _targetBitmap = null;
        _swapChainWidth = size.Width;
        _swapChainHeight = size.Height;
        _swapChain!.ResizeBuffers(2, (uint)size.Width, (uint)size.Height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        CreateTargetBitmap();
    }

    /// <summary>
    /// Pushes one decoded BGRA frame to the screen. Reuses the same source
    /// bitmap across calls (recreated only when the frame's own dimensions
    /// change, e.g. a quality-preset switch) via <c>CopyFromMemory</c> -
    /// the same "one persistent buffer, write into it" principle the
    /// WriteableBitmap version used, just against a D2D bitmap that draws
    /// straight into the composition swap chain instead of a XAML Image.
    /// </summary>
    public void UpdateFrame(byte[] bgra, int width, int height)
    {
        if (_disposed || _targetBitmap is null)
        {
            return;
        }

        if (_frameBitmap is null || _frameWidth != width || _frameHeight != height)
        {
            _frameBitmap?.Dispose();
            _frameBitmap = _d2dContext.CreateBitmap(
                new SizeI(width, height),
                IntPtr.Zero,
                0,
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
            _frameWidth = width;
            _frameHeight = height;
        }

        _frameBitmap.CopyFromMemory(bgra, (uint)(width * 4));

        _d2dContext.Target = _targetBitmap;
        _d2dContext.BeginDraw();
        _d2dContext.Clear(Colors.Black);
        _d2dContext.DrawBitmap(_frameBitmap, ComputeDestinationRect(width, height), 1f, Vortice.Direct2D1.InterpolationMode.Linear, null, null);
        _d2dContext.EndDraw();

        // SyncInterval 1: vsync-paced, tear-free presentation - this is the
        // point of moving off WriteableBitmap, which had no equivalent
        // pacing of its own and just fought with XAML's compositor tick.
        _swapChain!.Present(1, Vortice.DXGI.PresentFlags.None);
    }

    /// <summary>Replicates the old Image's Stretch="Uniform": preserve the frame's aspect ratio, letterbox rather than crop or distort.</summary>
    private RawRectF ComputeDestinationRect(int frameWidth, int frameHeight)
    {
        if (_swapChainWidth <= 0 || _swapChainHeight <= 0 || frameWidth <= 0 || frameHeight <= 0)
        {
            return new RawRectF(0, 0, _swapChainWidth, _swapChainHeight);
        }

        var frameAspect = frameWidth / (float)frameHeight;
        var panelAspect = _swapChainWidth / (float)_swapChainHeight;

        if (frameAspect > panelAspect)
        {
            var destHeight = _swapChainWidth / frameAspect;
            var y = (_swapChainHeight - destHeight) / 2f;
            return new RawRectF(0, y, _swapChainWidth, y + destHeight);
        }

        var destWidth = _swapChainHeight * frameAspect;
        var x = (_swapChainWidth - destWidth) / 2f;
        return new RawRectF(x, 0, x + destWidth, _swapChainHeight);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _frameBitmap?.Dispose();
        _targetBitmap?.Dispose();
        _swapChain?.Dispose();
        _d2dContext.Dispose();
        _d2dDevice.Dispose();
        _d2dFactory.Dispose();
        _dxgiFactory.Dispose();
        _d3dDevice.Dispose();
    }
}
