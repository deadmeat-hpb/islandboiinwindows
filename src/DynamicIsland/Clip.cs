using System.Runtime.InteropServices;
using Microsoft.UI;

namespace DynamicIsland;

/// <summary>
/// What was just copied, as a "Copied" notice: a one-line text preview, or the file name(s). Images are left out (screenshots
/// already get their own island). Password managers mark their copies as private, and those are never shown; text that looks
/// like a password or key is shown only as its length.
/// </summary>
static class Clip
{
    const uint CF_UNICODETEXT = 13, CF_HDROP = 15;
    static readonly uint Exclude = Native.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    static readonly uint NoHistory = Native.RegisterClipboardFormat("CanIncludeInClipboardHistory");
    static readonly uint Ignore = Native.RegisterClipboardFormat("Clipboard Viewer Ignore");
    static readonly Windows.UI.Color Tint = ColorHelper.FromArgb(255, 150, 180, 255);

    public static HudEvent? Read(nint hwnd)
    {
        if (Native.IsClipboardFormatAvailable(Exclude) || Native.IsClipboardFormatAvailable(Ignore)) return null;
        bool text = Native.IsClipboardFormatAvailable(CF_UNICODETEXT), files = Native.IsClipboardFormatAvailable(CF_HDROP);
        if (!text && !files) return null;
        if (!Native.OpenClipboard(hwnd)) return null; // busy: another app holds it; skip this copy rather than wait
        try
        {
            if (Native.IsClipboardFormatAvailable(NoHistory) && Native.GetClipboardData(NoHistory) is var h && h != 0 && Native.GlobalLock(h) is var p && p != 0)
            {
                int allowed = Marshal.ReadInt32(p);
                Native.GlobalUnlock(h);
                if (allowed == 0) return null; // marked private by the app that copied it
            }
            string detail;
            if (files)
            {
                nint drop = Native.GetClipboardData(CF_HDROP);
                uint count = Native.DragQueryFile(drop, 0xFFFFFFFF, null, 0);
                if (count == 0) return null;
                var first = new System.Text.StringBuilder(260);
                Native.DragQueryFile(drop, 0, first, first.Capacity);
                string name = Path.GetFileName(first.ToString().TrimEnd('\\'));
                detail = count == 1 ? name : $"{name} and {count - 1} more";
            }
            else
            {
                nint mem = Native.GetClipboardData(CF_UNICODETEXT);
                nint ptr = mem == 0 ? 0 : Native.GlobalLock(mem);
                if (ptr == 0) return null;
                string s;
                try { s = Marshal.PtrToStringUni(ptr) ?? ""; }
                finally { Native.GlobalUnlock(mem); }
                detail = Preview(s);
                if (detail.Length == 0) return null;
            }
            return new HudEvent("", -1, "", "Copied", Tint, 1.6, null, HudKind.Clipboard, detail);
        }
        finally { Native.CloseClipboard(); }
    }

    /// <summary>One line of the text, or "16 characters" when it looks like a password or key.</summary>
    static string Preview(string s)
    {
        string flat = string.Join(' ', s.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length == 0) return "";
        bool secret = !flat.Contains(' ') && flat.Length is >= 8 and <= 128 && !flat.Contains('/') && !flat.Contains('\\') && !flat.Contains('@')
            && flat.Any(char.IsDigit) && flat.Any(char.IsLetter) && (flat.Any(c => !char.IsLetterOrDigit(c)) || (flat.Any(char.IsUpper) && flat.Any(char.IsLower)));
        if (secret) return $"{flat.Length} characters";
        return flat.Length > 90 ? flat[..90] : flat;
    }
}
