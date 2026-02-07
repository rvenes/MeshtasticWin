using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using MeshtasticWin.Models;
using MeshtasticWin.Services;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace MeshtasticWin.Pages;

public sealed partial class MessagesPage : Page, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<MessageVm> ViewMessages { get; } = new();
    public ObservableCollection<ChatListItemVm> ChatListItems { get; } = new();

    private static readonly Regex UrlRegex = new(@"https?://\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string ActiveChatTitle
    {
        get
        {
            var peer = MeshtasticWin.AppState.ActiveChatPeerIdHex;
            if (string.IsNullOrWhiteSpace(peer))
                return "Primary channel";

            var node = MeshtasticWin.AppState.Nodes.FirstOrDefault(n =>
                string.Equals(n.IdHex, peer, StringComparison.OrdinalIgnoreCase));

            if (node is null)
                return $"DM: {peer}";

            return string.IsNullOrWhiteSpace(node.ShortId)
                ? $"DM: {node.Name}"
                : $"DM: {node.Name} ({node.ShortId})";
        }
    }

    private bool _suppressListEvent;
    private string _chatFilter = "";

    private int _hideOlderThanDays = 90; // default: 3 months
    private bool _hideInactive = true;

    public MessagesPage()
    {
        InitializeComponent();

        AgeFilterCombo.Items.Add("Show all");
        AgeFilterCombo.Items.Add("Hide > 3 months");
        AgeFilterCombo.SelectedIndex = 1;

        HideInactiveToggle.IsChecked = _hideInactive;

        MeshtasticWin.AppState.Messages.CollectionChanged += Messages_CollectionChanged;
        MeshtasticWin.AppState.Nodes.CollectionChanged += Nodes_CollectionChanged;
        MeshtasticWin.AppState.ActiveChatChanged += ActiveChatChanged;

        RebuildChatList();
        SyncListToActiveChat();
        RebuildView();
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(() =>
        {
            RebuildView();
            RebuildChatList();
            SyncListToActiveChat();
        });

    private void Nodes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(() =>
        {
            RebuildChatList();
            SyncListToActiveChat();
            OnChanged(nameof(ActiveChatTitle));
        });

    private void ActiveChatChanged()
        => DispatcherQueue.TryEnqueue(() =>
        {
            SyncListToActiveChat();
            MeshtasticWin.AppState.MarkChatRead(MeshtasticWin.AppState.ActiveChatPeerIdHex);
            RebuildView();
            RebuildChatList();
            OnChanged(nameof(ActiveChatTitle));
        });

    private void RebuildChatList()
    {
        ChatListItems.Clear();

        var q = (_chatFilter ?? "").Trim();

        // Primary (broadcast)
        ChatListItems.Add(ChatListItemVm.Primary());

        // Nodes (same sort som NodesPage: online først, så lastHeard)
        var nodes = MeshtasticWin.AppState.Nodes
            .Where(n => !string.IsNullOrWhiteSpace(n.IdHex))
            .Where(n => !IsTooOld(n))
            .Where(n => !IsHiddenByInactive(n))
            .Where(n =>
            {
                if (string.IsNullOrWhiteSpace(q)) return true;
                return (n.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (n.IdHex?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (n.ShortId?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
            })
            .OrderByDescending(IsOnlineByRssi)
            .ThenByDescending(n => n.LastHeardUtc)
            .ThenBy(n => n.Name)
            .ToList();

        foreach (var n in nodes)
            ChatListItems.Add(ChatListItemVm.ForNode(n));

        OnChanged(nameof(ActiveChatTitle));
    }

    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void SyncListToActiveChat()
    {
        _suppressListEvent = true;

        var peer = MeshtasticWin.AppState.ActiveChatPeerIdHex;

        ChatListItemVm? match;
        if (string.IsNullOrWhiteSpace(peer))
            match = ChatListItems.FirstOrDefault(x => x.PeerIdHex is null);
        else
            match = ChatListItems.FirstOrDefault(x => string.Equals(x.PeerIdHex, peer, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
            ChatList.SelectedItem = match;

        _suppressListEvent = false;
    }

    private void ChatList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressListEvent)
            return;

        if (ChatList.SelectedItem is not ChatListItemVm target)
            return;

        MeshtasticWin.AppState.SetActiveChatPeer(target.PeerIdHex);
        MeshtasticWin.AppState.MarkChatRead(target.PeerIdHex);
        RebuildChatList();
        SyncListToActiveChat();
    }

    private void ChatSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _chatFilter = ChatSearchBox.Text ?? "";
        RebuildChatList();
        SyncListToActiveChat();
    }

    private void AgeFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _hideOlderThanDays = AgeFilterCombo.SelectedIndex switch
        {
            0 => 99999,
            1 => 90,
            _ => 90
        };

        RebuildChatList();
        SyncListToActiveChat();
    }

    private void HideInactiveToggle_Click(object sender, RoutedEventArgs e)
    {
        _hideInactive = HideInactiveToggle.IsChecked == true;
        RebuildChatList();
        SyncListToActiveChat();
    }

    private bool IsTooOld(NodeLive n)
    {
        if (_hideOlderThanDays >= 99999) return false;
        var age = DateTime.UtcNow - n.LastHeardUtc;
        return age.TotalDays > _hideOlderThanDays;
    }

    private bool IsHiddenByInactive(NodeLive n)
    {
        if (!_hideInactive) return false;
        return !IsOnlineByRssi(n);
    }

    private static bool IsOnlineByRssi(NodeLive n)
    {
        // Online = has measured RSSI (not "—" and not 0)
        if (string.IsNullOrWhiteSpace(n.RSSI) || n.RSSI == "—") return false;
        if (int.TryParse(n.RSSI, out var rssi))
            return rssi != 0;
        return false;
    }

    private void RebuildView()
    {
        ViewMessages.Clear();

        var peer = MeshtasticWin.AppState.ActiveChatPeerIdHex;

        foreach (var m in MeshtasticWin.AppState.Messages)
        {
            if (string.IsNullOrWhiteSpace(peer))
            {
                // Primary view: berre broadcast
                if (!m.IsDirect)
                    ViewMessages.Add(MessageVm.From(m));
            }
            else
            {
                // DM view: meldingar som er mellom oss og peeren (inn eller ut)
                if (m.IsDirect &&
                    (string.Equals(m.FromIdHex, peer, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(m.ToIdHex, peer, StringComparison.OrdinalIgnoreCase)))
                {
                    ViewMessages.Add(MessageVm.From(m));
                }
            }
        }
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
        => await SendNowAsync();

    private async void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await SendNowAsync();
        }
    }

    private static bool TryParseNodeNumFromHex(string idHex, out uint nodeNum)
    {
        nodeNum = 0;

        if (string.IsNullOrWhiteSpace(idHex))
            return false;

        var s = idHex.Trim();

        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s.Substring(2);

        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out nodeNum);
    }

    private async System.Threading.Tasks.Task SendNowAsync()
    {
        var text = InputBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return;

        InputBox.Text = "";

        var peer = MeshtasticWin.AppState.ActiveChatPeerIdHex;

        // Primary defaults
        uint? toNodeNum = null;
        uint dmTargetNodeNum = 0;

        string toIdHex = "0xffffffff";
        string toName = "Primary";

        // DM
        if (!string.IsNullOrWhiteSpace(peer))
        {
            toIdHex = peer;

            if (TryParseNodeNumFromHex(peer, out var u))
            {
                toNodeNum = u;
                dmTargetNodeNum = u;
            }

            var node = MeshtasticWin.AppState.Nodes.FirstOrDefault(n =>
                string.Equals(n.IdHex, peer, StringComparison.OrdinalIgnoreCase));

            toName = node?.Name ?? peer;
        }

        // Send -> få packetId for ACK-match
        var packetId = await RadioClient.Instance.SendTextAsync(text, toNodeNum);

        // Lokal melding (så kan FromRadioRouter merke ✓ / ✓✓ seinare)
        var local = MessageLive.CreateOutgoing(
            toIdHex: toIdHex,
            toName: toName,
            text: text,
            packetId: packetId,
            dmTargetNodeNum: dmTargetNodeNum);

        MeshtasticWin.AppState.Messages.Insert(0, local);

        // Arkiv
        if (string.IsNullOrWhiteSpace(peer))
            MessageArchive.Append(local, channelName: "Primary");
        else
            MessageArchive.Append(local, dmPeerIdHex: peer);
    }

    private void MessageText_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is RichTextBlock textBlock)
            UpdateMessageText(textBlock);
    }

    private void MessageText_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is RichTextBlock textBlock)
            UpdateMessageText(textBlock);
    }

    private void UpdateMessageText(RichTextBlock textBlock)
    {
        var text = GetMessageText(textBlock);
        textBlock.Blocks.Clear();

        var paragraph = new Paragraph();
        if (string.IsNullOrEmpty(text))
        {
            textBlock.Blocks.Add(paragraph);
            return;
        }

        var lastIndex = 0;
        foreach (Match match in UrlRegex.Matches(text))
        {
            if (match.Index > lastIndex)
            {
                paragraph.Inlines.Add(new Run
                {
                    Text = text.Substring(lastIndex, match.Index - lastIndex)
                });
            }

            var url = match.Value;
            var link = new Hyperlink { Tag = url };
            link.Inlines.Add(new Run { Text = url });
            link.Click += MessageHyperlink_Click;
            paragraph.Inlines.Add(link);
            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
        {
            paragraph.Inlines.Add(new Run
            {
                Text = text.Substring(lastIndex)
            });
        }

        textBlock.Blocks.Add(paragraph);
    }

    private static string GetMessageText(RichTextBlock textBlock)
    {
        if (textBlock.Tag is string tagText)
            return tagText;

        if (textBlock.DataContext is MessageVm message)
            return message.Text ?? "";

        return "";
    }

    private async void MessageHyperlink_Click(Hyperlink sender, HyperlinkClickEventArgs args)
    {
        if (sender.Tag is not string url || string.IsNullOrWhiteSpace(url))
            return;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            await Launcher.LaunchUriAsync(uri);
    }

    private void CopyMessageText_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item)
            return;

        var flyout = item.Parent as MenuFlyout;
        if (flyout?.Target is not RichTextBlock textBlock)
            return;

        var selected = textBlock.Selection?.Text ?? "";
        var fullText = GetMessageText(textBlock);
        var textToCopy = string.IsNullOrWhiteSpace(selected) ? fullText : selected;

        if (string.IsNullOrWhiteSpace(textToCopy))
            return;

        var package = new DataPackage();
        package.SetText(textToCopy);
        Clipboard.SetContent(package);
    }

}

