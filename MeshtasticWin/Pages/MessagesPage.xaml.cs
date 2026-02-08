using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
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
    public ObservableCollection<ChatListItemVm> ChatListItems { get; } = new();
    public ObservableCollection<ChatListItemVm> VisibleChatItems { get; } = new();
    private readonly ObservableCollection<MessageVm> _emptyMessages = new();
    private ChatListItemVm? _selectedChatItem;

    public ObservableCollection<MessageVm> SelectedChatMessages =>
        _selectedChatItem?.Messages ?? _emptyMessages;

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
    private SortMode _sortMode = SortMode.Alphabetical;
    private ObservableCollection<ChatListItemVm> ChatsView { get; }

    private int _hideOlderThanDays = 90; // default: 3 months
    private bool _hideInactive = true;

    private readonly ChatListItemVm _primaryChatItem = ChatListItemVm.Primary();
    private readonly Dictionary<string, ChatListItemVm> _chatItemsByPeer = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _chatFilterRefreshTimer = new();
    private readonly DispatcherTimer _chatSortRefreshTimer = new();

    private enum SortMode
    {
        Alphabetical,
        LastActive
    }

    public MessagesPage()
    {
        InitializeComponent();

        AgeFilterCombo.Items.Add("Show all");
        AgeFilterCombo.Items.Add("Hide > 3 months");
        AgeFilterCombo.SelectedIndex = 1;

        SortCombo.Items.Add("Sort: Alphabetical");
        SortCombo.Items.Add("Sort: Last active");
        SortCombo.SelectedIndex = 0;

        HideInactiveToggle.IsChecked = _hideInactive;
        ChatsView = VisibleChatItems;
        ApplyChatSorting();

        MeshtasticWin.AppState.Messages.CollectionChanged += Messages_CollectionChanged;
        MeshtasticWin.AppState.Nodes.CollectionChanged += Nodes_CollectionChanged;
        MeshtasticWin.AppState.ActiveChatChanged += ActiveChatChanged;
        MeshtasticWin.AppState.UnreadChanged += UnreadChanged;

        _chatFilterRefreshTimer.Interval = TimeSpan.FromMilliseconds(200);
        _chatFilterRefreshTimer.Tick += (_, __) =>
        {
            _chatFilterRefreshTimer.Stop();
            RebuildVisibleChats();
        };

        _chatSortRefreshTimer.Interval = TimeSpan.FromMilliseconds(300);
        _chatSortRefreshTimer.Tick += (_, __) =>
        {
            _chatSortRefreshTimer.Stop();
            ApplyChatSorting();
        };

        _chatItemsByPeer[""] = _primaryChatItem;
        ChatListItems.Add(_primaryChatItem);

        foreach (var node in MeshtasticWin.AppState.Nodes)
        {
            node.PropertyChanged += Node_PropertyChanged;
            AddChatItemForNode(node);
        }

        foreach (var message in MeshtasticWin.AppState.Messages)
            AddMessageToChat(message, insertAtStart: false);

        RebuildVisibleChats();
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
                foreach (MessageLive message in e.NewItems)
                {
                    AddMessageToChat(message, insertAtStart: e.NewStartingIndex == 0);
                }
                return;
            }

            if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems is not null)
            {
                foreach (MessageLive message in e.OldItems)
                    RemoveMessageVm(message);
                return;
            }

            if (e.Action == NotifyCollectionChangedAction.Replace && e.NewItems is not null)
            {
                for (var i = 0; i < e.NewItems.Count; i++)
                {
                    if (e.NewItems[i] is MessageLive message)
                        UpdateMessageVm(message);
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

            ScheduleChatFilterRefresh();
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
            ScheduleChatFilterRefresh();
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
        RefreshChatSorting();
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

    private void ScheduleChatFilterRefresh()
    {
        if (!_hideInactive && _hideOlderThanDays >= 99999 && string.IsNullOrWhiteSpace(_chatFilter))
            return;

        if (_chatFilterRefreshTimer.IsEnabled)
            _chatFilterRefreshTimer.Stop();
        _chatFilterRefreshTimer.Start();
    }

    private void RebuildVisibleChats()
    {
        var desired = new List<ChatListItemVm>();
        foreach (var item in ChatListItems)
        {
            if (item.PeerIdHex is null)
            {
                desired.Add(item);
                continue;
            }

            var node = MeshtasticWin.AppState.Nodes.FirstOrDefault(n =>
                string.Equals(n.IdHex, item.PeerIdHex, StringComparison.OrdinalIgnoreCase));
            if (node is not null && ShouldShowChatItem(node))
                desired.Add(item);
        }

        desired = SortChatItems(desired);

        for (var i = VisibleChatItems.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(VisibleChatItems[i]))
                VisibleChatItems.RemoveAt(i);
        }

        for (var targetIndex = 0; targetIndex < desired.Count; targetIndex++)
        {
            var item = desired[targetIndex];
            if (targetIndex < VisibleChatItems.Count && ReferenceEquals(VisibleChatItems[targetIndex], item))
                continue;

            var existingIndex = VisibleChatItems.IndexOf(item);
            if (existingIndex >= 0)
                VisibleChatItems.Move(existingIndex, targetIndex);
            else
                VisibleChatItems.Insert(targetIndex, item);
        }

        EnsureChatSelectionVisible();
        RefreshChatSorting();
    }

    private void EnsureChatSelectionVisible()
    {
        if (ChatList.SelectedItem is ChatListItemVm selected && IsChatItemVisible(selected))
            return;

        var firstVisible = VisibleChatItems.FirstOrDefault();
        SetActiveChatSelection(firstVisible);
    }

    private bool IsChatItemVisible(ChatListItemVm item)
    {
        return VisibleChatItems.Contains(item);
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

        if (match is not null && IsChatItemVisible(match))
            ChatList.SelectedItem = match;
        else
            SetActiveChatSelection(VisibleChatItems.FirstOrDefault());

        _suppressListEvent = false;
    }

    private void SetActiveChatSelection(ChatListItemVm? chat)
    {
        ChatList.SelectedItem = chat;
        _selectedChatItem = chat;
        MeshtasticWin.AppState.SetActiveChatPeer(chat?.PeerIdHex);
        OnChanged(nameof(SelectedChatMessages));
    }

    private void ChatList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressListEvent)
            return;

        if (ChatList.SelectedItem is not ChatListItemVm target)
            return;

        _selectedChatItem = target;
        MeshtasticWin.AppState.SetActiveChatPeer(target.PeerIdHex);
        MeshtasticWin.AppState.MarkChatRead(target.PeerIdHex);
        OnChanged(nameof(SelectedChatMessages));
        SyncListToActiveChat();
    }

    private void ChatSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _chatFilter = ChatSearchBox.Text ?? "";
        RebuildVisibleChats();
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

        RebuildVisibleChats();
        SyncListToActiveChat();
    }

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _sortMode = SortCombo.SelectedIndex switch
        {
            1 => SortMode.LastActive,
            _ => SortMode.Alphabetical
        };

        ApplyChatSorting();
        RebuildVisibleChats();
        SyncListToActiveChat();
    }

    private void HideInactiveToggle_Click(object sender, RoutedEventArgs e)
    {
        _hideInactive = HideInactiveToggle.IsChecked == true;
        RebuildVisibleChats();
        SyncListToActiveChat();
    }

    private bool IsTooOld(NodeLive n)
    {
        if (_hideOlderThanDays >= 99999) return false;
        if (n.LastHeardUtc == DateTime.MinValue) return false;
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
        OnChanged(nameof(SelectedChatMessages));
    }

    private List<ChatListItemVm> SortChatItems(List<ChatListItemVm> items)
    {
        return _sortMode switch
        {
            SortMode.LastActive => items
                .OrderByDescending(item => item.LastHeardUtc != DateTime.MinValue)
                .ThenByDescending(item => item.LastHeardUtc)
                .ThenBy(item => item.SortNameKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.SortIdKey, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => items
                .OrderBy(item => item.SortNameKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.SortIdKey, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private void ApplyChatSorting()
    {
        if (VisibleChatItems.Count <= 1)
            return;

        var indexed = VisibleChatItems.Select((item, index) => (item, index));
        var sorted = _sortMode switch
        {
            SortMode.LastActive => indexed
                .OrderByDescending(entry => entry.item.LastHeardUtc != DateTime.MinValue)
                .ThenByDescending(entry => entry.item.LastHeardUtc)
                .ThenBy(entry => entry.item.SortNameKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.item.SortIdKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.index)
                .Select(entry => entry.item)
                .ToList(),
            _ => indexed
                .OrderBy(entry => entry.item.SortNameKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.item.SortIdKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.index)
                .Select(entry => entry.item)
                .ToList()
        };

        ApplySortedOrder(VisibleChatItems, sorted);
    }

    private void RefreshChatSorting()
    {
        if (_chatSortRefreshTimer.IsEnabled)
            _chatSortRefreshTimer.Stop();
        _chatSortRefreshTimer.Start();
    }

    private static void ApplySortedOrder<T>(ObservableCollection<T> collection, IList<T> desiredOrder)
    {
        for (var targetIndex = 0; targetIndex < desiredOrder.Count; targetIndex++)
        {
            var item = desiredOrder[targetIndex];
            var currentIndex = collection.IndexOf(item);
            if (currentIndex < 0 || currentIndex == targetIndex)
                continue;

            collection.Move(currentIndex, targetIndex);
        }
    }

    private MessageVm CreateMessageVm(MessageLive message)
    {
        var vm = MessageVm.From(message);
        vm.PeerKey = GetChatKey(message);
        return vm;
    }

    private void UpdateMessageVm(MessageLive message)
    {
        var chat = GetOrCreateChatForMessage(message);
        var vm = FindMessageVm(chat.Messages, message);
        if (vm is not null)
            vm.UpdateFrom(message);
    }

    private void RemoveMessageVm(MessageLive message)
    {
        var chat = GetOrCreateChatForMessage(message);
        var vm = FindMessageVm(chat.Messages, message);
        if (vm is not null)
            chat.Messages.Remove(vm);
    }

    private void SyncMessagesWithAppState()
    {
        foreach (var chat in ChatListItems)
            chat.Messages.Clear();

        foreach (var message in MeshtasticWin.AppState.Messages)
            AddMessageToChat(message, insertAtStart: false);
    }

    private static string NormalizePeerKey(string? peerIdHex)
        => string.IsNullOrWhiteSpace(peerIdHex) ? "" : peerIdHex.Trim();

    private static string GetChatKey(string? peerIdHex)
    {
        var normalized = NormalizePeerKey(peerIdHex);
        return string.IsNullOrWhiteSpace(normalized)
            ? "channel:primary"
            : $"dm:{normalized}";
    }

    private static string GetChatKey(MessageLive message)
    {
        if (!message.IsDirect)
            return "channel:primary";

        var peerIdHex = message.IsMine ? message.ToIdHex : message.FromIdHex;
        return $"dm:{NormalizePeerKey(peerIdHex)}";
    }

    private void AddMessageToChat(MessageLive message, bool insertAtStart)
    {
        var chat = GetOrCreateChatForMessage(message);
        var vm = CreateMessageVm(message);
        if (insertAtStart)
            chat.Messages.Insert(0, vm);
        else
            chat.Messages.Add(vm);
    }

    private ChatListItemVm GetOrCreateChatForMessage(MessageLive message)
    {
        var chatKey = GetChatKey(message);
        if (chatKey == "channel:primary")
            return _primaryChatItem;

        var peerIdHex = message.IsMine ? message.ToIdHex : message.FromIdHex;
        peerIdHex = NormalizePeerKey(peerIdHex);

        if (_chatItemsByPeer.TryGetValue(peerIdHex, out var existing))
            return existing;

        var node = MeshtasticWin.AppState.Nodes.FirstOrDefault(n =>
            string.Equals(n.IdHex, peerIdHex, StringComparison.OrdinalIgnoreCase));

        var item = node is not null
            ? ChatListItemVm.ForNode(node)
            : ChatListItemVm.ForPeer(peerIdHex);

        _chatItemsByPeer[peerIdHex] = item;
        ChatListItems.Add(item);
        ScheduleChatFilterRefresh();
        return item;
    }

    private static MessageVm? FindMessageVm(IEnumerable<MessageVm> messages, MessageLive message)
    {
        if (message.PacketId != 0)
            return messages.FirstOrDefault(vm => vm.PacketId == message.PacketId && vm.IsMine);

        return messages.FirstOrDefault(vm =>
            string.Equals(vm.Text, message.Text ?? "", StringComparison.Ordinal) &&
            string.Equals(vm.When, message.When ?? "", StringComparison.Ordinal));
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

    public ObservableCollection<MessageVm> Messages { get; } = new();

    private string _chatKey = "channel:primary";
    public string ChatKey
    {
        get => _chatKey;
        set { if (_chatKey != value) { _chatKey = value; OnChanged(nameof(ChatKey)); } }
    }

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

    private DateTime _lastHeardUtc = DateTime.MinValue;
    public DateTime LastHeardUtc
    {
        get => _lastHeardUtc;
        set { if (_lastHeardUtc != value) { _lastHeardUtc = value; OnChanged(nameof(LastHeardUtc)); } }
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

    private string _sortNameKey = "";
    public string SortNameKey
    {
        get => _sortNameKey;
        set { if (_sortNameKey != value) { _sortNameKey = value; OnChanged(nameof(SortNameKey)); } }
    }

    private string _sortIdKey = "";
    public string SortIdKey
    {
        get => _sortIdKey;
        set { if (_sortIdKey != value) { _sortIdKey = value; OnChanged(nameof(SortIdKey)); } }
    }

    public static ChatListItemVm Primary()
        => new()
        {
            Title = "Primary channel",
            ShortId = "",
            LastHeard = "Broadcast",
            LastHeardUtc = DateTime.MinValue,
            SNR = "—",
            RSSI = "—",
            UnreadVisible = MeshtasticWin.AppState.HasUnread(null) ? Visibility.Visible : Visibility.Collapsed,
            PeerIdHex = null,
            ChatKey = "channel:primary",
            SortNameKey = "PRIMARY CHANNEL",
            SortIdKey = ""
        };

    public static ChatListItemVm ForNode(NodeLive n)
        => new()
        {
            Title = string.IsNullOrWhiteSpace(n.ShortId)
                ? n.Name
                : n.Name,
            ShortId = n.ShortId ?? "",
            LastHeard = n.LastHeard ?? "—",
            LastHeardUtc = n.LastHeardUtc,
            SNR = n.SNR ?? "—",
            RSSI = n.RSSI ?? "—",
            UnreadVisible = MeshtasticWin.AppState.HasUnread(n.IdHex) ? Visibility.Visible : Visibility.Collapsed,
            PeerIdHex = n.IdHex,
            ChatKey = $"dm:{n.IdHex}",
            SortNameKey = BuildSortNameKey(n.LongName, n.ShortName, n.IdHex),
            SortIdKey = (n.IdHex ?? "").ToUpperInvariant()
        };

    public static ChatListItemVm ForPeer(string peerIdHex)
        => new()
        {
            Title = string.IsNullOrWhiteSpace(peerIdHex) ? "Unknown" : peerIdHex,
            ShortId = "",
            LastHeard = "—",
            LastHeardUtc = DateTime.MinValue,
            SNR = "—",
            RSSI = "—",
            UnreadVisible = Visibility.Collapsed,
            PeerIdHex = peerIdHex,
            ChatKey = $"dm:{peerIdHex}",
            SortNameKey = (peerIdHex ?? "").ToUpperInvariant(),
            SortIdKey = (peerIdHex ?? "").ToUpperInvariant()
        };

    public void UpdateFromNode(NodeLive n)
    {
        Title = string.IsNullOrWhiteSpace(n.ShortId) ? n.Name : n.Name;
        ShortId = n.ShortId ?? "";
        LastHeard = n.LastHeard ?? "—";
        LastHeardUtc = n.LastHeardUtc;
        SNR = n.SNR ?? "—";
        RSSI = n.RSSI ?? "—";
        UnreadVisible = MeshtasticWin.AppState.HasUnread(n.IdHex) ? Visibility.Visible : Visibility.Collapsed;
        PeerIdHex = n.IdHex;
        ChatKey = $"dm:{n.IdHex}";
        SortNameKey = BuildSortNameKey(n.LongName, n.ShortName, n.IdHex);
        SortIdKey = (n.IdHex ?? "").ToUpperInvariant();
    }

    private static string BuildSortNameKey(string? longName, string? shortName, string? idHex)
    {
        if (!string.IsNullOrWhiteSpace(longName))
            return longName.ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(shortName))
            return shortName.ToUpperInvariant();
        return (idHex ?? "").ToUpperInvariant();
    }

    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class MessageVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string _peerKey = "";
    public string PeerKey
    {
        get => _peerKey;
        set { if (_peerKey != value) { _peerKey = value; OnChanged(nameof(PeerKey)); } }
    }

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

    private uint _packetId;
    public uint PacketId
    {
        get => _packetId;
        set { if (_packetId != value) { _packetId = value; OnChanged(nameof(PacketId)); } }
    }

    private bool _isMine;
    public bool IsMine
    {
        get => _isMine;
        set { if (_isMine != value) { _isMine = value; OnChanged(nameof(IsMine)); } }
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
            Header = m.Header ?? "",
            Text = m.Text ?? "",
            When = m.When ?? "",
            HeardVisible = (m.IsMine && m.IsHeard) ? Visibility.Visible : Visibility.Collapsed,
            DeliveredVisible = (m.IsMine && m.IsDelivered) ? Visibility.Visible : Visibility.Collapsed,
            PacketId = m.PacketId,
            IsMine = m.IsMine
        };

    public void UpdateFrom(MessageLive m)
    {
        Header = m.Header ?? "";
        Text = m.Text ?? "";
        When = m.When ?? "";
        HeardVisible = (m.IsMine && m.IsHeard) ? Visibility.Visible : Visibility.Collapsed;
        DeliveredVisible = (m.IsMine && m.IsDelivered) ? Visibility.Visible : Visibility.Collapsed;
        PacketId = m.PacketId;
        IsMine = m.IsMine;
    }

    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
// TEMP: noop commit to advance main so Codex environment refreshes.
// Can be removed once sorting fix PR is merged.
