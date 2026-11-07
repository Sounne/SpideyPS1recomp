using RecompOne.Runtime.Config;
using Silk.NET.Windowing;

namespace RecompOne.Runtime.Host;

/// <summary>
/// Frame rate and VSync settings, and the unlimited frame rate presentation.
///
/// At an unlimited frame rate the game keeps its own clock: it still runs one update
/// per game frame on the console ticks. What changes is what the host does while the
/// game waits for its next tick. The frame the game just finished is closed, and until
/// the tick is due the host keeps presenting it through the FrameInterpolator at the
/// fraction of a game frame that has passed, so motion advances on every refresh of
/// the display instead of every 33 or 40 ms. The real frame is put back in VRAM before
/// the game runs again. Each picture is one game frame behind the game, as with any
/// interpolation between two known frames.
///
///     RECOMP_FRAMERATE=original|doubled|unlimited   override the frame rate setting
///     RECOMP_VSYNC=0|1|adaptive                      override the VSync setting
///     RECOMP_FPS_LIMIT=n                             override the frame limit (0: none)
/// </summary>
public static partial class HostWindow
{
    static FrameRateMode _frameRate = FrameRateMode.Doubled;
    static VSyncMode _vsyncMode;
    static double _fpsLimitMs;
    static bool _adaptiveSupported, _adaptivePending;

    static readonly string? FrameRateOverride = Environment.GetEnvironmentVariable("RECOMP_FRAMERATE");
    static readonly string? VSyncOverride = Environment.GetEnvironmentVariable("RECOMP_VSYNC");
    static readonly string? FpsLimitOverride = Environment.GetEnvironmentVariable("RECOMP_FPS_LIMIT");

    /// <summary>The graphics driver can sync to the monitor but tear on a late frame.</summary>
    public static bool AdaptiveVSyncSupported => _adaptiveSupported;

    /// <summary>
    /// Frames are presented between console ticks: the frame rate is unlimited and the
    /// interpolating backend is the one in use.
    /// </summary>
    public static bool Uncapped =>
        _frameRate == FrameRateMode.Unlimited && !_headless && _window != null &&
        _interpolator is { } ip && Hle.GpuHle.Backend == ip;

    static void ApplyFrameRateSettings()
    {
        var view = ConfigManager.View;
        var mode = view.FrameRate;
        if (Enum.TryParse(FrameRateOverride, true, out FrameRateMode fm) && Enum.IsDefined(fm)) mode = fm;
        var vsync = view.VSyncMode;
        if (VSyncOverride is "0") vsync = VSyncMode.Off;
        else if (VSyncOverride is "1") vsync = VSyncMode.On;
        else if (string.Equals(VSyncOverride, "adaptive", StringComparison.OrdinalIgnoreCase)) vsync = VSyncMode.Adaptive;
        int limit = int.TryParse(FpsLimitOverride, out int l) ? l : view.FpsLimit;

        SetFpsLimit(limit);
        SetVSync(vsync);
        SetFrameRate(mode);
    }

    public static void SetFrameRate(FrameRateMode mode)
    {
        _frameRate = mode;
        _interpActive = false;
        _haveAnchor = false;
        SetInterpolation(mode != FrameRateMode.Original);
        Console.WriteLine($"[Host] frame rate {mode}" +
                          (mode == FrameRateMode.Unlimited && _fpsLimitMs > 0 ? $", limit {1000.0 / _fpsLimitMs:F0} FPS" : ""));
    }

    public static void SetFpsLimit(int fps) => _fpsLimitMs = fps > 0 ? 1000.0 / Math.Clamp(fps, 20, 1000) : 0;

    public static void SetVSync(VSyncMode mode)
    {
        if (mode == VSyncMode.Adaptive && !_adaptiveSupported && _window != null) mode = VSyncMode.On;
        _vsyncMode = mode;
        if (_window != null) _window.VSync = mode != VSyncMode.Off;
        // Silk applies VSync as a swap interval of 1 on its next render; the adaptive
        // interval (-1) is set after that, from inside the render callback.
        _adaptivePending = mode == VSyncMode.Adaptive;
        FrameClock.VSync = mode != VSyncMode.Off;
        FrameClock.Resync();
    }

