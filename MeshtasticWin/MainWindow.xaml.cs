using MeshtasticWin.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace MeshtasticWin;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closed += MainWindow_Closed;
        SetInitialWindowSize(1200, 800);
    }

    public void NavigateTo(string tag)
    {
        // Vel meny-elementet som matchar tag
        foreach (var mi in Nav.MenuItems)
        {
            if (mi is NavigationViewItem nvi && (nvi.Tag?.ToString() == tag))
            {
                Nav.SelectedItem = nvi;
                return;
            }
        }
    }

    private void SetInitialWindowSize(int width, int height)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is not NavigationViewItem item)
            return;

        switch (item.Tag?.ToString())
        {
            case "messages":
                ContentFrame.Navigate(typeof(Pages.MessagesPage));
                break;
            case "connect":
                ContentFrame.Navigate(typeof(Pages.ConnectPage));
                break;
            case "nodes":
                ContentFrame.Navigate(typeof(Pages.NodesPage));
                break;
            case "settings":
                ContentFrame.Navigate(typeof(Pages.SettingsPage));
                break;
        }
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        try { await RadioClient.Instance.DisconnectAsync(); }
        catch { }
    }
}
