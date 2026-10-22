using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OpenCvSharp;

namespace WebcamFbt.Mac.Rendering;

/// <summary>
/// OpenCV の画像（BGR）を Avalonia の WriteableBitmap に写す（Windows 版の WpfExtensions の代わり）。
/// 同じビットマップに書き直しても Image が再描画されないので、2 枚を交互に使い、
/// 書いた方を毎回別のオブジェクトとして返す。UI スレッドから呼ぶこと。
/// </summary>
public sealed class MatBitmap : IDisposable
{
    private readonly WriteableBitmap?[] _buffers = new WriteableBitmap?[2];
    private readonly Mat _bgra = new();
    private int _next;

    /// <summary>画像を書き込んだビットマップを返す。</summary>
    public WriteableBitmap Update(Mat bgr)
    {
        Cv2.CvtColor(bgr, _bgra, ColorConversionCodes.BGR2BGRA);

        var bmp = _buffers[_next];
        if (bmp == null || bmp.PixelSize.Width != bgr.Width || bmp.PixelSize.Height != bgr.Height)
        {
            bmp?.Dispose();
            bmp = new WriteableBitmap(
                new PixelSize(bgr.Width, bgr.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _buffers[_next] = bmp;
        }

        using (var fb = bmp.Lock())
        {
            int rowBytes = bgr.Width * 4;
            int height = bgr.Height;
            unsafe
            {
                for (int y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(
                        (void*)_bgra.Ptr(y),
                        (void*)(fb.Address + y * fb.RowBytes),
                        fb.RowBytes,
                        rowBytes);
                }
            }
        }

        _next = 1 - _next;
        return bmp;
    }

    public void Dispose()
    {
        // 表示中のビットマップは Image が参照しているので、ここでは Mat だけ解放する
        _bgra.Dispose();
    }
}
