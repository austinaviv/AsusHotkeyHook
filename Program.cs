using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace AsusHotkeyDetector;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const int ASUS_MIC_MUTE_EVENT_ID = 124;

    private static readonly object StateLock = new();

    private static bool _isMuted;

    private static NotifyIcon? _trayIcon;
    private static ToolStripMenuItem? _startupMenuItem;
    private static ManagementEventWatcher? _watcher;

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        _isMuted = GetPrimaryHardwareMicMuteState();

        ContextMenuStrip trayMenu = new();

        _startupMenuItem = new ToolStripMenuItem(
            "Run at Windows Startup",
            null,
            ToggleStartupHandler)
        {
            Checked = IsRegisteredInStartup()
        };

        trayMenu.Items.Add(_startupMenuItem);
        trayMenu.Items.Add(new ToolStripSeparator());

        trayMenu.Items.Add(
            "Exit Monitor",
            null,
            (_, _) => Shutdown());

        _trayIcon = new NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = trayMenu
        };

        UpdateTrayIcon(_isMuted);

        try
        {
            ManagementScope scope = new(@"root\wmi");

            WqlEventQuery query =
                new("SELECT * FROM AsusAtkWmiEvent");

            scope.Connect();

            _watcher = new ManagementEventWatcher(scope, query);
            _watcher.EventArrived += OnAsusKeyStroke;
            _watcher.Start();

            Application.ApplicationExit += (_, _) => Shutdown();

            Application.Run();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);

            _trayIcon?.Dispose();
        }
    }

    private static void Shutdown()
    {
        try
        {
            _watcher?.Stop();
            _watcher?.Dispose();
        }
        catch { }

        try
        {
            _trayIcon?.Dispose();
        }
        catch { }

        Application.Exit();
    }

    private static void OnAsusKeyStroke(
        object sender,
        EventArrivedEventArgs e)
    {
        try
        {
            object? eventData =
                e.NewEvent.Properties["EventID"]?.Value;

            if (eventData == null)
                return;

            if (Convert.ToInt32(eventData) != ASUS_MIC_MUTE_EVENT_ID)
                return;

            lock (StateLock)
            {
                bool currentState =
                    GetPrimaryHardwareMicMuteState();

                bool newState = !currentState;

                SetGlobalHardwareMicMuteState(newState);

                _isMuted = newState;

                UpdateTrayIcon(newState);

                Thread uiThread = new(() =>
                {
                    using CuteOverlayForm form =
                        new(newState);

                    Application.Run(form);
                });

                uiThread.SetApartmentState(
                    ApartmentState.STA);

                uiThread.IsBackground = true;
                uiThread.Start();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private static bool GetPrimaryHardwareMicMuteState()
    {
        try
        {
            using MMDeviceEnumerator enumerator = new();

            using MMDevice device =
                enumerator.GetDefaultAudioEndpoint(
                    DataFlow.Capture,
                    Role.Communications);

            return device.AudioEndpointVolume.Mute;
        }
        catch
        {
            return false;
        }
    }

    private static void SetGlobalHardwareMicMuteState(
        bool mute)
    {
        try
        {
            using MMDeviceEnumerator enumerator = new();

            var devices =
                enumerator.EnumerateAudioEndPoints(
                    DataFlow.Capture,
                    DeviceState.Active);

            foreach (var device in devices)
            {
                try
                {
                    device.AudioEndpointVolume.Mute = mute;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private static void UpdateTrayIcon(
        bool mutedState)
    {
        if (_trayIcon == null)
            return;

        using Bitmap bitmap = new(16, 16);

        using Graphics g =
            Graphics.FromImage(bitmap);

        g.Clear(Color.Transparent);
        g.SmoothingMode =
            SmoothingMode.AntiAlias;

        Brush brush =
            mutedState
                ? Brushes.Crimson
                : Brushes.LimeGreen;

        g.FillEllipse(
            brush,
            1,
            1,
            14,
            14);

        g.DrawEllipse(
            Pens.Black,
            1,
            1,
            14,
            14);

        IntPtr hIcon = bitmap.GetHicon();

        try
        {
            using Icon tempIcon =
                Icon.FromHandle(hIcon);

            _trayIcon.Icon?.Dispose();
            _trayIcon.Icon =
                (Icon)tempIcon.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }

        _trayIcon.Text =
            mutedState
                ? "Mic Status: Muted"
                : "Mic Status: Live";
    }

    private static bool IsRegisteredInStartup()
    {
        try
        {
            using RegistryKey? key =
                Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                    false);

            return key?.GetValue(
                "AsusMicMuteOverlay") != null;
        }
        catch
        {
            return false;
        }
    }

    private static void ToggleStartupHandler(
        object? sender,
        EventArgs e)
    {
        try
        {
            const string runKey =
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

            using RegistryKey? key =
                Registry.CurrentUser.OpenSubKey(
                    runKey,
                    true);

            if (key == null)
                return;

            string appPath =
                Environment.ProcessPath
                ?? Application.ExecutablePath;

            if (!_startupMenuItem!.Checked)
            {
                key.SetValue(
                    "AsusMicMuteOverlay",
                    $"\"{appPath}\"");

                _startupMenuItem.Checked = true;
            }
            else
            {
                key.DeleteValue(
                    "AsusMicMuteOverlay",
                    false);

                _startupMenuItem.Checked = false;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not update startup settings:\n\n{ex.Message}",
                "Permissions Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }
}

public class CuteOverlayForm : Form
{
    private readonly bool _muted;
    private readonly System.Windows.Forms.Timer _dismissTimer;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;

            cp.ExStyle |= 0x00080000;
            cp.ExStyle |= 0x00000020;
            cp.ExStyle |= 0x00000008;

            return cp;
        }
    }

    public CuteOverlayForm(bool muted)
    {
        _muted = muted;

        DoubleBuffered = true;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;

        Size = new Size(260, 75);

        TopMost = true;

        BackColor = Color.FromArgb(
            32,
            32,
            32);

        Screen? mainScreen =
            Screen.PrimaryScreen;

        if (mainScreen == null &&
            Screen.AllScreens.Length > 0)
        {
            mainScreen = Screen.AllScreens[0];
        }

        if (mainScreen != null)
        {
            Left =
                mainScreen.WorkingArea.Left +
                (mainScreen.WorkingArea.Width - Width) / 2;

            Top =
                mainScreen.WorkingArea.Bottom -
                Height -
                140;
        }

        _dismissTimer = new System.Windows.Forms.Timer
        {
            Interval = 1500
        };

        _dismissTimer.Tick += (_, _) =>
        {
            _dismissTimer.Stop();
            Application.ExitThread();
        };

        _dismissTimer.Start();
    }

    protected override void OnPaint(
        PaintEventArgs e)
    {
        base.OnPaint(e);

        Graphics g = e.Graphics;

        g.SmoothingMode =
            SmoothingMode.AntiAlias;

        using GraphicsPath path =
            NewRoundedRectanglePath(
                0,
                0,
                Width - 1,
                Height - 1,
                24);

        Region = new Region(path);

        using SolidBrush backgroundBrush =
            new(Color.FromArgb(32, 32, 32));

        g.FillPath(
            backgroundBrush,
            path);

        Color themeColor =
            _muted
                ? Color.FromArgb(
                    255,
                    95,
                    95)
                : Color.FromArgb(
                    95,
                    235,
                    180);

        using Pen borderPen =
            new(themeColor, 3f);

        g.DrawPath(
            borderPen,
            path);

        string statusEmoji =
            _muted
                ? " Mic Off"
                : " Mic On";

        string infoText =
            _muted
                ? "Shhh... you are muted!"
                : "Go ahead, speak up!";

        using Font emojiFont =
            new(
                "Segoe UI",
                14,
                FontStyle.Bold);

        using Font textFont =
            new(
                "Segoe UI",
                10,
                FontStyle.Regular);

        g.DrawString(
            statusEmoji,
            emojiFont,
            Brushes.White,
            new PointF(20, 14));

        g.DrawString(
            infoText,
            textFont,
            Brushes.DarkGray,
            new PointF(22, 42));
    }

    private static GraphicsPath NewRoundedRectanglePath(
        int x,
        int y,
        int width,
        int height,
        int radius)
    {
        GraphicsPath path = new();

        int diameter = radius * 2;

        path.AddArc(
            x,
            y,
            diameter,
            diameter,
            180,
            90);

        path.AddArc(
            x + width - diameter,
            y,
            diameter,
            diameter,
            270,
            90);

        path.AddArc(
            x + width - diameter,
            y + height - diameter,
            diameter,
            diameter,
            0,
            90);

        path.AddArc(
            x,
            y + height - diameter,
            diameter,
            diameter,
            90,
            90);

        path.CloseFigure();

        return path;
    }
}