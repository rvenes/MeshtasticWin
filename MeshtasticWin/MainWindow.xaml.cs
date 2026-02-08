using MeshtasticWin.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Specialized;
using System.Linq;
using WinRT.Interop;

namespace MeshtasticWin;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closed += MainWindow_Closed;
        SetInitialWindowSize(1200, 800);
        RadioClient.Instance.ConnectionChanged += OnConnectionChanged;
        AppState.ConnectedNodeChanged += OnConnectionChanged;
        AppState.Nodes.CollectionChanged += Nodes_CollectionChanged;
        foreach (var node in AppState.Nodes)
            node.PropertyChanged += Node_PropertyChanged;
        UpdateConnectionStatusText();
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
        RadioClient.Instance.ConnectionChanged -= OnConnectionChanged;
        AppState.ConnectedNodeChanged -= OnConnectionChanged;
        AppState.Nodes.CollectionChanged -= Nodes_CollectionChanged;
        foreach (var node in AppState.Nodes)
            node.PropertyChanged -= Node_PropertyChanged;
        try { await RadioClient.Instance.DisconnectAsync(); }
        catch { }
    }

    private void OnConnectionChanged()
        => _ = DispatcherQueue.TryEnqueue(UpdateConnectionStatusText);

    private void Nodes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems)
            {
                if (item is Models.NodeLive node)
                    node.PropertyChanged -= Node_PropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems)
            {
                if (item is Models.NodeLive node)
                    node.PropertyChanged += Node_PropertyChanged;
            }
        }

        UpdateConnectionStatusText();
    }

    private void Node_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not Models.NodeLive node)
            return;

        if (!string.Equals(node.IdHex, AppState.ConnectedNodeIdHex, StringComparison.OrdinalIgnoreCase))
            return;

        UpdateConnectionStatusText();
    }

    private void UpdateConnectionStatusText()
    {
        var label = "";
        if (RadioClient.Instance.IsConnected && !string.IsNullOrWhiteSpace(AppState.ConnectedNodeIdHex))
        {
            var node = AppState.Nodes.FirstOrDefault(n =>
                string.Equals(n.IdHex, AppState.ConnectedNodeIdHex, StringComparison.OrdinalIgnoreCase));

            if (node is not null)
            {
                var longName = !string.IsNullOrWhiteSpace(node.LongName)
                    ? node.LongName
                    : !string.IsNullOrWhiteSpace(node.Name)
                        ? node.Name
                        : node.IdHex ?? "";
                var shortName = !string.IsNullOrWhiteSpace(node.ShortName)
                    ? node.ShortName
                    : node.ShortId;

                label = longName;
                if (!string.IsNullOrWhiteSpace(shortName))
                    label += $" ({shortName})";
            }
        }

        ConnectionStatusText.Text = string.IsNullOrWhiteSpace(label)
            ? "Connected to:"
            : $"Connected to: {label}";
    }
}
