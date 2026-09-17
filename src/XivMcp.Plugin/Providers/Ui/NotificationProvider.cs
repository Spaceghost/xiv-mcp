using System.Text;
using Dalamud.Game.Gui.Toast;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.Ui;

/// <summary>Local, on-screen notices: game toasts and Dalamud notifications.</summary>
[McpProvider("ui")]
public sealed class NotificationProvider
{
    private const int MaxToastLength = 200;
    private const int MaxTitleLength = 100;
    private const int MaxContentLength = 2000;

    private readonly IToastGui toastGui;
    private readonly INotificationManager notificationManager;

    public NotificationProvider(IToastGui toastGui, INotificationManager notificationManager)
    {
        this.toastGui = toastGui;
        this.notificationManager = notificationManager;
    }

    public enum ToastKind
    {
        Normal,
        Quest,
        Error,
    }

    public enum NotificationKind
    {
        Info,
        Success,
        Warning,
        Error,
    }

    [McpTool("show_toast",
        Title = "Show game toast",
        Description =
            "Shows a short, transient on-screen message using the game's own toast styles, visible only to the user: " +
            "normal (small banner near the top of the screen), quest (large centred quest-style text with a chime), error (red text like \"Target is not in range.\"). " +
            "Use for brief alerts the user should notice without reading chat. Single line, at most 200 characters (longer text is cut). Nothing is sent to other players.",
        Permission = ToolPermission.Ui, Idempotent = false)]
    public ToastResult ShowToast(
        [McpParam("Text to show (single line).")] string message,
        [McpParam("Toast style.")] ToastKind kind = ToastKind.Normal)
    {
        var text = Flatten(message, MaxToastLength, "message", out var truncated);
        switch (kind)
        {
            case ToastKind.Quest:
                toastGui.ShowQuest(text);
                break;
            case ToastKind.Error:
                toastGui.ShowError(text);
                break;
            default:
                toastGui.ShowNormal(text);
                break;
        }

        return new ToastResult(text, kind.ToString().ToLowerInvariant(), truncated ? true : null);
    }

    [McpTool("show_notification",
        Title = "Show Dalamud notification",
        Description =
            "Shows a Dalamud overlay notification card (bottom-right corner, with title, text and a coloured icon for the type) visible only to the user; works on the title screen too. " +
            "Better than a toast for longer or lasting messages such as \"Your retainer ventures are done\" or a task summary. " +
            "type: info, success, warning or error. durationSeconds 1-600 (default 8; hovering keeps it open). Content up to 2000 characters, line breaks allowed.",
        Permission = ToolPermission.Ui, Idempotent = false, RequiresLogin = false)]
    public NotificationResult ShowNotification(
        [McpParam("Body text.")] string content,
        [McpParam("Title line; defaults to \"xiv-mcp\".")] string? title = null,
        [McpParam("Notification type (icon and colour).")] NotificationKind type = NotificationKind.Info,
        [McpParam("How long it stays visible, in seconds.", Minimum = 1, Maximum = 600)] double durationSeconds = 8)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new McpToolException("content is empty.");

        var body = StripControl(content, keepNewlines: true).Trim();
        if (body.Length == 0)
            throw new McpToolException("content is empty after removing control characters.");
        var truncated = body.Length > MaxContentLength;
        if (truncated)
            body = body[..MaxContentLength] + "…";

        var heading = string.IsNullOrWhiteSpace(title) ? "xiv-mcp" : Flatten(title, MaxTitleLength, "title", out _);
        if (double.IsNaN(durationSeconds))
            durationSeconds = 8;
        var duration = TimeSpan.FromSeconds(Math.Clamp(durationSeconds, 1, 600));

        notificationManager.AddNotification(new Notification
        {
            Title = heading,
            Content = body,
            Type = type switch
            {
                NotificationKind.Success => NotificationType.Success,
                NotificationKind.Warning => NotificationType.Warning,
                NotificationKind.Error => NotificationType.Error,
                _ => NotificationType.Info,
            },
            InitialDuration = duration,
            Minimized = false,
        });

        return new NotificationResult(heading, body, type.ToString().ToLowerInvariant(), duration.TotalSeconds, truncated ? true : null);
    }

    private static string Flatten(string? text, int max, string what, out bool truncated)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new McpToolException($"{what} is empty.");
        var flat = StripControl(text, keepNewlines: false).Trim();
        if (flat.Length == 0)
            throw new McpToolException($"{what} is empty after removing control characters.");
        truncated = flat.Length > max;
        return truncated ? flat[..max] : flat;
    }

    private static string StripControl(string text, bool keepNewlines)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch == '\n')
                sb.Append(keepNewlines ? '\n' : ' ');
            else if (ch is '\r')
                continue;
            else if (ch == '\t')
                sb.Append(' ');
            else if (!char.IsControl(ch))
                sb.Append(ch);
        }

        return sb.ToString();
    }

    public sealed record ToastResult(string Shown, string Kind, bool? Truncated);

    public sealed record NotificationResult(string Title, string Content, string Type, double DurationSeconds, bool? Truncated);
}
