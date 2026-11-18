using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FolioTray;

/// <param name="Quiet">Don't ask first (used by scripts/install.ps1, which has already asked).</param>
/// <param name="Startup">Start Folio when you sign in to Windows.</param>
/// <param name="CopyBackupsTo">Second folder for backup copies; null keeps the current setting (or the OneDrive default), "" turns copies off.</param>
/// <param name="Open">Open Folio in the browser when done.</param>
internal sealed record InstallOptions(bool Quiet = false, bool Startup = true, string? CopyBackupsTo = null, bool Open = true);

/// <summary>
/// Installs and uninstalls Folio for the current Windows account, with no terminal, PowerShell or .NET SDK: double-click
/// FolioTray.exe in the downloaded folder. The program goes in %LOCALAPPDATA%\Programs\Folio and your data in
/// %LOCALAPPDATA%\Folio, which installing, updating and uninstalling never touch. scripts/install.ps1 (building from
/// source) hands its build to this too, so there's one way Folio gets installed.
/// </summary>
internal static class Installer
{
    /// <summary>The installed copy serves plain http://localhost (see README "Security"), so no certificate is needed.</summary>
    public const string SiteUrl = "http://localhost:7245";

    private const string Title = "Folio";
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string ProgramDirectory { get; } = Path.Combine(LocalAppData, "Programs", "Folio");
    public static string DataDirectory { get; } = Path.Combine(LocalAppData, "Folio");

    private static string InstalledTray => Path.Combine(ProgramDirectory, "FolioTray.exe");
    private static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Folio.lnk");
    private static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Folio.lnk");
    private static string StartupLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Folio.lnk");

    public static bool IsInstalledCopy(string directory) => SamePath(directory, ProgramDirectory);

    /// <summary>A folder holding a Folio build to install (FolioTray.exe next to Folio.exe), other than the installed copy.</summary>
    public static bool IsInstallSource(string directory) =>
        !IsInstalledCopy(directory) && File.Exists(Path.Combine(directory, "Folio.exe")) && File.Exists(Path.Combine(directory, "FolioTray.exe"));

    /// <summary>Installs (or updates) Folio from <paramref name="source"/>. Returns 0 when installed, 1 when you said no, 2 on an error.</summary>
    public static int Install(string source, InstallOptions options)
    {
        if (!IsInstallSource(source))
        {
            Show($"There's no Folio to install in {source}. Unzip the whole download, then run FolioTray.exe from that folder.", MessageBoxIcon.Error);
            return 2;
        }
        var updating = File.Exists(InstalledTray);
        if (!options.Quiet)
        {
            var question = (updating ? "Update Folio?" : "Install Folio for your Windows account?") + "\n\n" +
                $"Program: {ProgramDirectory}\nYour data: {DataDirectory}" +
                (updating ? " (kept as it is)" : "") + "\n\n" +
                "It adds a Folio shortcut to your desktop and Start menu, runs in the background with an icon by the clock, " +
                "and starts when you sign in. Nothing needs administrator rights.";
            if (MessageBox.Show(question, Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 1;
        }

        try
        {
            var copyBackupsTo = options.CopyBackupsTo ?? ReadSetting("CopyBackupsTo") ?? DefaultCopyBackupsTo();
            StopInstalled();
            ReplaceProgramFiles(source);
            WriteSettings(copyBackupsTo);
            Directory.CreateDirectory(DataDirectory);

            CreateShortcut(DesktopLink, "--open", "Open Folio (starts it if needed)");
            CreateShortcut(StartMenuLink, "--open", "Open Folio (starts it if needed)");
            if (options.Startup) CreateShortcut(StartupLink, "", "Start Folio in the background (tray icon)");
            else DeleteOurShortcut(StartupLink);

            var start = new ProcessStartInfo(InstalledTray) { WorkingDirectory = ProgramDirectory, UseShellExecute = false };
            if (options.Open) start.ArgumentList.Add("--open");
            Process.Start(start);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Show($"Folio couldn't be {(updating ? "updated" : "installed")}:\n\n{ex.Message}\n\nYour data in {DataDirectory} wasn't touched.", MessageBoxIcon.Error);
            return 2;
        }
    }

    /// <summary>
    /// Removes the program and its shortcuts; your data stays. <paramref name="fromTray"/>: called from the running tray's
    /// menu, which has already stopped Folio and exits right after.
    /// </summary>
    public static bool Uninstall(bool quiet, bool fromTray)
    {
        if (!quiet && !ConfirmUninstall()) return false;

        if (!fromTray) StopInstalled(exceptSelf: true);
        foreach (var link in new[] { DesktopLink, StartMenuLink, StartupLink }) DeleteOurShortcut(link);

        // A running program can't delete its own folder, so a hidden command does it a moment after we exit.
        // (cmd.exe takes its own quoting, so the command line is passed as is.)
        if (Directory.Exists(ProgramDirectory))
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{ProgramDirectory}\"")
            {
                CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath(),
            });
        return true;
    }

