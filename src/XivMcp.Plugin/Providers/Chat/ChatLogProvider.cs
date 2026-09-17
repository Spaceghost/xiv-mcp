using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.Chat;

/// <summary>
/// Captures chat lines into a ring buffer from plugin load onwards and serves read_chat and
/// ffxiv://chat/recent. Battle-log lines (damage, heals, buffs) are not captured so combat does not
/// flush real conversation out of the buffer.
/// </summary>
/// <remarks>
/// Privacy: private tells (TellIncoming/TellOutgoing/GmTell) are captured but hidden from read_chat
/// unless the caller passes includeTells=true or names a tell channel explicitly, and they are never
/// included in the ffxiv://chat/recent resource (which clients may poll or subscribe to passively).
/// </remarks>
[McpProvider("chat")]
public sealed class ChatLogProvider : IDisposable
{
    public const string RecentUri = "ffxiv://chat/recent";

    private const int RecentCount = 100;
    private const int FallbackCapacity = 1000;
    private const int NotifyIntervalMs = 500;

    private static readonly HashSet<XivChatType> TellTypes = [XivChatType.TellIncoming, XivChatType.TellOutgoing, XivChatType.GmTell];

    private static readonly HashSet<XivChatType> BattleTypes =
    [
        XivChatType.Damage, XivChatType.Miss, XivChatType.Action, XivChatType.Item, XivChatType.Healing,
        XivChatType.GainBuff, XivChatType.GainDebuff, XivChatType.LoseBuff, XivChatType.LoseDebuff,
    ];

    private static readonly Dictionary<string, XivChatType[]> ChannelGroups = BuildChannelGroups();

    private readonly IChatGui chatGui;
    private readonly IMcpNotifier notifier;
    private readonly Configuration configuration;
    private readonly IPluginLog log;

    private readonly object gate = new();
    private Entry?[] ring;
    private int head; // index of the oldest entry
    private int count;
    private long nextSequence = 1;

    private readonly object notifyGate = new();
    private readonly Timer notifyTimer;
    private long lastNotifyTicks;
    private bool notifyScheduled;
    private bool disposed;

    public ChatLogProvider(IChatGui chatGui, IMcpNotifier notifier, Configuration configuration, IPluginLog log)
    {
        this.chatGui = chatGui;
        this.notifier = notifier;
        this.configuration = configuration;
        this.log = log;
        ring = new Entry?[ResolveCapacity()];
        notifyTimer = new Timer(OnNotifyTimer, null, Timeout.Infinite, Timeout.Infinite);

        chatGui.ChatMessageHandled += OnChatMessage;
        chatGui.ChatMessageUnhandled += OnChatMessage;
    }

