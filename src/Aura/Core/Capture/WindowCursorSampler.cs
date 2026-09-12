using System.Runtime.InteropServices;
using Aura.Core.Interop;

namespace Aura.Core.Capture;

/// <summary>Снимает системный курсор и переводит его в координаты client texture.</summary>
internal sealed class WindowCursorSampler
{
    private readonly GameCaptureTarget _target;
    private readonly object _sync = new();
    private nint _cursorHandle;
    private DdaCursorShape? _shape;
    private int _hotspotX;
    private int _hotspotY;
    private long _targetRevision;
    private bool _resetPending = true;
    private long _invalidShapes;

    public WindowCursorSampler(in GameCaptureTarget target)
    {
        _target = target;
        Reset(target.Revision);
    }

    public long InvalidShapes => Interlocked.Read(ref _invalidShapes);

    public void Reset(long targetRevision, bool force = false)
    {
        lock (_sync)
        {
            if (!force && !WindowCursorPolicy.ShouldReset(_targetRevision, targetRevision)) return;
            _targetRevision = targetRevision;
            _cursorHandle = 0;
            _shape = null;
            _hotspotX = 0;
            _hotspotY = 0;
            _resetPending = true;
        }
    }

    public CaptureCursorUpdate Sample(bool enabled)
    {
        lock (_sync)
        {
            bool reset = _resetPending;
            _resetPending = false;
            if (!enabled) return Hidden(hasPosition: false, reset);

            var info = new NativeMethods.CURSORINFO
            {
                cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>()
            };
            if (!NativeMethods.GetCursorInfo(ref info)) return Hidden(hasPosition: false, reset);

            bool showing = (info.flags & NativeMethods.CURSOR_SHOWING) != 0;
            bool inside = WindowCursorPolicy.IsInsideTarget(
                info.ptScreenPos.X,
                info.ptScreenPos.Y,
                _target.ClientBounds);
            if (!showing || !inside) return Hidden(hasPosition: true, reset);

            bool refresh = WindowCursorPolicy.ShouldRefreshShape(
                _cursorHandle,
                info.hCursor,
                _shape is not null);
            if (refresh)
            {
                if (!TryReadShape(
                        info.hCursor,
                        out DdaCursorShape? shape,
                        out int hotspotX,
                        out int hotspotY))
                {
                    _cursorHandle = 0;
                    _shape = null;
                    Interlocked.Increment(ref _invalidShapes);
                    return Hidden(hasPosition: true, reset: true);
                }

                _cursorHandle = info.hCursor;
                _shape = shape;
                _hotspotX = hotspotX;
                _hotspotY = hotspotY;
            }

            CursorDrawPosition position = WindowCursorPolicy.MapPosition(
                info.ptScreenPos.X,
                info.ptScreenPos.Y,
                _target.ClientBounds.X,
                _target.ClientBounds.Y,
                _hotspotX,
                _hotspotY);
            return new CaptureCursorUpdate(
                CaptureCursorMode.Separate,
                HasPosition: true,
                Visible: _shape is not null,
                position.X,
                position.Y,
                Shape: refresh ? _shape : null,
                ResetState: reset);
        }
    }

    private static CaptureCursorUpdate Hidden(bool hasPosition, bool reset) => new(
        CaptureCursorMode.Separate,
        hasPosition,
        Visible: false,
        X: 0,
        Y: 0,
        Shape: null,
        ResetState: reset);

    private static bool TryReadShape(
        nint cursor,
        out DdaCursorShape? shape,
        out int hotspotX,
        out int hotspotY)
    {
        shape = null;
        hotspotX = 0;
        hotspotY = 0;
        nint copied = 0;
        NativeMethods.ICONINFO icon = default;
        try
        {
            copied = NativeMethods.CopyIcon(cursor);
            if (copied == 0 || !NativeMethods.GetIconInfo(copied, out icon)) return false;
            hotspotX = checked((int)icon.xHotspot);
            hotspotY = checked((int)icon.yHotspot);

            if (icon.hbmColor != 0)
                return TryReadColorShape(icon.hbmColor, icon.hbmMask, out shape);
            return TryReadMonochromeShape(icon.hbmMask, out shape);
        }
        catch (Exception)
        {
            shape = null;
            return false;
        }
        finally
        {
            if (icon.hbmColor != 0) NativeMethods.DeleteObject(icon.hbmColor);
            if (icon.hbmMask != 0) NativeMethods.DeleteObject(icon.hbmMask);
            if (copied != 0) NativeMethods.DestroyIcon(copied);
        }
    }

