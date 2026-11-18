namespace FolioTray;

internal static class Program
{
    /// <summary>Set by <c>FolioTray --quit</c> (used by the installer) to ask the running tray to stop Folio and exit.</summary>
    internal const string QuitSignalName = @"Local\FolioTray-Quit";

    /// <summary>
    /// <c>FolioTray</c> starts the tray (and Folio). <c>--open</c> also opens the browser; if the tray is already
    /// running it just opens the browser. <c>--quit</c> tells a running tray to stop Folio and exit.
    /// <para>
    /// Installing: run from a downloaded Folio folder (not the installed copy), it offers to install Folio, which is
    /// how people install it without a terminal. <c>--install</c> does the same with options (<c>--yes</c>,
    /// <c>--no-startup</c>, <c>--no-open</c>, <c>--copy-backups-to &lt;folder&gt;</c>); <c>--uninstall [--yes]</c> removes it.
    /// </para>
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

        ApplicationConfiguration.Initialize();
        var here = AppContext.BaseDirectory;

        if (args.Contains("--install")) return Installer.Install(here, InstallOptionsFrom(args));
        if (args.Contains("--uninstall")) return Installer.Uninstall(quiet: args.Contains("--yes"), fromTray: false) ? 0 : 1;
        if (args.Length == 0 && !Installer.IsInstalledCopy(here))
        {
            if (Installer.IsInstallSource(here)) return Installer.Install(here, new InstallOptions());
            if (!File.Exists(Path.Combine(here, "Folio.exe")))
            {
                // Typically FolioTray.exe opened straight from inside the zip, without the rest of Folio.
                MessageBox.Show("Unzip the whole Folio download first, then run FolioTray.exe from the unzipped folder.", "Folio", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 2;
            }
        }

        using var single = new Mutex(true, @"Local\FolioTray", out var first);
        var settings = TraySettings.Load(here);
        if (!first)
        {
            if (args.Contains("--open")) TrayContext.OpenBrowser(settings.OpenUrl());
            return 0;
        }

        using var tray = new TrayContext(settings, openBrowser: args.Contains("--open"));
        Application.Run(tray);
        return 0;
    }

    private static InstallOptions InstallOptionsFrom(string[] args)
    {
        var i = Array.IndexOf(args, "--copy-backups-to");
        return new InstallOptions(
            Quiet: args.Contains("--yes"),
            Startup: !args.Contains("--no-startup"),
            CopyBackupsTo: i >= 0 && i + 1 < args.Length ? args[i + 1] : null,
            Open: !args.Contains("--no-open"));
    }
}
