using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using MeshtasticWin.Models;
using MeshtasticWin.Services;
using System;
using System.Collections.Generic;
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

    private readonly ChatListItemVm _primaryChatItem = ChatListItemVm.Primary();
    private readonly Dictionary<string, ChatListItemVm> _chatItemsByPeer = new(StringComparer.OrdinalIgnoreCase);

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
        MeshtasticWin.AppState.UnreadChanged += UnreadChanged;

        _chatItemsByPeer[""] = _primaryChatItem;
        ChatListItems.Add(_primaryChatItem);

        foreach (var node in MeshtasticWin.AppState.Nodes)
        {
            node.PropertyChanged += Node_PropertyChanged;
            AddChatItemForNode(node);
        }

        foreach (var message in MeshtasticWin.AppState.Messages)
            ViewMessages.Add(CreateMessageVm(message));

        ApplyChatFiltersToAllItems();
        ApplyMessageVisibilityToAll();
        SyncListToActiveChat();
        OnChanged(nameof(ActiveChatTitle));
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(() =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                SyncMessagesWithAppState();
                return;
            }

            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
            {
                var insertAt = e.NewStartingIndex < 0 ? ViewMessages.Count : e.NewStartingIndex;
                foreach (MessageLive message in e.NewItems)
                {
                    ViewMessages.Insert(insertAt++, CreateMessageVm(message));
                }
                return;
            }

            if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems is not null)
            {
                if (e.OldStartingIndex >= 0)
                {
                    for (var i = 0; i < e.OldItems.Count; i++)
                        ViewMessages.RemoveAt(e.OldStartingIndex);
                }
                else
                {
                    foreach (MessageLive message in e.OldItems)
                        RemoveMessageVm(message);
                }
                return;
            }

            if (e.Action == NotifyCollectionChangedAction.Replace && e.NewItems is not null)
            {
                var startIndex = e.NewStartingIndex;
                for (var i = 0; i < e.NewItems.Count; i++)
                {
                    if (startIndex + i >= ViewMessages.Count)
                        break;

                    if (e.NewItems[i] is MessageLive message)
                        UpdateMessageVm(ViewMessages[startIndex + i], message);
                }
            }
        });

    private void Nodes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(() =>
        {
            if (e.NewItems is not null)
                foreach (NodeLive node in e.NewItems)
                {
                    node.PropertyChanged += Node_PropertyChanged;
                    AddChatItemForNode(node);
                }

            if (e.OldItems is not null)
                foreach (NodeLive node in e.OldItems)
                {
                    node.PropertyChanged -= Node_PropertyChanged;
                    RemoveChatItemForNode(node);
                }

            ApplyChatFiltersToAllItems();
            SyncListToActiveChat();
            OnChanged(nameof(ActiveChatTitle));
        });

    private void ActiveChatChanged()
        => DispatcherQueue.TryEnqueue(() =>
        {
            SyncListToActiveChat();
            MeshtasticWin.AppState.MarkChatRead(MeshtasticWin.AppState.ActiveChatPeerIdHex);
            ApplyMessageVisibilityToAll();
            OnChanged(nameof(ActiveChatTitle));
        });

    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Node_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not NodeLive node)
            return;

        if (e.PropertyName is nameof(NodeLive.LastHeard) or nameof(NodeLive.LastHeardUtc)
            or nameof(NodeLive.SNR) or nameof(NodeLive.RSSI)
            or nameof(NodeLive.Name) or nameof(NodeLive.ShortName))
        {
            UpdateChatItemFromNode(node);
            UpdateChatItemVisibility(node);
            if (string.Equals(MeshtasticWin.AppState.ActiveChatPeerIdHex, node.IdHex, StringComparison.OrdinalIgnoreCase))
                OnChanged(nameof(ActiveChatTitle));
        }
    }

    private void AddChatItemForNode(NodeLive node)
    {
        if (string.IsNullOrWhiteSpace(node.IdHex))
            return;

        if (_chatItemsByPeer.ContainsKey(node.IdHex))
            return;

        var item = ChatListItemVm.ForNode(node);
        _chatItemsByPeer[node.IdHex] = item;
        ChatListItems.Add(item);
        UpdateChatItemVisibility(node);
    }

    private void RemoveChatItemForNode(NodeLive node)
    {
        if (string.IsNullOrWhiteSpace(node.IdHex))
            return;

        if (_chatItemsByPeer.Remove(node.IdHex, out var item))
            ChatListItems.Remove(item);
    }

    private void UpdateChatItemFromNode(NodeLive node)
    {
        if (string.IsNullOrWhiteSpace(node.IdHex))
            return;

        if (_chatItemsByPeer.TryGetValue(node.IdHex, out var item))
            item.UpdateFromNode(node);
    }

    private void UpdateChatItemVisibility(NodeLive node)
    {
        if (string.IsNullOrWhiteSpace(node.IdHex))
            return;

        if (_chatItemsByPeer.TryGetValue(node.IdHex, out var item))
            item.IsVisible = ShouldShowChatItem(node);
    }

    private void ApplyChatFiltersToAllItems()
    {
        _primaryChatItem.IsVisible = true;

        foreach (var node in MeshtasticWin.AppState.Nodes)
            UpdateChatItemVisibility(node);
    }

    private bool ShouldShowChatItem(NodeLive node)
    {
        if (IsTooOld(node) || IsHiddenByInactive(node))
            return false;

        var q = (_chatFilter ?? "").Trim();
        if (string.IsNullOrWhiteSpace(q))
            return true;

        return (node.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (node.IdHex?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (node.ShortId?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void UnreadChanged(string? peerIdHex)
        => DispatcherQueue.TryEnqueue(() => UpdateUnreadIndicators(peerIdHex));

    private void UpdateUnreadIndicators(string? peerIdHex)
    {
        if (string.IsNullOrWhiteSpace(peerIdHex))
        {
            _primaryChatItem.UnreadVisible = MeshtasticWin.AppState.HasUnread(null)
                ? Visibility.Visible
                : Visibility.Collapsed;
            return;
        }

        if (_chatItemsByPeer.TryGetValue(peerIdHex, out var item))
            item.UnreadVisible = MeshtasticWin.AppState.HasUnread(peerIdHex)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

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
        SyncListToActiveChat();
    }

    private void ChatSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _chatFilter = ChatSearchBox.Text ?? "";
        ApplyChatFiltersToAllItems();
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

        ApplyChatFiltersToAllItems();
        SyncListToActiveChat();
    }

    private void HideInactiveToggle_Click(object sender, RoutedEventArgs e)
    {
        _hideInactive = HideInactiveToggle.IsChecked == true;
        ApplyChatFiltersToAllItems();
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

    private void ApplyMessageVisibilityToAll()
    {
        var peer = MeshtasticWin.AppState.ActiveChatPeerIdHex;
        for (var i = 0; i < ViewMessages.Count && i < MeshtasticWin.AppState.Messages.Count; i++)
        {
            var live = MeshtasticWin.AppState.Messages[i];
            var vm = ViewMessages[i];
            vm.IsVisible = ShouldShowMessage(live, peer);
        }
    }

    private MessageVm CreateMessageVm(MessageLive message)
    {
        var vm = MessageVm.From(message);
        vm.IsVisible = ShouldShowMessage(message, MeshtasticWin.AppState.ActiveChatPeerIdHex);
        return vm;
    }

    private static bool ShouldShowMessage(MessageLive message, string? peer)
    {
        if (string.IsNullOrWhiteSpace(peer))
            return !message.IsDirect;

        return message.IsDirect &&
               (string.Equals(message.FromIdHex, peer, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(message.ToIdHex, peer, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateMessageVm(MessageVm vm, MessageLive message)
    {
        vm.UpdateFrom(message);
        vm.IsVisible = ShouldShowMessage(message, MeshtasticWin.AppState.ActiveChatPeerIdHex);
    }

    private void RemoveMessageVm(MessageLive message)
    {
        var index = MeshtasticWin.AppState.Messages.IndexOf(message);
        if (index >= 0 && index < ViewMessages.Count)
            ViewMessages.RemoveAt(index);
    }

    private void SyncMessagesWithAppState()
    {
        var targetCount = MeshtasticWin.AppState.Messages.Count;
        while (ViewMessages.Count > targetCount)
            ViewMessages.RemoveAt(ViewMessages.Count - 1);

        for (var i = 0; i < targetCount; i++)
        {
            var message = MeshtasticWin.AppState.Messages[i];
            if (i >= ViewMessages.Count)
                ViewMessages.Add(CreateMessageVm(message));
            else
                UpdateMessageVm(ViewMessages[i], message);
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
            var link = new Hyperlink();
            link.Inlines.Add(new Run { Text = url });
            link.Click += async (_, __) =>
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    await Launcher.LaunchUriAsync(uri);
            };
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

    private void CopyMessageText_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item)
            return;

        var flyout = item.Parent as MenuFlyout;
        if (flyout?.Target is not RichTextBlock textBlock)
            return;

        var fullText = GetMessageText(textBlock);
        if (string.IsNullOrWhiteSpace(fullText))
            return;

        var package = new DataPackage();
        package.SetText(fullText);
        Clipboard.SetContent(package);
    }

}

public sealed class ChatListItemVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string _title = "";
    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; OnChanged(nameof(Title)); } }
    }

    private string _shortId = "";
    public string ShortId
    {
        get => _shortId;
        set { if (_shortId != value) { _shortId = value; OnChanged(nameof(ShortId)); } }
    }

    private string _lastHeard = "";
    public string LastHeard
    {
        get => _lastHeard;
        set { if (_lastHeard != value) { _lastHeard = value; OnChanged(nameof(LastHeard)); } }
    }

    private string _snr = "";
    public string SNR
    {
        get => _snr;
        set { if (_snr != value) { _snr = value; OnChanged(nameof(SNR)); } }
    }

    private string _rssi = "";
    public string RSSI
    {
        get => _rssi;
        set { if (_rssi != value) { _rssi = value; OnChanged(nameof(RSSI)); } }
    }

    private Visibility _unreadVisible = Visibility.Collapsed;
    public Visibility UnreadVisible
    {
        get => _unreadVisible;
        set { if (_unreadVisible != value) { _unreadVisible = value; OnChanged(nameof(UnreadVisible)); } }
    }

    public string? PeerIdHex { get; set; } // null = Primary

    private bool _isVisible = true;
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            OnChanged(nameof(IsVisible));
            OnChanged(nameof(RowVisibility));
        }
    }

    public Visibility RowVisibility => IsVisible ? Visibility.Visible : Visibility.Collapsed;

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

    public void UpdateFromNode(NodeLive n)
    {
        Title = string.IsNullOrWhiteSpace(n.ShortId) ? n.Name : n.Name;
        ShortId = n.ShortId ?? "";
        LastHeard = n.LastHeard ?? "—";
        SNR = n.SNR ?? "—";
        RSSI = n.RSSI ?? "—";
        UnreadVisible = MeshtasticWin.AppState.HasUnread(n.IdHex) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class MessageVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string _header = "";
    public string Header
    {
        get => _header;
        set { if (_header != value) { _header = value; OnChanged(nameof(Header)); } }
    }

    private string _text = "";
    public string Text
    {
        get => _text;
        set { if (_text != value) { _text = value; OnChanged(nameof(Text)); } }
    }

    private string _when = "";
    public string When
    {
        get => _when;
        set { if (_when != value) { _when = value; OnChanged(nameof(When)); } }
    }

    private Visibility _heardVisible = Visibility.Collapsed;
    public Visibility HeardVisible
    {
        get => _heardVisible;
        set { if (_heardVisible != value) { _heardVisible = value; OnChanged(nameof(HeardVisible)); } }
    }

    private Visibility _deliveredVisible = Visibility.Collapsed;
    public Visibility DeliveredVisible
    {
        get => _deliveredVisible;
        set { if (_deliveredVisible != value) { _deliveredVisible = value; OnChanged(nameof(DeliveredVisible)); } }
    }

    private bool _isVisible = true;
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            OnChanged(nameof(IsVisible));
            OnChanged(nameof(RowVisibility));
        }
    }

    public Visibility RowVisibility => IsVisible ? Visibility.Visible : Visibility.Collapsed;

    public static MessageVm From(MessageLive m)
        => new()
        {
            Header = m.Header,
            Text = m.Text,
            When = m.When,
            HeardVisible = (m.IsMine && m.IsHeard) ? Visibility.Visible : Visibility.Collapsed,
            DeliveredVisible = (m.IsMine && m.IsDelivered) ? Visibility.Visible : Visibility.Collapsed
        };

    public void UpdateFrom(MessageLive m)
    {
        Header = m.Header;
        Text = m.Text;
        When = m.When;
        HeardVisible = (m.IsMine && m.IsHeard) ? Visibility.Visible : Visibility.Collapsed;
        DeliveredVisible = (m.IsMine && m.IsDelivered) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
