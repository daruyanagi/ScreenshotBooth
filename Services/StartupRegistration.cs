namespace ScreenshotBooth.Services;

/// <summary>
/// "Start with Windows": a shortcut in the user's Startup folder (shell:startup), ported from
/// Petapeta. Works the same for the zip, winget and packaged builds; a packaged StartupTask is
/// not used because a dev-mode registration never fires at sign-in. The app starts hidden in
/// the tray anyway, so no extra "silent" argument is needed.
/// </summary>
public static class StartupRegistration
{
    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "ScreenshotBooth.lnk");

    public static bool IsEnabled() => File.Exists(ShortcutPath);

    public static void SetEnabled(bool enable)
    {
        if (enable)
        {
            CreateShortcut();
        }
        else if (File.Exists(ShortcutPath))
        {
            File.Delete(ShortcutPath);
        }
    }

    private static void CreateShortcut()
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell is not available");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(ShortcutPath);

        if (PackageContext.IsPackaged)
        {
            // A packaged exe cannot be started directly; go through its AUMID.
            var aumid = $"{Windows.ApplicationModel.Package.Current.Id.FamilyName}!App";
            link.TargetPath = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows", "explorer.exe");
            link.Arguments = $"shell:AppsFolder\\{aumid}";
        }
        else
        {
            link.TargetPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot determine the executable path");
            link.WorkingDirectory = AppContext.BaseDirectory;
        }

        link.IconLocation = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico") + ",0";
        link.Description = "ScreenshotBooth";
        link.Save();
    }
}
