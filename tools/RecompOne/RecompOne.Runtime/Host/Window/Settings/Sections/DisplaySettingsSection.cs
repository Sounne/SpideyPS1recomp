using ImGuiNET;
using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Host.Window;

internal sealed class DisplaySettingsSection : ISettingsSection
{
    public string Id => "display";
    public string TitleKey => "settings.display";
    public int Order => 5;

    public void Draw()
    {
        if (Hle.GpuHle.WidescreenSupported)
        {
            bool wide = ConfigManager.Game.Widescreen ?? Hle.GpuHle.WidescreenDefault;
            string preview = Localization.T(wide
                ? "settings.display.aspect_wide"
                : "settings.display.aspect_standard");

            ImGui.TextUnformatted(Localization.T("settings.display.aspect"));
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.BeginCombo("##display-aspect", preview))
            {
                if (ImGui.Selectable(Localization.T("settings.display.aspect_standard"), !wide))
                {
                    ConfigManager.Game.Widescreen = false;
                    ConfigManager.SaveGame();
                    OutputPanel.RequestWindowAspect(Hle.GpuHle.BaseAspect);
                }
                if (ImGui.Selectable(Localization.T("settings.display.aspect_wide"), wide))
                {
                    ConfigManager.Game.Widescreen = true;
                    ConfigManager.SaveGame();
                    OutputPanel.RequestWindowAspect(16f / 9f);
                }
                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Localization.T("settings.display.aspect_hint"));
            ImGui.Spacing();
        }

        bool fullscreen = ConfigManager.View.Fullscreen;
        if (ImGui.Checkbox(Localization.T("settings.display.fullscreen"), ref fullscreen))
        {
            ConfigManager.View.Fullscreen = fullscreen;
            HostWindow.SetFullscreen(fullscreen);
            ConfigManager.SaveView(PanelManager.Panels);
        }

        ImGui.Spacing();
        DrawFrameRate();
        DrawVSync();
        if (ConfigManager.View.FrameRate == FrameRateMode.Unlimited) DrawFpsLimit();
        ImGui.Spacing();

        bool fxaa = ConfigManager.View.Fxaa;
        if (ImGui.Checkbox(Localization.T("settings.display.fxaa"), ref fxaa))
        {
            ConfigManager.View.Fxaa = fxaa;
            Hle.GpuHle.FxaaEnabled = fxaa;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.T("settings.display.fxaa_hint"));

        int scale = ConfigManager.View.RenderScale;
        if (ImGui.SliderInt(Localization.T("settings.display.render_scale"), ref scale, 1, 8, "%dx"))
        {
            ConfigManager.View.RenderScale = scale;
            ConfigManager.SaveView(PanelManager.Panels);
            NoticePopup.Show(Localization.T("common.restart_required"));
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.T("settings.display.render_scale_hint"));

        int lines = Hle.GpuHle.LastDisplayH;
        int width = Hle.GpuHle.LastDisplayW;
        if (lines > 0)
            ImGui.TextDisabled(Localization.T("settings.display.render_scale_lines",
                width, lines, width * scale, lines * scale, scale));

        if (scale != Hle.GlVram.Scale)
            ImGui.TextDisabled(Localization.T("settings.display.restart_pending"));

        ImGui.Separator();
        ImGui.TextDisabled(Localization.T("settings.display.backend_running", Hle.GpuBackendFactory.Selected));
    }

    static readonly int[] FpsLimits = [0, 30, 60, 75, 90, 120, 144, 165, 240, 360];

    /// <summary>The game's own frame rate: 30 FPS on NTSC discs, 25 on PAL.</summary>
    static int GameFps => (int)Math.Round(Runtime.VBlankHz / Math.Max(2, Runtime.VBlanksPerFrame));

    static string FrameRateLabel(FrameRateMode mode) => mode switch
    {
        FrameRateMode.Original => Localization.T("settings.display.framerate_original", GameFps),
        FrameRateMode.Doubled => Localization.T("settings.display.framerate_doubled", GameFps * 2),
        _ => Localization.T("settings.display.framerate_unlimited"),
    };

    static string FrameRateHint(FrameRateMode mode) => mode switch
    {
        FrameRateMode.Original => Localization.T("settings.display.framerate_original_hint"),
        FrameRateMode.Doubled => Localization.T("settings.display.framerate_doubled_hint"),
        _ => Localization.T("settings.display.framerate_unlimited_hint"),
    };

    static void DrawFrameRate()
    {
        var current = ConfigManager.View.FrameRate;
        ImGui.TextUnformatted(Localization.T("settings.display.framerate"));
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("##display-framerate", FrameRateLabel(current)))
        {
            foreach (var mode in Enum.GetValues<FrameRateMode>())
            {
                if (ImGui.Selectable(FrameRateLabel(mode), mode == current) && mode != current)
                {
                    ConfigManager.View.FrameRate = mode;
                    HostWindow.SetFrameRate(mode);
                    ConfigManager.SaveView(PanelManager.Panels);
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(FrameRateHint(mode));
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(FrameRateHint(current));
    }

    static string VSyncLabel(VSyncMode mode) => mode switch
    {
        VSyncMode.Off => Localization.T("settings.display.vsync_off"),
        VSyncMode.On => Localization.T("settings.display.vsync_on"),
        _ => Localization.T("settings.display.vsync_adaptive"),
    };

    static void DrawVSync()
    {
        var current = ConfigManager.View.VSyncMode;
        if (current == VSyncMode.Adaptive && !HostWindow.AdaptiveVSyncSupported) current = VSyncMode.On;
        ImGui.TextUnformatted(Localization.T("settings.display.vsync"));
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("##display-vsync", VSyncLabel(current)))
        {
            foreach (var mode in Enum.GetValues<VSyncMode>())
            {
                bool supported = mode != VSyncMode.Adaptive || HostWindow.AdaptiveVSyncSupported;
                if (ImGui.Selectable(VSyncLabel(mode), mode == current,
                        supported ? ImGuiSelectableFlags.None : ImGuiSelectableFlags.Disabled) && mode != current)
                {
                    ConfigManager.View.VSyncMode = mode;
                    HostWindow.SetVSync(mode);
                    ConfigManager.SaveView(PanelManager.Panels);
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(Localization.T(mode switch
                    {
                        VSyncMode.Off => "settings.display.vsync_off_hint",
                        VSyncMode.On => "settings.display.vsync_hint",
                        _ => supported ? "settings.display.vsync_adaptive_hint" : "settings.display.vsync_adaptive_unsupported",
                    }));
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.T("settings.display.vsync_hint"));
    }

    static string FpsLimitLabel(int fps) =>
        fps > 0 ? $"{fps} FPS" : Localization.T("settings.display.fps_limit_none");

    static void DrawFpsLimit()
    {
        int current = ConfigManager.View.FpsLimit;
        ImGui.TextUnformatted(Localization.T("settings.display.fps_limit"));
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("##display-fps-limit", FpsLimitLabel(current)))
        {
            foreach (int fps in FpsLimits)
            {
                if (ImGui.Selectable(FpsLimitLabel(fps), fps == current) && fps != current)
                {
                    ConfigManager.View.FpsLimit = fps;
                    HostWindow.SetFpsLimit(fps);
                    ConfigManager.SaveView(PanelManager.Panels);
                }
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.T("settings.display.fps_limit_hint"));
    }
}