    public static bool ConfirmUninstall() => MessageBox.Show(
        $"Uninstall Folio?\n\nThe program and its shortcuts are removed. Your portfolios, backups and settings stay in {DataDirectory}; delete that folder yourself if you want them gone.",
        Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    /// <summary>Asks the installed tray to shut Folio down cleanly, then makes sure (matched by path only).</summary>
    private static void StopInstalled(bool exceptSelf = false)
    {
        if (File.Exists(InstalledTray) && !(exceptSelf && SamePath(Environment.ProcessPath, InstalledTray)))
        {
            using var quit = Process.Start(new ProcessStartInfo(InstalledTray) { UseShellExecute = false, ArgumentList = { "--quit" } });
            quit?.WaitForExit(10_000);
        }
        else if (EventWaitHandle.TryOpenExisting(Program.QuitSignalName, out var signal))
        {
            using (signal) signal.Set();
        }

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var running = InstalledProcesses().Where(p => !exceptSelf || p.Id != Environment.ProcessId).ToList();
            if (running.Count == 0) return;
            if (DateTime.UtcNow > deadline)
            {
                foreach (var p in running) { try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
                return;
            }
            Thread.Sleep(300);
        }
    }

    private static IEnumerable<Process> InstalledProcesses() =>
        Process.GetProcessesByName("Folio").Concat(Process.GetProcessesByName("FolioTray")).Where(p =>
        {
            try { return p.MainModule?.FileName is { } f && SamePath(Path.GetDirectoryName(f), ProgramDirectory); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
        });

    /// <summary>The program folder holds only Folio's own files (data lives elsewhere), so it's replaced as a whole.</summary>
    private static void ReplaceProgramFiles(string source)
    {
        if (Directory.Exists(ProgramDirectory)) Retry(() => Directory.Delete(ProgramDirectory, recursive: true));
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(ProgramDirectory, Path.GetRelativePath(source, dir)));
        Directory.CreateDirectory(ProgramDirectory);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(ProgramDirectory, Path.GetRelativePath(source, file)), overwrite: true);
    }

    /// <summary>Settings for the installed copy (Production): plain http://localhost, data outside the program folder, a log file.</summary>
    private static void WriteSettings(string copyBackupsTo)
    {
        var settings = new JsonObject
        {
            ["Logging"] = new JsonObject
            {
                ["LogLevel"] = new JsonObject
                {
                    ["Default"] = "Information",
                    ["Microsoft.AspNetCore"] = "Warning",
                    ["Microsoft.EntityFrameworkCore"] = "Warning",
                    ["System.Net.Http.HttpClient"] = "Warning",
                },
            },
            ["Folio"] = new JsonObject
            {
                ["Url"] = SiteUrl,
                ["DataDirectory"] = @"%LOCALAPPDATA%\Folio",
                ["CopyBackupsTo"] = copyBackupsTo,
            },
        };
        File.WriteAllText(Path.Combine(ProgramDirectory, "appsettings.Production.json"), settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>A setting from the installed copy's appsettings.Production.json, read before it's replaced.</summary>
    private static string? ReadSetting(string name)
    {
        var path = Path.Combine(ProgramDirectory, "appsettings.Production.json");
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?["Folio"]?[name]?.GetValue<string>() : null; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException) { return null; }
    }

    /// <summary>Backups are also copied to OneDrive when it's set up, so they're off this PC.</summary>
    private static string DefaultCopyBackupsTo() =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OneDrive")) ? "" : @"%OneDrive%\Folio backups";

    private static void CreateShortcut(string path, string arguments, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host isn't available to create shortcuts.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = InstalledTray;
            link.Arguments = arguments;
            link.WorkingDirectory = ProgramDirectory;
            link.Description = description;
            link.Save();
            Marshal.FinalReleaseComObject(link);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>Deletes a Folio.lnk only if it points at the installed tray, so a shortcut of yours with that name is left alone.</summary>
    private static void DeleteOurShortcut(string path)
    {
        if (!File.Exists(path)) return;
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            string target = link.TargetPath;
            Marshal.FinalReleaseComObject(link);
            if (SamePath(target, InstalledTray)) File.Delete(path);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 10) { Thread.Sleep(500); }
        }
    }

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static void Show(string text, MessageBoxIcon icon) => MessageBox.Show(text, Title, MessageBoxButtons.OK, icon);
}