    private static bool TryReadColorShape(
        nint colorBitmap,
        nint maskBitmap,
        out DdaCursorShape? shape)
    {
        shape = null;
        if (!TryReadBitmap32(colorBitmap, out int width, out int height, out byte[] pixels))
            return false;
        if (width is < 1 or > DdaCursorShape.MaximumDimension ||
            height is < 1 or > DdaCursorShape.MaximumDimension)
            return false;

        bool hasAlpha = false;
        for (int offset = 3; offset < pixels.Length; offset += 4)
            hasAlpha |= pixels[offset] != 0;

        if (!hasAlpha && maskBitmap != 0 &&
            TryReadBitmap32(maskBitmap, out int maskWidth, out int maskHeight, out byte[] mask) &&
            maskWidth == width && maskHeight >= height)
        {
            for (int pixel = 0; pixel < width * height; pixel++)
                pixels[pixel * 4 + 3] = mask[pixel * 4] < 128 ? (byte)255 : (byte)0;
        }

        shape = new DdaCursorShape(DdaCursorShapeKind.Color, width, height, pixels);
        return true;
    }

    private static bool TryReadMonochromeShape(nint maskBitmap, out DdaCursorShape? shape)
    {
        shape = null;
        if (maskBitmap == 0 ||
            !TryReadBitmap32(maskBitmap, out int width, out int stackedHeight, out byte[] masks) ||
            (stackedHeight & 1) != 0)
            return false;

        int height = stackedHeight / 2;
        if (width is < 1 or > DdaCursorShape.MaximumDimension ||
            height is < 1 or > DdaCursorShape.MaximumDimension)
            return false;

        var pixels = new byte[checked(width * height * 4)];
        int halfOffset = checked(width * height * 4);
        for (int pixel = 0; pixel < width * height; pixel++)
        {
            byte and = masks[pixel * 4] >= 128 ? (byte)255 : (byte)0;
            byte xor = masks[halfOffset + pixel * 4] >= 128 ? (byte)255 : (byte)0;
            int destination = pixel * 4;
            pixels[destination] = xor;
            pixels[destination + 1] = xor;
            pixels[destination + 2] = xor;
            pixels[destination + 3] = and;
        }

        shape = new DdaCursorShape(DdaCursorShapeKind.Monochrome, width, height, pixels);
        return true;
    }

    private static bool TryReadBitmap32(
        nint bitmapHandle,
        out int width,
        out int height,
        out byte[] pixels)
    {
        width = 0;
        height = 0;
        pixels = [];
        if (NativeMethods.GetObjectW(
                bitmapHandle,
                Marshal.SizeOf<NativeMethods.BITMAP>(),
                out NativeMethods.BITMAP bitmap) == 0)
            return false;

        width = Math.Abs(bitmap.bmWidth);
        height = Math.Abs(bitmap.bmHeight);
        if (width <= 0 || height <= 0) return false;

        int byteCount = checked(width * height * 4);
        pixels = new byte[byteCount];
        var info = new NativeMethods.BITMAPINFO
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB,
                biSizeImage = (uint)byteCount
            }
        };

        nint dc = NativeMethods.CreateCompatibleDC(0);
        if (dc == 0) return false;
        try
        {
            return NativeMethods.GetDIBits(
                dc,
                bitmapHandle,
                0,
                (uint)height,
                pixels,
                ref info,
                NativeMethods.DIB_RGB_COLORS) == height;
        }
        finally
        {
            NativeMethods.DeleteDC(dc);
        }
    }
}
