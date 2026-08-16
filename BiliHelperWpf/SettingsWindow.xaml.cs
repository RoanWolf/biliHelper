using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using BiliHelperWpf.Settings;
using BiliHelperWpf.ViewModels;

namespace BiliHelperWpf;

public partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly MainViewModel _vm;
    private readonly UserControl[] _panels;
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int cursor);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    private const int WM_SETCURSOR = 0x0020;
    private const int WM_NCHITTEST = 0x0084;
    private const int IDC_ARROW = 32512;
    private const int HTCLIENT = 1;

    // Resize hit test codes
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, int uMsg, IntPtr wParam, IntPtr lParam);

    public SettingsWindow(MainViewModel vm, int panelIndex = 0)
    {
        InitializeComponent();
        _vm = vm;

        _panels =
        [
            new AccountPanel(vm),
            new AiModelPanel(),
            new FeishuPanel(),
            new AppearancePanel(),
        ];

        NavigateTo(panelIndex);
    }

    protected override void SetWindowChrome()
    {
        base.SetWindowChrome();
        if (WindowChrome.GetWindowChrome(this) is { } chrome)
        {
            chrome.ResizeBorderThickness = new Thickness(0);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (PresentationSource.FromDependencyObject(this) is HwndSource source)
        {
            source.AddHook(WndProc);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST)
        {
            // 让默认处理执行，获取 hit test 结果
            IntPtr result = DefWindowProc(hwnd, msg, wParam, lParam);
            int hitTest = result.ToInt32();

            // 如果是 resize 区域，改为客户区
            if (hitTest >= HTLEFT && hitTest <= HTBOTTOMRIGHT)
            {
                handled = true;
                return (IntPtr)HTCLIENT;
            }

            return result;
        }

        if (msg == WM_SETCURSOR)
        {
            // 从 lParam 低位获取 hit test 代码
            int hitTest = (int)(lParam.ToInt64() & 0xFFFF);

            // 如果是 resize 区域，强制设置箭头光标
            if (hitTest >= HTLEFT && hitTest <= HTBOTTOMRIGHT)
            {
                IntPtr arrowCursor = LoadCursor(IntPtr.Zero, IDC_ARROW);
                SetCursor(arrowCursor);
                handled = true;
                return (IntPtr)1;
            }
        }

        return IntPtr.Zero;
    }

    public void NavigateTo(int panelIndex)
    {
        if (panelIndex < 0 || panelIndex >= _panels.Length) panelIndex = 0;

        ContentHost.Content = _panels[panelIndex];

        switch (panelIndex)
        {
            case 0 when NavAccount.IsChecked != true: NavAccount.IsChecked = true; break;
            case 1 when NavAi.IsChecked != true: NavAi.IsChecked = true; break;
            case 2 when NavFeishu.IsChecked != true: NavFeishu.IsChecked = true; break;
            case 3 when NavAppearance.IsChecked != true: NavAppearance.IsChecked = true; break;
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        if (_panels is null) return;
        if (int.TryParse(tag, out var index) && index >= 0 && index < _panels.Length)
            ContentHost.Content = _panels[index];
    }
}
