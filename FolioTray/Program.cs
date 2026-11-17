namespace FolioTray;

internal static class Program
{
    /// <summary>Set by <c>FolioTray --quit</c> (used by the installer) to ask the running tray to stop Folio and exit.</summary>
    internal const string QuitSignalName = @"Local\FolioTray-Quit";

    /// <summary>
    /// <c>FolioTray</c> starts the tray (and Folio). <c>--open</c> also opens the browser; if the tray is already
    /// running it just opens the browser. <c>--quit</c> tells a running tray to stop Folio and exit.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--quit"))
        {
            if (!EventWaitHandle.TryOpenExisting(QuitSignalName, out var quit)) return 0; // not running
            using (quit) quit.Set();
            return 0;
        }

        using var single = new Mutex(true, @"Local\FolioTray", out var first);
        var settings = TraySettings.Load(AppContext.BaseDirectory);
        if (!first)
        {
            if (args.Contains("--open")) TrayContext.OpenBrowser(settings.OpenUrl());
            return 0;
        }

        ApplicationConfiguration.Initialize();
        using var tray = new TrayContext(settings, openBrowser: args.Contains("--open"));
        Application.Run(tray);
        return 0;
    }
}
