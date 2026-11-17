using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace FolioTray;

/// <summary>
/// The tray icon. Keeps Folio running (restarting it after a crash, at most <see cref="MaxRestarts"/> times in
/// <see cref="RestartWindow"/>), and offers Open, Restart, Open logs and Quit.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private const int MaxRestarts = 3;
    private static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(15);

    private readonly TraySettings _settings;
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 5000 };
    private readonly EventWaitHandle _quitSignal = new(false, EventResetMode.AutoReset, Program.QuitSignalName);
    private readonly RegisteredWaitHandle _quitWait;
    private readonly Queue<DateTime> _restarts = new();
    private readonly SynchronizationContext _ui;
    private Process? _folio;
    private bool _stopping;
    private bool _gaveUp;

    public TrayContext(TraySettings settings, bool openBrowser)
    {
        _settings = settings;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Open Folio", null, (_, _) => OpenBrowser(_settings.OpenUrl())) { Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold) };
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Restart Folio", null, async (_, _) => await RestartAsync());
        menu.Items.Add("Open logs folder", null, (_, _) => OpenFolder(_settings.LogDirectory));
        menu.Items.Add("Open data folder", null, (_, _) => OpenFolder(_settings.DataDirectory));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Folio", null, async (_, _) => await QuitAsync());

        _icon = new NotifyIcon { Icon = DrawIcon(), Text = "Folio", ContextMenuStrip = menu, Visible = true };
        _icon.DoubleClick += (_, _) => OpenBrowser(_settings.OpenUrl());

        // The installer runs "FolioTray --quit" before replacing files.
        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitSignal, (_, _) => _ui.Post(async _ => await QuitAsync(), null), null, Timeout.Infinite, executeOnlyOnce: false);

        _folio = FindRunning();
        if (_folio is null) Start(openBrowser);
        else if (openBrowser) OpenBrowser(_settings.OpenUrl());
        UpdateStatus();

        _watch.Tick += (_, _) => Watch();
        _watch.Start();
    }

    /// <summary>Folio.exe from this folder, if it's already running (e.g. started before the tray).</summary>
    private Process? FindRunning() =>
        Process.GetProcessesByName("Folio").FirstOrDefault(p =>
        {
            try { return string.Equals(p.MainModule?.FileName, _settings.FolioExe, StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
        });

    private void Start(bool openBrowser)
    {
        var args = new List<string> { "--contentRoot", _settings.AppDirectory };
        if (openBrowser) args.Add("--open");
        var info = new ProcessStartInfo(_settings.FolioExe) { WorkingDirectory = _settings.AppDirectory, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        try { _folio = Process.Start(info); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _icon.ShowBalloonTip(8000, "Folio couldn't start", ex.Message, ToolTipIcon.Error);
        }
    }

    /// <summary>Restarts Folio if it stopped on its own; gives up after repeated crashes.</summary>
    private void Watch()
    {
        if (_stopping || _gaveUp) return;
        if (_folio is { HasExited: false }) return;
        _folio = FindRunning(); // someone may have started it another way
        if (_folio is not null) { UpdateStatus(); return; }

        var now = DateTime.UtcNow;
        while (_restarts.Count > 0 && now - _restarts.Peek() > RestartWindow) _restarts.Dequeue();
        if (_restarts.Count >= MaxRestarts)
        {
            _gaveUp = true;
            _icon.ShowBalloonTip(10000, "Folio keeps stopping", "It stopped 3 times in 5 minutes, so it won't be restarted. Check the logs folder (right-click the icon), then choose Restart.", ToolTipIcon.Warning);
            UpdateStatus();
            return;
        }
        _restarts.Enqueue(now);
        Start(openBrowser: false);
        UpdateStatus();
    }

    /// <summary>Asks Folio to shut down cleanly (its stop signal), and only ends it forcibly if it doesn't.</summary>
    private async Task StopAsync()
    {
        var p = _folio ?? FindRunning();
        _folio = null;
        if (p is null || p.HasExited) return;
        if (EventWaitHandle.TryOpenExisting($@"Local\Folio-Stop-{p.Id}", out var stop))
            using (stop) stop.Set();
        using var timeout = new CancellationTokenSource(StopTimeout);
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { p.Kill(entireProcessTree: true); }
    }

    private async Task RestartAsync()
    {
        _stopping = true;
        try
        {
            await StopAsync();
            _gaveUp = false;
            _restarts.Clear();
            Start(openBrowser: false);
        }
        finally
        {
            _stopping = false;
            UpdateStatus();
        }
    }

    private async Task QuitAsync()
    {
        if (_stopping) return;
        _stopping = true;
        _watch.Stop();
        await StopAsync();
        _icon.Visible = false;
        ExitThread();
    }

    private void UpdateStatus() =>
        _icon.Text = _gaveUp ? "Folio (stopped after repeated crashes)" : _folio is { HasExited: false } ? "Folio: running" : "Folio: starting…";

    internal static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { /* no browser registered */ }
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>The favicon as a tray icon: an ice-white chart line on a black rounded square.</summary>
    private static Icon DrawIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var square = new GraphicsPath();
            const int r = 8;
            square.AddArc(0, 0, r * 2, r * 2, 180, 90);
            square.AddArc(31 - r * 2, 0, r * 2, r * 2, 270, 90);
            square.AddArc(31 - r * 2, 31 - r * 2, r * 2, r * 2, 0, 90);
            square.AddArc(0, 31 - r * 2, r * 2, r * 2, 90, 90);
            square.CloseFigure();
            using var black = new SolidBrush(Color.FromArgb(7, 8, 9));
            g.FillPath(black, square);
            using var ice = new Pen(Color.FromArgb(220, 235, 243), 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(ice, new PointF[] { new(8, 22), new(14, 16), new(18, 20), new(24, 12) });
        }
        var handle = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _quitWait.Unregister(null);
            _quitSignal.Dispose();
            _watch.Dispose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
