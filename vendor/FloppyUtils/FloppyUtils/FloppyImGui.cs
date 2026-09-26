using Dalamud.Bindings.ImGui;
using System;
using System.Numerics;

namespace FloppyUtils;

/// <summary>
/// Extensions which I couldn't find in Dalamud but feel like they would totally belong.
/// </summary>
public static unsafe class FloppyImGui
{
    /// <summary>
    /// Turns an ImGui RGBA int to a Vector4. This does the opposite of <see cref="ImGui.GetColorU32(uint)"/>.
    /// </summary>
    /// <param name="rgba"></param>
    /// <returns></returns>
    public static Vector4 ToXYZW01(this uint rgba)
    {
        return new Vector4(
            ((rgba >> 0) & 0xFF) / (float) 0xFF,
            ((rgba >> 8) & 0xFF) / (float) 0xFF,
            ((rgba >> 16) & 0xFF) / (float) 0xFF,
            ((rgba >> 24) & 0xFF) / (float) 0xFF
        );
    }

    /// <summary>
    /// Turns an uint BGRA int to a Vector4.
    /// </summary>
    /// <param name="brga"></param>
    /// <returns></returns>
    public static Vector4 ToZYXW01(this uint brga)
    {
        return new Vector4(
            ((brga >> 16) & 0xFF) / (float) 0xFF,
            ((brga >> 8) & 0xFF) / (float) 0xFF,
            ((brga >> 0) & 0xFF) / (float) 0xFF,
            ((brga >> 24) & 0xFF) / (float) 0xFF
        );
    }

    /// <summary>
    /// <see cref="ImGui.GetColorU32(uint)"/> but for ZXYW-formatted vectors.
    /// </summary>
    /// <param name="bgra"></param>
    /// <returns></returns>
    public static uint GetColorZYXWU32(Vector4 bgra)
    {
        return ImGui.GetColorU32(new Vector4(bgra.Z, bgra.Y, bgra.X, bgra.W));
    }

    /// <summary>
    /// Determines whether the given color is dark (f.e. bg needs white text) or not (f.e. bg needs black text).
    /// </summary>
    /// <remarks>
    /// Based on W3C guidelines according to some internet comments:tm:
    /// </remarks>
    /// <param name="bg"></param>
    /// <returns></returns>
    public static bool GetIsDark(this Vector4 bg)
    {
        bg.X = bg.X <= 0.04045f ? bg.X / 12.92f : MathF.Pow((bg.X + 0.055f) / 1.055f, 2.4f);
        bg.Y = bg.Y <= 0.04045f ? bg.Y / 12.92f : MathF.Pow((bg.Y + 0.055f) / 1.055f, 2.4f);
        bg.Z = bg.Z <= 0.04045f ? bg.Z / 12.92f : MathF.Pow((bg.Z + 0.055f) / 1.055f, 2.4f);
        return 0.2126f * bg.X + 0.7152f * bg.Y + 0.0722 * bg.Z <= 0.179f;
    }

    /// <summary>
    /// https://github.com/ocornut/imgui/issues/942#issuecomment-268369298
    /// </summary>
    /// <param name="label"></param>
    /// <param name="value"></param>
    /// <param name="vMin"></param>
    /// <param name="vMax"></param>
    /// <param name="radius"></param>
    /// <param name="t"></param>
    /// <param name="tooltip"></param>
    /// <returns></returns>
    public static bool Knob(
        string label,
        ref float value,
        float vMin,
        float vMax,
        float radius = 20f,
        Func<float, string?>? tooltip = null
    )
    {
        var io = ImGui.GetIO();
        var style = ImGui.GetStyle();

        var pos = ImGui.GetCursorScreenPos();
        var center = new Vector2(pos.X + radius, pos.Y + radius);
        var lineHeight = ImGui.GetTextLineHeight();
        var drawList = ImGui.GetWindowDrawList();

        ImGui.InvisibleButton(label, new(radius * 2, radius * 2));
        bool valueChanged = false;
        var isActive = ImGui.IsItemActive();
        var isHovered = ImGui.IsItemHovered();

        var delta = io.MouseDelta.X - io.MouseDelta.Y;
        if (isActive && delta != 0.0f)
        {
            var step = (vMax - vMin) / 200.0f;
            value += delta * step;
            if (value < vMin)
            {
                value = vMin;
            }
            if (value > vMax)
            {
                value = vMax;
            }
            valueChanged = true;
        }

        const float ANGLE_MIN = MathF.PI * 0.75f;
        const float ANGLE_MAX = MathF.PI * 2.25f;

        var angle = ANGLE_MIN + (ANGLE_MAX - ANGLE_MIN) * ((value - vMin) / (vMax - vMin));
        var angleCos = MathF.Cos(angle);
        var angleSin = MathF.Sin(angle);
        var radiusInner = radius * 0.40f;

        drawList.AddCircleFilled(center, radius, ImGui.GetColorU32(ImGuiCol.FrameBg), 16);
        drawList.AddCircle(center, radius, ImGui.GetColorU32(ImGuiCol.Border), 16);
        drawList.AddLine(
            new(center.X + angleCos * radiusInner, center.Y + angleSin * radiusInner),
            new(center.X + angleCos * (radius - 2), center.Y + angleSin * (radius - 2)),
            ImGui.GetColorU32(ImGuiCol.SliderGrabActive),
            2.0f
        );
        drawList.AddCircleFilled(
            center,
            radiusInner,
            ImGui.GetColorU32(isActive ? ImGuiCol.FrameBgActive : isHovered ? ImGuiCol.FrameBgHovered : ImGuiCol.FrameBg),
            16
        );

        if (tooltip != null && (isActive || isHovered))
        {
            var text = tooltip(value);
            if (!string.IsNullOrEmpty(text))
            {
                ImGui.SetTooltip(text);
            }
        }

        return valueChanged;
    }
}
