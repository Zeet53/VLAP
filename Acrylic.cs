using System;
using System.Runtime.InteropServices;

namespace VLAP;

/// <summary>
/// Real, live acrylic behind the window — the same system material Windows Terminal uses.
/// Windows 11's newer "system backdrop" attribute (DWMWA_SYSTEMBACKDROP_TYPE) is accepted but
/// never painted for this borderless WPF window (verified: flat grey/black, no blur), so the
/// window uses the accent-policy acrylic instead: it needs a layered window and keeps updating
/// with whatever is actually underneath.
/// </summary>
public static class Acrylic
{
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_DISABLED = 0;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    /// <summary>
    /// Tint alpha of the frosted look. Windows gives the material one fixed blur radius and no way
    /// to change it, so the glass is not adjustable: this one value is the whole look.
    /// </summary>
    private const int FrostTint = 102;

    /// <summary>Theme colour laid over the blurred backdrop, as 0x00BBGGRR (alpha is added per call).</summary>
    private const int DarkRgb = 0x001C1714;  // #14171C
    private const int LightRgb = 0x00FFFFFF; // white

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public int AccentState, AccentFlags, GradientColor, AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    private static int _lastAccent = int.MinValue;

    /// <summary>True when the system actually paints a live backdrop; false means use the solid colour.</summary>
    public static bool Apply(IntPtr hwnd, bool light, bool acrylic)
    {
        if (hwnd == IntPtr.Zero) return false;

        if (!acrylic)
        {
            _lastAccent = int.MinValue;
            Accent(hwnd, ACCENT_DISABLED, 0);
            return false;
        }

        var gradient = (FrostTint << 24) | (light ? LightRgb : DarkRgb);

        // switching themes calls this again; only touch DWM when the colour actually changes
        if (gradient == _lastAccent) return true;

        var ok = Accent(hwnd, ACCENT_ENABLE_ACRYLICBLURBEHIND, gradient);
        if (ok) _lastAccent = gradient;
        SettingsStore.Log("acrylic: " + (ok ? "on" : "failed") + (light ? " light" : " dark") + " tint=" + FrostTint);
        return ok;
    }

    private static bool Accent(IntPtr hwnd, int state, int gradient)
    {
        try
        {
            var policy = new AccentPolicy
            {
                AccentState = state,
                AccentFlags = 2,
                GradientColor = gradient,
                AnimationId = 0
            };

            var size = Marshal.SizeOf<AccentPolicy>();
            var memory = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, memory, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WCA_ACCENT_POLICY,
                    Data = memory,
                    SizeOfData = size
                };
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(memory);
            }
        }
        catch (Exception ex)
        {
            SettingsStore.Log("accent policy: " + ex.Message);
            return false;
        }
    }
}
