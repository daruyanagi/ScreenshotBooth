using Microsoft.UI.Windowing;
using ScreenshotBooth.Models;

namespace ScreenshotBooth.Services;

/// <summary>
/// Resolves which display to use for the booth window and target window, via the modern
/// Microsoft.UI.Windowing.DisplayArea API. Deliberately does NOT use the legacy WinForms
/// System.Windows.Forms.Screen class, which is not DPI/per-monitor aware in the way WinUI 3 needs
/// and pulls in an unwanted WinForms dependency.
/// </summary>
public static class DisplayService
{
    /// <summary>
    /// Returns the display to use, honoring <see cref="AppSettings.SelectedDisplayIndex"/> when it
    /// points at a still-connected display; falls back to the primary display otherwise. Display
    /// selection is intentionally pluggable via this single seam for future multi-monitor UI.
    /// </summary>
    public static DisplayArea GetSelectedDisplay(AppSettings settings)
    {
        var all = DisplayArea.FindAll();

        if (settings.SelectedDisplayIndex >= 0 && settings.SelectedDisplayIndex < all.Count)
        {
            return all[settings.SelectedDisplayIndex];
        }

        return DisplayArea.Primary;
    }
}
