using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DeskPal.Pose;

/// <summary>Copies a camera frame's pixels into a reused in-memory buffer. Nothing is written anywhere else.</summary>
internal sealed class PoseImage
{
    private byte[] _pixels = [];
    private IBuffer? _buffer;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride => Width * 4;
    public ReadOnlySpan<byte> Pixels => _pixels.AsSpan(0, Stride * Height);

    /// <summary>Takes the pixels of <paramref name="bitmap"/> as packed BGRA8. Converts first if the camera sent another format.</summary>
    public void CopyFrom(SoftwareBitmap bitmap)
    {
        SoftwareBitmap? converted = null;
        try
        {
            var source = bitmap;
            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                source = converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            int needed = source.PixelWidth * source.PixelHeight * 4;
            if (_pixels.Length != needed)
            {
                _pixels = new byte[needed];
                _buffer = _pixels.AsBuffer();
            }
            source.CopyToBuffer(_buffer);
            Width = source.PixelWidth;
            Height = source.PixelHeight;
        }
        finally { converted?.Dispose(); }
    }

    /// <summary>Wipes the last frame from memory.</summary>
    public void Clear()
    {
        Array.Clear(_pixels);
        Width = Height = 0;
    }
}
