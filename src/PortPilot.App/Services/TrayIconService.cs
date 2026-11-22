using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using PortPilot.App.Native;

namespace PortPilot.App.Services;

/// <summary>
/// A native notification-area icon backed by Shell_NotifyIcon. Its menu is a regular, themed WPF
/// <see cref="ContextMenu"/> so it matches the rest of the app.
/// </summary>
public sealed unsafe class TrayIconService : INotificationService, IDisposable
{
    private const int IconId = 1;
    private const int CallbackMessage = Win32.WM_APP + 1;

    private readonly ILogger _log;
    private readonly HwndSource _window;
    private readonly uint _taskbarCreatedMessage;
    private nint _icon;
    private string _tooltip = "PortPilot";
    private bool _added;
    private ContextMenu? _menu;

    public TrayIconService(ILogger<TrayIconService> log)
    {
        _log = log;
        _taskbarCreatedMessage = Win32.RegisterWindowMessage("TaskbarCreated");

        // A message-only style window (never shown) that receives tray callbacks.
        _window = new HwndSource(new HwndSourceParameters("PortPilotTray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        });
        _window.AddHook(WndProc);
    }

    /// <summary>Raised when the icon is clicked or activated from the keyboard.</summary>
    public event EventHandler? Activated;

    /// <summary>Raised before the menu opens so the owner can populate it.</summary>
    public event Action<ContextMenu>? MenuOpening;

    /// <summary>Raised when the user clicks a notification balloon.</summary>
    public event EventHandler? NotificationClicked;

    public void Show()
    {
        if (_added)
        {
            return;
        }

        _icon = LoadIcon();
        var data = CreateData(Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP);
        _added = Win32.Shell_NotifyIcon(Win32.NIM_ADD, &data);
        if (_added)
        {
            data.uVersion = Win32.NOTIFYICON_VERSION_4;
            Win32.Shell_NotifyIcon(Win32.NIM_SETVERSION, &data);
        }
        else
        {
            _log.LogWarning("Could not add the notification area icon");
        }
    }

    public void SetTooltip(string text)
    {
        _tooltip = text;
        if (_added)
        {
            var data = CreateData(Win32.NIF_TIP | Win32.NIF_SHOWTIP);
            Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, &data);
        }
    }

    /// <summary>Shows a native Windows notification from the tray icon.</summary>
    public void Notify(string title, string message)
    {
        if (!_added)
        {
            return;
        }

        var data = CreateData(Win32.NIF_INFO);
        Win32.CopyTo(title, data.szInfoTitle, 64);
        Win32.CopyTo(message, data.szInfo, 256);
        data.dwInfoFlags = Win32.NIIF_USER | Win32.NIIF_LARGE_ICON;
        data.hBalloonIcon = _icon;
        Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, &data);
    }

    private Win32.NOTIFYICONDATAW CreateData(int flags)
    {
        var data = new Win32.NOTIFYICONDATAW
        {
            cbSize = sizeof(Win32.NOTIFYICONDATAW),
            hWnd = _window.Handle,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
        };
        Win32.CopyTo(_tooltip, data.szTip, 128);
        return data;
    }

    private static nint LoadIcon()
    {
        var dpi = Win32.GetDpiForSystem();
        var size = Win32.GetSystemMetricsForDpi(Win32.SM_CXSMICON, dpi);
        var path = Environment.ProcessPath;
        if (path is not null && Win32.SHDefExtractIcon(path, 0, 0, out var large, out var small, (uint)(size | (size << 16))) == 0)
        {
            if (small != 0)
            {
                Win32.DestroyIcon(small);
            }

            return large;
        }

        return 0;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            switch ((int)(lParam & 0xFFFF))
            {
                case Win32.NIN_SELECT:
                case Win32.NIN_KEYSELECT:
                    Activated?.Invoke(this, EventArgs.Empty);
                    handled = true;
                    break;
                case Win32.WM_CONTEXTMENU:
                    ShowMenu();
                    handled = true;
                    break;
                case Win32.NIN_BALLOONUSERCLICK:
                    NotificationClicked?.Invoke(this, EventArgs.Empty);
                    handled = true;
                    break;
            }
        }
        else if (msg == _taskbarCreatedMessage)
        {
            // Explorer restarted; the icon must be added again.
            _added = false;
            Show();
        }

        return 0;
    }

    private void ShowMenu()
    {
        _menu ??= new ContextMenu();
        _menu.Items.Clear();
        MenuOpening?.Invoke(_menu);
        if (_menu.Items.Count == 0)
        {
            return;
        }

        // The menu needs a foreground window to dismiss correctly when the user clicks elsewhere.
        Win32.SetForegroundWindow(_window.Handle);
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = CreateData(0);
            Win32.Shell_NotifyIcon(Win32.NIM_DELETE, &data);
            _added = false;
        }

        if (_icon != 0)
        {
            Win32.DestroyIcon(_icon);
            _icon = 0;
        }

        _window.Dispose();
    }
}