    static void DetectAdaptiveVSync()
    {
        try
        {
            var glfw = Silk.NET.GLFW.Glfw.GetApi();
            _adaptiveSupported = _window?.Native?.Glfw != null &&
                (glfw.ExtensionSupported("WGL_EXT_swap_control_tear") ||
                 glfw.ExtensionSupported("GLX_EXT_swap_control_tear"));
        }
        catch (Exception e)
        {
            _adaptiveSupported = false;
            Console.WriteLine($"[Host] adaptive VSync unavailable: {e.Message}");
        }
    }

    /// <summary>Called first in the render callback, after Silk has applied its own swap interval.</summary>
    static void ApplyPendingSwapInterval()
    {
        if (!_adaptivePending) return;
        _adaptivePending = false;
        try { Silk.NET.GLFW.Glfw.GetApi().SwapInterval(-1); }
        catch (Exception e) { Console.WriteLine($"[Host] adaptive VSync failed: {e.Message}"); }
    }

    // Unlimited presentation state. Times are FrameClock milliseconds.
    static double _anchorMs, _spanMs, _lastShownMs = double.MinValue;
    static bool _haveAnchor, _interpActive, _finalShown, _shownSinceGameTick;

    /// <summary>
    /// Present until the console tick due at <paramref name="deadline"/>. Called on every
    /// tick before the wait. A frame the game drew since the last tick is closed first;
    /// its interpolation then runs from the previous frame's positions at the tick that
    /// closed it to its own one game frame later.
    /// </summary>
    public static void PresentUntil(Gpu? gpu, double deadline)
    {
        var ip = _interpolator!;
        if (ip.PendingOps > 0) CloseGameFrame(ip, gpu, deadline);
        if (!_interpActive) return;
        if (MovieFrameActive || gpu is not { DisplayEnabled: true } || !ip.LastFrameIntact ||
            ip.PendingOps > 0 || _glBackend is not { Ready: true })
        {
            // The screen now holds something the recording does not: present it the
            // ordinary way from here on.
            _interpActive = false;
            return;
        }
        // Nobody can see a minimised window; do not spend the GPU drawing it.
        if (_window!.WindowState == WindowState.Minimized) return;

        bool replayed = false;
        // Presenting runs the settings UI, which can switch the frame rate mid-loop.
        while (_interpActive)
        {
            double now = FrameClock.NowMs;
            if (now >= deadline) break;
            if (_fpsLimitMs > 0)
            {
                double slot = _lastShownMs + _fpsLimitMs;
                if (slot >= deadline) break;
                if (slot > now)
                {
                    FrameClock.WaitUntilMs(slot);
                    now = slot;
                }
            }

            double t = (now - _anchorMs) / _spanMs;
            if (t >= 1)
            {
                // The game is late with its next frame: show the real one, then hold it.
                if (_finalShown) break;
                _finalShown = true;
                t = 1;
            }
            else if (t < 0) t = 0;

            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            ip.Replay((float)t);
            replayed |= t < 1;
            Present(gpu);
            NoteSwap(start);
            _lastShownMs = now;
            _shownSinceGameTick = true;
        }
        // Put the real frame back where the game and VRAM readers expect it.
        if (replayed) ip.Replay(1f);
    }

    static void CloseGameFrame(Hle.FrameInterpolator ip, Gpu? gpu, double deadline)
    {
        double vblank = FrameClock.VBlankPeriodMs;
        // Anchor on the console's tick grid, not on when the game happened to finish,
        // so the in-between positions advance evenly.
        double anchor = deadline - vblank;
        _spanMs = _haveAnchor
            ? Math.Clamp(anchor - _anchorMs, vblank, 6 * vblank)
            : vblank * Math.Max(2, FrameClock.VBlanksPerFrame);
        _anchorMs = anchor;
        _haveAnchor = true;

        // Always close, so drawing never piles up across frames.
        bool closed = ip.EndFrame();
        bool shown = gpu is { DisplayEnabled: true } && !MovieFrameActive;
        _interpActive = closed && shown && _glBackend is { Ready: true };
        _finalShown = false;
        if (_interpActive || !shown) return;

        // Nothing to interpolate from (a menu that draws over the last picture, the
        // first frame after a cut): show the real frame now.
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        Present(gpu);
        NoteSwap(start);
        _lastShownMs = FrameClock.NowMs;
        _shownSinceGameTick = true;
    }

    /// <summary>
    /// On the tick that would present a game frame: true when the unlimited presentation
    /// already showed it, so the ordinary present is skipped.
    /// </summary>
    public static bool TakeUncappedPresented()
    {
        bool shown = _shownSinceGameTick;
        _shownSinceGameTick = false;
        return shown;
    }
}