    [McpTool("read_chat",
        Title = "Read chat log",
        Description =
            "Returns chat lines the plugin has captured since it loaded (not older history), oldest first. " +
            "Each entry: sequence (monotonic, use for paging/polling), timestamp, chatType (Dalamud XivChatType name such as Say, Shout, Party, FreeCompany, Ls1, CrossLinkShell1, TellIncoming, Echo, SystemMessage, NPCDialogue), chatTypeId, sender (plain text, omitted when none), message (plain text; item links/icons flattened), isHandled (a plugin suppressed it from the visible log). " +
            "Without afterSequence you get the newest `limit` matching lines; to poll for new lines pass the previous response's nextAfterSequence. " +
            "Filters: channels (XivChatType names, numeric ids, or groups: tell, party, alliance, fc, linkshell, ls1-ls8, cwls, cwls1-cwls8, novice, emote, echo, system, npc, pvpteam), contains (message substring), senderContains — all case-insensitive. " +
            "Battle-log lines (damage, healing, buffs) are not captured. " +
            "PRIVACY: private tells are hidden unless includeTells=true or channels names a tell type; hiddenTells reports how many matching tells were withheld. Only request tells when the user asked about their private messages.",
        GameThread = false, RequiresLogin = false)]
    public ReadChatResult ReadChat(
        [McpParam("Maximum entries to return.", Minimum = 1, Maximum = 500)] int limit = 50,
        [McpParam("Return only entries with a sequence greater than this (from a previous nextAfterSequence). Omit to get the newest entries.")] long? afterSequence = null,
        [McpParam("Chat types or groups to include, e.g. [\"party\", \"FreeCompany\", \"Say\"]. Omit for all captured types.")] string[]? channels = null,
        [McpParam("Only messages whose text contains this (case-insensitive).")] string? contains = null,
        [McpParam("Only messages whose sender name contains this (case-insensitive).")] string? senderContains = null,
        [McpParam("Include private tells. Default false; tells are also included when channels names a tell type.")] bool includeTells = false)
    {
        limit = Math.Clamp(limit, 1, 500);
        var typeFilter = ParseChannels(channels);
        var tellsAllowed = includeTells || (typeFilter?.Overlaps(TellTypes) ?? false);
        contains = string.IsNullOrEmpty(contains) ? null : contains;
        senderContains = string.IsNullOrEmpty(senderContains) ? null : senderContains;

        Entry[] snapshot;
        int capacity;
        long latest;
        lock (gate)
        {
            snapshot = new Entry[count];
            for (var i = 0; i < count; i++)
                snapshot[i] = ring[(head + i) % ring.Length]!;
            capacity = ring.Length;
            latest = nextSequence - 1;
        }

        var oldest = snapshot.Length > 0 ? snapshot[0].Sequence : latest + 1;
        var hiddenTells = 0;
        var selected = new List<Entry>(Math.Min(limit, snapshot.Length));
        var truncated = false;

        bool Matches(Entry e)
        {
            if (typeFilter != null && !typeFilter.Contains(e.Type))
                return false;
            if (contains != null && e.Message.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            if (senderContains != null && e.Sender.IndexOf(senderContains, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            if (!tellsAllowed && TellTypes.Contains(e.Type))
            {
                hiddenTells++;
                return false;
            }

            return true;
        }

        if (afterSequence is { } after)
        {
            foreach (var e in snapshot)
            {
                if (e.Sequence <= after || !Matches(e))
                    continue;
                if (selected.Count == limit)
                {
                    truncated = true;
                    break;
                }

                selected.Add(e);
            }
        }
        else
        {
            for (var i = snapshot.Length - 1; i >= 0; i--)
            {
                var e = snapshot[i];
                if (!Matches(e))
                    continue;
                if (selected.Count == limit)
                {
                    truncated = true;
                    break;
                }

                selected.Add(e);
            }

            selected.Reverse();
        }

        long nextAfter = afterSequence is not null && truncated && selected.Count > 0 ? selected[^1].Sequence : latest;

        return new ReadChatResult(
            selected.Select(ToDto).ToList(),
            selected.Count,
            truncated,
            nextAfter,
            latest,
            oldest,
            snapshot.Length,
            capacity,
            afterSequence is { } a && a + 1 < oldest && latest >= oldest ? true : null,
            hiddenTells > 0 ? hiddenTells : null);
    }

    [McpResource(RecentUri,
        Name = "Recent chat",
        Description = "The newest 100 captured chat lines (oldest first) in the same shape as read_chat, excluding private tells and battle-log lines. Subscribers get notifications/resources/updated at most twice per second while chat is active.",
        GameThread = false, RequiresLogin = false)]
    public RecentChatResource GetRecent()
    {
        var entries = new List<ChatEntryDto>(RecentCount);
        long latest;
        lock (gate)
        {
            latest = nextSequence - 1;
            for (var i = count - 1; i >= 0 && entries.Count < RecentCount; i--)
            {
                var e = ring[(head + i) % ring.Length]!;
                if (!TellTypes.Contains(e.Type))
                    entries.Add(ToDto(e));
            }
        }

        entries.Reverse();
        return new RecentChatResource(entries, latest);
    }

    public void Dispose()
    {
        chatGui.ChatMessageHandled -= OnChatMessage;
        chatGui.ChatMessageUnhandled -= OnChatMessage;
        lock (notifyGate)
            disposed = true;
        notifyTimer.Dispose();
    }

    private void OnChatMessage(IChatMessage message)
    {
        try
        {
            var type = message.LogKind;
            if (BattleTypes.Contains(type))
                return;

            var timestamp = message.Timestamp > 0 ? DateTimeOffset.FromUnixTimeSeconds(message.Timestamp) : DateTimeOffset.UtcNow;
            var sender = message.Sender?.TextValue ?? "";
            var text = message.Message?.TextValue ?? "";

            lock (gate)
            {
                var capacity = ResolveCapacity();
                if (capacity != ring.Length)
                    Resize(capacity);

                var entry = new Entry(nextSequence++, timestamp, type, sender, text, message.IsHandled);
                if (count < ring.Length)
                {
                    ring[(head + count) % ring.Length] = entry;
                    count++;
                }
                else
                {
                    ring[head] = entry;
                    head = (head + 1) % ring.Length;
                }
            }

            if (!TellTypes.Contains(type))
                ScheduleNotify();
        }
        catch (Exception ex)
        {
            try
            {
                log.Error(ex, "xiv-mcp: failed to capture chat message");
            }
            catch
            {
                // Never let anything escape a game event handler.
            }
        }
    }

    private int ResolveCapacity()
    {
        var size = configuration.ChatBufferSize;
        return size > 0 ? Math.Clamp(size, 50, 5000) : FallbackCapacity;
    }

    private void Resize(int capacity)
    {
        var keep = Math.Min(count, capacity);
        var next = new Entry?[capacity];
        for (var i = 0; i < keep; i++)
            next[i] = ring[(head + count - keep + i) % ring.Length];
        ring = next;
        head = 0;
        count = keep;
    }

    private void ScheduleNotify()
    {
        lock (notifyGate)
        {
            if (disposed || notifyScheduled)
                return;
            notifyScheduled = true;
            var elapsed = Environment.TickCount64 - lastNotifyTicks;
            var due = elapsed >= NotifyIntervalMs ? 0 : NotifyIntervalMs - elapsed;
            notifyTimer.Change(due, Timeout.Infinite);
        }
    }

    private void OnNotifyTimer(object? state)
    {
        lock (notifyGate)
        {
            notifyScheduled = false;
            if (disposed)
                return;
            lastNotifyTicks = Environment.TickCount64;
        }

        try
        {
            notifier.ResourceUpdated(RecentUri);
        }
        catch (Exception ex)
        {
            try
            {
                log.Warning(ex, "xiv-mcp: chat resource notification failed");
            }
            catch
            {
                // ignored
            }
        }
    }

    private static ChatEntryDto ToDto(Entry e) => new(
        e.Sequence,
        e.Timestamp,
        Enum.IsDefined(e.Type) ? e.Type.ToString() : ((ushort)e.Type).ToString(),
        (ushort)e.Type,
        e.Sender.Length > 0 ? e.Sender : null,
        e.Message,
        e.IsHandled);

    private static HashSet<XivChatType>? ParseChannels(string[]? channels)
    {
        if (channels is null || channels.Length == 0)
            return null;

        var set = new HashSet<XivChatType>();
        foreach (var raw in channels)
        {
            var name = raw?.Trim();
            if (string.IsNullOrEmpty(name))
                continue;
            if (ChannelGroups.TryGetValue(name, out var group))
            {
                set.UnionWith(group);
                continue;
            }

            if (ushort.TryParse(name, out var id))
            {
                set.Add((XivChatType)id);
                continue;
            }

            if (Enum.TryParse<XivChatType>(name, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            {
                set.Add(parsed);
                continue;
            }

            throw new McpToolException(
                $"Unknown chat channel '{name}'. Use XivChatType names (Say, Shout, Yell, Party, Alliance, FreeCompany, Ls1..Ls8, CrossLinkShell1..CrossLinkShell8, NoviceNetwork, TellIncoming, TellOutgoing, CustomEmote, StandardEmote, Echo, SystemMessage, ErrorMessage, NPCDialogue, LootNotice), numeric ids, " +
                "or groups: tell, party, alliance, fc, linkshell, ls1-ls8, cwls, cwls1-cwls8, novice, emote, echo, system, npc, pvpteam.");
        }

        return set.Count > 0 ? set : null;
    }

    private static Dictionary<string, XivChatType[]> BuildChannelGroups()
    {
        var d = new Dictionary<string, XivChatType[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["tell"] = [XivChatType.TellIncoming, XivChatType.TellOutgoing, XivChatType.GmTell],
            ["tells"] = [XivChatType.TellIncoming, XivChatType.TellOutgoing, XivChatType.GmTell],
            ["say"] = [XivChatType.Say, XivChatType.GmSay],
            ["shout"] = [XivChatType.Shout, XivChatType.GmShout],
            ["yell"] = [XivChatType.Yell, XivChatType.GmYell],
            ["party"] = [XivChatType.Party, XivChatType.CrossParty, XivChatType.GmParty],
            ["alliance"] = [XivChatType.Alliance],
            ["fc"] = [XivChatType.FreeCompany, XivChatType.GmFreeCompany, XivChatType.FreeCompanyAnnouncement, XivChatType.FreeCompanyLoginLogout],
            ["freeCompany"] = [XivChatType.FreeCompany, XivChatType.GmFreeCompany, XivChatType.FreeCompanyAnnouncement, XivChatType.FreeCompanyLoginLogout],
            ["linkshell"] = [XivChatType.Ls1, XivChatType.Ls2, XivChatType.Ls3, XivChatType.Ls4, XivChatType.Ls5, XivChatType.Ls6, XivChatType.Ls7, XivChatType.Ls8],
            ["cwls"] =
            [
                XivChatType.CrossLinkShell1, XivChatType.CrossLinkShell2, XivChatType.CrossLinkShell3, XivChatType.CrossLinkShell4,
                XivChatType.CrossLinkShell5, XivChatType.CrossLinkShell6, XivChatType.CrossLinkShell7, XivChatType.CrossLinkShell8,
            ],
            ["novice"] = [XivChatType.NoviceNetwork, XivChatType.NoviceNetworkSystem, XivChatType.GmNoviceNetwork],
            ["emote"] = [XivChatType.CustomEmote, XivChatType.StandardEmote],
            ["echo"] = [XivChatType.Echo],
            ["system"] = [XivChatType.SystemMessage, XivChatType.SystemError, XivChatType.ErrorMessage, XivChatType.Notice, XivChatType.Urgent, XivChatType.GatheringSystemMessage],
            ["npc"] = [XivChatType.NPCDialogue, XivChatType.NPCDialogueAnnouncements],
            ["pvpteam"] = [XivChatType.PvPTeam, XivChatType.PvpTeamAnnouncement, XivChatType.PvpTeamLoginLogout],
        };
        d["crossLinkshell"] = d["cwls"];

        XivChatType[] ls = [XivChatType.Ls1, XivChatType.Ls2, XivChatType.Ls3, XivChatType.Ls4, XivChatType.Ls5, XivChatType.Ls6, XivChatType.Ls7, XivChatType.Ls8];
        XivChatType[] gmLs = [XivChatType.GmLinkshell1, XivChatType.GmLinkshell2, XivChatType.GmLinkshell3, XivChatType.GmLinkshell4, XivChatType.GmLinkshell5, XivChatType.GmLinkshell6, XivChatType.GmLinkshell7, XivChatType.GmLinkshell8];
        XivChatType[] cw = d["cwls"];
        for (var i = 0; i < 8; i++)
        {
            d[$"ls{i + 1}"] = [ls[i], gmLs[i]];
            d[$"linkshell{i + 1}"] = [ls[i], gmLs[i]];
            d[$"cwls{i + 1}"] = [cw[i]];
            d[$"cw{i + 1}"] = [cw[i]];
            d[$"crossLinkshell{i + 1}"] = [cw[i]];
        }

        return d;
    }

    private sealed record Entry(long Sequence, DateTimeOffset Timestamp, XivChatType Type, string Sender, string Message, bool IsHandled);

    public sealed record ChatEntryDto(
        long Sequence,
        DateTimeOffset Timestamp,
        string ChatType,
        int ChatTypeId,
        string? Sender,
        string Message,
        bool IsHandled);

    public sealed record ReadChatResult(
        IReadOnlyList<ChatEntryDto> Entries,
        int Returned,
        bool Truncated,
        long NextAfterSequence,
        long LatestSequence,
        long OldestBufferedSequence,
        int Buffered,
        int Capacity,
        bool? Gap,
        int? HiddenTells);

    public sealed record RecentChatResource(IReadOnlyList<ChatEntryDto> Entries, long LatestSequence);
}
