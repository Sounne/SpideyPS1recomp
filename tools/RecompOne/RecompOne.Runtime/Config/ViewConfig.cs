using System.Globalization;

namespace RecompOne.Runtime.Config;

public class PanelState
{
    public bool Open { get; set; }
}

/// <summary>How often the host presents a picture of the game.</summary>
public enum FrameRateMode
{
    /// <summary>One picture per game frame, as on the console.</summary>
    Original,
    /// <summary>Twice the game's rate, with an interpolated frame between each pair.</summary>
    Doubled,
    /// <summary>As many interpolated frames as the display takes.</summary>
    Unlimited,
}

public enum VSyncMode
{
    Off,
    On,
    /// <summary>Synced, but a late frame is shown at once instead of waiting a refresh.</summary>
    Adaptive,
}

public class ViewConfig
{
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PanelState> Panels { get; set; } = [];

    public bool GetBool(string key, bool fallback = false)
        => Values.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;

    public void SetBool(string key, bool value) => Values[key] = value.ToString();

    public int GetInt(string key, int fallback = 0)
        => Values.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;

    public void SetInt(string key, int value) => Values[key] = value.ToString(CultureInfo.InvariantCulture);

    public float GetFloat(string key, float fallback = 0f)
        => Values.TryGetValue(key, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback;

    public void SetFloat(string key, float value) => Values[key] = value.ToString(CultureInfo.InvariantCulture);

    public string GetString(string key, string fallback = "")
        => Values.TryGetValue(key, out var v) ? v : fallback;

    public void SetString(string key, string value) => Values[key] = value;

    public bool HideTopBar
    {
        get => GetBool("HideTopBar");
        set => SetBool("HideTopBar", value);
    }

    public bool AutoHideMenuBar
    {
        get => GetBool("AutoHideMenuBar");
        set => SetBool("AutoHideMenuBar", value);
    }

    public bool Fullscreen
    {
        get => GetBool("Fullscreen");
        set => SetBool("Fullscreen", value);
    }

    public int RenderScale
    {
        get
        {
            int v = (int)GetFloat("RenderScale", 4f);
            return v < 1 ? 1 : v > 8 ? 8 : v;
        }
        set => SetFloat("RenderScale", value < 1 ? 1 : value > 8 ? 8 : value);
    }

    public VSyncMode VSyncMode
    {
        get => Enum.TryParse(GetString("VSyncMode"), true, out VSyncMode m) && Enum.IsDefined(m)
            ? m
            : GetBool("VSync") ? VSyncMode.On : VSyncMode.Off;
        set
        {
            SetString("VSyncMode", value.ToString());
            SetBool("VSync", value != VSyncMode.Off);
        }
    }

    public bool VSync
    {
        get => VSyncMode != VSyncMode.Off;
        set => VSyncMode = value ? VSyncMode.On : VSyncMode.Off;
    }

    public FrameRateMode FrameRate
    {
        get
        {
            if (Enum.TryParse(GetString("FrameRate"), true, out FrameRateMode m) && Enum.IsDefined(m)) return m;
            // Earlier builds had a single "60 FPS (interpolated)" checkbox, on by default.
            return Values.ContainsKey("Interpolate60") && !GetBool("Interpolate60", true)
                ? FrameRateMode.Original
                : FrameRateMode.Unlimited;
        }
        set => SetString("FrameRate", value.ToString());
    }

    /// <summary>Most frames presented per second at an unlimited frame rate; 0 for no limit.</summary>
    public int FpsLimit
    {
        get => GetInt("FpsLimit") is var v && v > 0 ? Math.Clamp(v, 20, 1000) : 0;
        set => SetInt("FpsLimit", value > 0 ? Math.Clamp(value, 20, 1000) : 0);
    }

    public bool Fxaa
    {
        get => GetBool("Fxaa", true);
        set => SetBool("Fxaa", value);
    }

    public int WindowWidth
    {
        get => Math.Clamp(GetInt("WindowWidth", 1280), 320, 7680);
        set => SetInt("WindowWidth", Math.Clamp(value, 320, 7680));
    }

    public int WindowHeight
    {
        get => Math.Clamp(GetInt("WindowHeight", 960), 240, 4320);
        set => SetInt("WindowHeight", Math.Clamp(value, 240, 4320));
    }

    public string Language
    {
        get => GetString("Language");
        set => SetString("Language", value);
    }

    public string Accent
    {
        get => GetString("Accent");
        set => SetString("Accent", value);
    }

    public string Background
    {
        get => GetString("Background");
        set => SetString("Background", value);
    }

    public float UiScale
    {
        get => Math.Clamp(GetFloat("UiScale", 1f), 0.5f, 3f);
        set => SetFloat("UiScale", Math.Clamp(value, 0.5f, 3f));
    }
}
