namespace ScreenshotBooth.Models;

/// <summary>
/// A global hotkey as RegisterHotKey sees it (Win32 MOD_* flags + virtual-key code), with the
/// display/catalog helpers the settings UI needs. MOD_NOREPEAT is always added on registration.
/// </summary>
public readonly record struct HotkeyBinding(uint Modifiers, uint VirtualKey)
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000;

    public bool Win => (Modifiers & ModWin) != 0;
    public bool Control => (Modifiers & ModControl) != 0;
    public bool Shift => (Modifiers & ModShift) != 0;
    public bool Alt => (Modifiers & ModAlt) != 0;

    /// <summary>Shift alone would fire on ordinary typing; require Win, Ctrl or Alt.</summary>
    public bool HasStrongModifier => Win || Control || Alt;

    /// <summary>The flags to hand to RegisterHotKey.</summary>
    public uint RegistrationModifiers => (Modifiers & (ModAlt | ModControl | ModShift | ModWin)) | ModNoRepeat;

    public static HotkeyBinding Default => new(HotkeyDefaults.Modifiers, HotkeyDefaults.VirtualKey);

    /// <summary>"Win + Shift + B" style text for labels and notices.</summary>
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Win) parts.Add("Win");
        if (Control) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        parts.Add(KeyName(VirtualKey));
        return string.Join(" + ", parts);
    }

    /// <summary>The keys offered by the settings UI: letters, digits, function keys and a few specials.</summary>
    public static IReadOnlyList<(uint VirtualKey, string Name)> KeyCatalog { get; } = BuildCatalog();

    public static string KeyName(uint virtualKey)
    {
        foreach (var (vk, name) in KeyCatalog)
        {
            if (vk == virtualKey)
            {
                return name;
            }
        }

        return $"0x{virtualKey:X2}";
    }

    private static List<(uint, string)> BuildCatalog()
    {
        var list = new List<(uint, string)>();
        for (var c = 'A'; c <= 'Z'; c++) list.Add(((uint)c, c.ToString()));
        for (var c = '0'; c <= '9'; c++) list.Add(((uint)c, c.ToString()));
        for (var i = 1; i <= 12; i++) list.Add((0x70u + (uint)(i - 1), $"F{i}"));
        list.Add((0x2C, "Print Screen"));
        list.Add((0x20, "Space"));
        list.Add((0x2D, "Insert"));
        list.Add((0x2E, "Delete"));
        list.Add((0x24, "Home"));
        list.Add((0x23, "End"));
        list.Add((0x21, "Page Up"));
        list.Add((0x22, "Page Down"));
        return list;
    }
}
