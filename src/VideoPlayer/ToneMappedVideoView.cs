using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.OpenGL;
using Avalonia.Platform;
using HanumanInstitute.LibMpv.Avalonia;
using SkiaSharp;
using VideoPlayer.Core;
using static Avalonia.OpenGL.GlConsts;

namespace VideoPlayer;

/// <summary>Captures source-sized frames using the same HDR tone mapping as playback.</summary>
public sealed class ToneMappedVideoView : OpenGlView
{
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(10);
    private CaptureRequest? _captureRequest;

    public async Task CaptureFrameAsync(
        string path,
        bool includeSubtitles = false,
        bool optimized = false,
        Action<Bitmap>? onPreviewCaptured = null)
    {
        VerifyAccess();
        if (_captureRequest is not null) throw new InvalidOperationException("A frame capture is already pending.");
        if (!MpvContext.IsCustomRendering()) throw new InvalidOperationException("The video renderer is not ready.");

        var width = MpvContext.GetProperty<int>("video-out-params/dw");
        var height = MpvContext.GetProperty<int>("video-out-params/dh");
        if (width <= 0 || height <= 0) throw new InvalidOperationException("No video frame is available.");
        var subtitlesVisible = MpvContext.GetProperty<bool>("sub-visibility");
        var osdLevel = MpvContext.GetProperty<long>("osd-level");
        var aspect = MpvContext.GetProperty<string>("video-aspect-override");
        var keepAspect = MpvContext.GetProperty<bool>("keepaspect");
        var panscan = MpvContext.GetProperty<double>("panscan");
        var settings = new CaptureSettings(subtitlesVisible, osdLevel, aspect, keepAspect, panscan);
        var request = new CaptureRequest(width, height, settings);
        byte[] pixels;
        try
        {
            // Capture-only settings are restored inside the render callback so playback
            // can draw its next frame without showing the temporary capture layout.
            MpvContext.SetProperty("sub-visibility", includeSubtitles && subtitlesVisible);
            MpvContext.SetProperty("osd-level", 0L);
            MpvContext.SetPropertyString("video-aspect-override", "no");
            MpvContext.SetProperty("keepaspect", true);
            MpvContext.SetProperty("panscan", 0.0);
            _captureRequest = request;
            RequestNextFrameRendering();
            pixels = await request.Completion.Task.WaitAsync(CaptureTimeout);
        }
        finally
        {
            _captureRequest = null;
            RestoreCaptureSettings(settings);
            RequestNextFrameRendering();
        }

        if (onPreviewCaptured is not null)
        {
            var previewThumbnail = CreatePreviewThumbnail(pixels, width, height, 160);
            onPreviewCaptured(previewThumbnail);
        }

        await Task.Run(() =>
        {
            if (optimized)
            {
                SaveHighQualityJpeg(pixels, width, height, path);
                return;
            }

            using var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormats.Rgba8888, AlphaFormat.Opaque);
            using (var framebuffer = bitmap.Lock())
            {
                var stride = checked(width * 4);
                for (var row = 0; row < height; row++)
                    Marshal.Copy(pixels, row * stride, framebuffer.Address + row * framebuffer.RowBytes, stride);
            }
            SaveSrgbPng(bitmap, path);
        });
    }

    private static Bitmap CreatePreviewThumbnail(byte[] pixels, int width, int height, int targetWidth)
    {
        var targetHeight = Math.Max(1, (int)Math.Round((double)height * targetWidth / width));
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info);
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);

        var targetInfo = new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var scaled = bitmap.Resize(targetInfo, SKSamplingOptions.Default)
            ?? throw new InvalidOperationException("The image resizer could not create a thumbnail.");

        using var encoded = scaled.Encode(SKEncodedImageFormat.Jpeg, 80)
            ?? throw new InvalidOperationException("The image encoder could not create a JPEG preview.");

        using var stream = encoded.AsStream();
        return new Bitmap(stream);
    }

    private static void SaveHighQualityJpeg(byte[] pixels, int width, int height, string path)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, ScreenshotSaveModeRules.OptimizedJpegQuality)
            ?? throw new InvalidOperationException("The image encoder could not create a JPEG screenshot.");
        using var output = File.Create(path);
        encoded.SaveTo(output);
    }

    public async Task<Bitmap> CapturePreviewFrameAsync(int targetWidth)
    {
        VerifyAccess();
        if (_captureRequest is not null) throw new InvalidOperationException("A frame capture is already pending.");
        if (!MpvContext.IsCustomRendering()) throw new InvalidOperationException("The video renderer is not ready.");
        if (targetWidth <= 0) throw new ArgumentOutOfRangeException(nameof(targetWidth));

        var sourceWidth = MpvContext.GetProperty<int>("video-out-params/dw");
        var sourceHeight = MpvContext.GetProperty<int>("video-out-params/dh");
        if (sourceWidth <= 0 || sourceHeight <= 0) throw new InvalidOperationException("No video frame is available.");
        var width = Math.Min(targetWidth, sourceWidth);
        var height = Math.Max(1, (int)Math.Round((double)sourceHeight * width / sourceWidth));
        var request = new CaptureRequest(width, height);
        byte[] pixels;
        try
        {
            _captureRequest = request;
            RequestNextFrameRendering();
            pixels = await request.Completion.Task.WaitAsync(CaptureTimeout);
        }
        finally
        {
            _captureRequest = null;
            RequestNextFrameRendering();
        }

        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormats.Rgba8888, AlphaFormat.Opaque);
        using (var framebuffer = bitmap.Lock())
        {
            var stride = checked(width * 4);
            for (var row = 0; row < height; row++)
                Marshal.Copy(pixels, row * stride, framebuffer.Address + row * framebuffer.RowBytes, stride);
        }
        return bitmap;
    }

    protected override void OnOpenGlRender(GlInterface gl, int fbo)
    {
        if (_captureRequest is { } request)
        {
            if (request.Completion.Task.IsCompleted) return;
            byte[]? pixels = null;
            Exception? captureError = null;
            try { pixels = RenderCapture(gl, fbo, request.Width, request.Height); }
            catch (Exception exception) { captureError = exception; }

            if (request.Settings is { } settings) RestoreCaptureSettings(settings);
            base.OnOpenGlRender(gl, fbo);
            if (captureError is null) request.Completion.TrySetResult(pixels!);
            else request.Completion.TrySetException(captureError);
            return;
        }
        base.OnOpenGlRender(gl, fbo);
    }

    private void RestoreCaptureSettings(CaptureSettings settings)
    {
        MpvContext.SetPropertyString("video-aspect-override", settings.Aspect);
        MpvContext.SetProperty("keepaspect", settings.KeepAspect);
        MpvContext.SetProperty("panscan", settings.Panscan);
        MpvContext.SetProperty("osd-level", settings.OsdLevel);
        MpvContext.SetProperty("sub-visibility", settings.SubtitlesVisible);
    }

    private unsafe byte[] RenderCapture(GlInterface gl, int windowFramebuffer, int width, int height)
    {
        var texture = gl.GenTexture();
        var framebuffer = gl.GenFramebuffer();
        try
        {
            gl.BindTexture(GL_TEXTURE_2D, texture);
            gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
            gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
            gl.TexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, width, height, 0, GL_RGBA, GL_UNSIGNED_BYTE, IntPtr.Zero);
            gl.BindFramebuffer(GL_FRAMEBUFFER, framebuffer);
            gl.FramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, texture, 0);
            if (gl.CheckFramebufferStatus(GL_FRAMEBUFFER) != GL_FRAMEBUFFER_COMPLETE)
                throw new InvalidOperationException("The GPU could not allocate a screenshot framebuffer.");

            // Readback starts at the bottom of the GL framebuffer. The normal
            // unflipped mpv render puts the source's first row there.
            MpvContext.OpenGlRender(width, height, framebuffer, flipY: 0);
            gl.BindFramebuffer(GL_FRAMEBUFFER, framebuffer);
            var pixels = new byte[checked(width * height * 4)];
            var readPixels = Marshal.GetDelegateForFunctionPointer<ReadPixels>(gl.GetProcAddress("glReadPixels"));
            fixed (byte* pointer = pixels)
                readPixels(0, 0, width, height, GL_RGBA, GL_UNSIGNED_BYTE, pointer);
            if (gl.GetError() != GL_NO_ERROR) throw new InvalidOperationException("GPU screenshot readback failed.");
            return pixels;
        }
        finally
        {
            gl.BindFramebuffer(GL_FRAMEBUFFER, windowFramebuffer);
            gl.DeleteFramebuffer(framebuffer);
            gl.DeleteTexture(texture);
        }
    }

    private static void SaveSrgbPng(Bitmap bitmap, string path)
    {
        // Avalonia's PNG encoder omits color metadata. Insert a standard sRGB
        // chunk (perceptual intent, including its CRC) immediately after IHDR.
        const int pngHeaderLength = 33;
        ReadOnlySpan<byte> srgbChunk = [0, 0, 0, 1, (byte)'s', (byte)'R', (byte)'G', (byte)'B', 0, 174, 206, 28, 233];
        using var encoded = new MemoryStream();
        bitmap.Save(encoded);
        using var output = File.Create(path);
        output.Write(encoded.GetBuffer().AsSpan(0, pngHeaderLength));
        output.Write(srgbChunk);
        encoded.Position = pngHeaderLength;
        encoded.CopyTo(output);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _captureRequest?.Completion.TrySetException(new InvalidOperationException("The video renderer closed during capture."));
        base.OnOpenGlDeinit(gl);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private unsafe delegate void ReadPixels(int x, int y, int width, int height, int format, int type, void* pixels);

    private sealed record CaptureSettings(bool SubtitlesVisible, long OsdLevel, string Aspect, bool KeepAspect, double Panscan);

    private sealed record CaptureRequest(int Width, int Height, CaptureSettings? Settings = null)
    {
        public TaskCompletionSource<byte[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