public sealed class ChatListItemVm
{
    public string Title { get; set; } = "";
    public string ShortId { get; set; } = "";
    public string LastHeard { get; set; } = "";
    public string SNR { get; set; } = "";
    public string RSSI { get; set; } = "";

    public Visibility UnreadVisible { get; set; } = Visibility.Collapsed;

    public string? PeerIdHex { get; set; } // null = Primary

    public static ChatListItemVm Primary()
        => new()
        {
            Title = "Primary channel",
            ShortId = "",
            LastHeard = "Broadcast",
            SNR = "—",
            RSSI = "—",
            UnreadVisible = MeshtasticWin.AppState.HasUnread(null) ? Visibility.Visible : Visibility.Collapsed,
            PeerIdHex = null
        };

    public static ChatListItemVm ForNode(NodeLive n)
        => new()
        {
            Title = string.IsNullOrWhiteSpace(n.ShortId)
                ? n.Name
                : n.Name,
            ShortId = n.ShortId ?? "",
            LastHeard = n.LastHeard ?? "—",
            SNR = n.SNR ?? "—",
            RSSI = n.RSSI ?? "—",
            UnreadVisible = MeshtasticWin.AppState.HasUnread(n.IdHex) ? Visibility.Visible : Visibility.Collapsed,
            PeerIdHex = n.IdHex
        };
}

public sealed class MessageVm
{
    public string Header { get; set; } = "";
    public string Text { get; set; } = "";
    public string When { get; set; } = "";

    public Visibility HeardVisible { get; set; } = Visibility.Collapsed;
    public Visibility DeliveredVisible { get; set; } = Visibility.Collapsed;

    public static MessageVm From(MessageLive m)
        => new()
        {
            Header = m.Header,
            Text = m.Text,
            When = m.When,
            HeardVisible = (m.IsMine && m.IsHeard) ? Visibility.Visible : Visibility.Collapsed,
            DeliveredVisible = (m.IsMine && m.IsDelivered) ? Visibility.Visible : Visibility.Collapsed
        };
}
