using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using XivMcp.Core;
using XivMcp.Plugin.Util;
using CsFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace XivMcp.Plugin.Providers.Media;

/// <summary>
/// Captures what the player sees. The capture is a copy of the main ImGui viewport (the surface the game
/// renders into) taken through Dalamud's texture services, encoded to PNG with the WIC encoder Dalamud
/// exposes. Nothing is written to disk and nothing is sent anywhere: the image goes back to the caller as an
/// MCP image content block.
/// </summary>
[McpProvider("media")]
public sealed class ScreenshotProvider : IDisposable
{
    private const string PngMimeType = "image/png";

    /// <summary>Frames to wait after hiding/showing the HUD so the change is on screen before capturing.</summary>
    private const int HudSettleMilliseconds = 120;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ITextureProvider textures;
    private readonly ITextureReadbackProvider readback;
    private readonly IGameConfig gameConfig;
    private readonly IPluginLog log;

    /// <summary>Main viewport id and size, refreshed from the render thread; 0 until the first frame.</summary>
    private volatile uint viewportId;
    private int viewportWidth;
    private int viewportHeight;
    private Guid pngContainer;
    private bool pngProbed;

    public ScreenshotProvider(
        IDalamudPluginInterface pluginInterface,
        ITextureProvider textures,
        ITextureReadbackProvider readback,
        IGameConfig gameConfig,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.textures = textures;
        this.readback = readback;
        this.gameConfig = gameConfig;
        this.log = log;
        pluginInterface.UiBuilder.Draw += CacheViewport;
    }

    public sealed record ScreenshotDto(
        int Width,
        int Height,
        int SourceWidth,
        int SourceHeight,
        bool Downscaled,
        int Bytes,
        string MimeType,
        string Source,
        string? Addon,
        bool HudHidden,
        bool OverlaysIncluded,
        double CaptureMs,
        DateTimeOffset CapturedAt,
        string? Note);

    public sealed record LatestScreenshotDto(
        string FileName,
        string Directory,
        DateTimeOffset ModifiedUtc,
        long FileBytes,
        int Width,
        int Height,
        int SourceWidth,
        int SourceHeight,
        bool Downscaled,
        int Bytes,
        string MimeType,
        double LoadMs);

    [McpTool("take_screenshot",
        Title = "Take a screenshot",
        Permission = ToolPermission.Ui,
        GameThread = false,
        RequiresLogin = false,
        Idempotent = false,
        Description =
            "Captures what is on the player's screen right now and returns it as a PNG image the model can look at, plus a text summary. " +
            "The capture is a copy of the game's own render surface taken inside the client; no file is written and nothing leaves the machine. " +
            "maxDimension downscales the longer side (64-4096, default 1024) — keep it small unless fine UI text has to be readable. " +
            "hideUi=true hides the game's HUD for the capture and restores it afterwards (adds about 0.3 s). " +
            "includeOverlays=false captures the frame before Dalamud/plugin windows are drawn, so only the game is visible. " +
            "addon captures just one game window's rectangle (internal addon name from list_addons, e.g. \"ItemDetail\", \"Talk\", \"_PartyList\"); " +
            "the addon must be loaded and visible, and addonPadding adds pixels around it. " +
            "The result carries width/height, sourceWidth/sourceHeight, bytes and captureMs (how long the capture took). " +
            "Fails when the game has not drawn a frame yet (e.g. still loading) or when the named addon is not on screen. " +
            "Use get_addon_text instead when you only need the text of a window — it is far cheaper than an image.")]
    public async Task<ToolResult> TakeScreenshot(
        [McpParam("Longer side of the returned image in pixels (64-4096). The capture is never scaled up.", Minimum = CaptureGeometry.MinDimension, Maximum = CaptureGeometry.MaxDimension)]
        int maxDimension = 1024,
        [McpParam("Hide the game's HUD for the capture and restore it afterwards.")] bool hideUi = false,
        [McpParam("Include Dalamud and plugin windows (ImGui overlays). False captures the game frame only.")] bool includeOverlays = true,
        [McpParam("Capture only this addon's rectangle (internal name from list_addons). Omit for the whole screen.")] string? addon = null,
        [McpParam("Extra pixels around the addon rectangle (0-200).", Minimum = 0, Maximum = 200)] int addonPadding = 8,
        ToolContext? ctx = null)
    {
        if (ctx == null)
        {
            throw new McpToolException("Tool context unavailable.");
        }

        maxDimension = Math.Clamp(maxDimension, CaptureGeometry.MinDimension, CaptureGeometry.MaxDimension);
        addonPadding = Math.Clamp(addonPadding, 0, 200);
        var addonName = string.IsNullOrWhiteSpace(addon) ? null : addon.Trim();

        var id = viewportId;
        if (id == 0)
        {
            throw new McpToolException("The game has not drawn a frame yet; try again in a moment.");
        }

        var stopwatch = Stopwatch.StartNew();
        var args = new ImGuiViewportTextureArgs
        {
            ViewportId = id,
            AutoUpdate = false,
            KeepTransparency = false,
            TakeBeforeImGuiRender = !includeOverlays,
        };

        string? note = null;
        if (addonName != null)
        {
            var rect = await ctx.Game.InvokeAsync(() => AddonRect(addonName), ctx.CancellationToken).ConfigureAwait(false);
            if (rect == null)
            {
                throw new McpToolException($"Addon \"{addonName}\" is not loaded or not visible. Use list_addons to see what is on screen.");
            }

            var uv = CaptureGeometry.RectToUv(
                rect.Value.X, rect.Value.Y, rect.Value.Width, rect.Value.Height,
                Volatile.Read(ref viewportWidth), Volatile.Read(ref viewportHeight), addonPadding);
            if (uv == null)
            {
                throw new McpToolException($"Addon \"{addonName}\" is off screen or has no size right now.");
            }

            args.Uv0 = uv.Value.Uv0;
            args.Uv1 = uv.Value.Uv1;
        }

        var hudWasHidden = false;
        try
        {
            if (hideUi)
            {
                hudWasHidden = await ctx.Game.InvokeAsync(() => SetHudVisible(false), ctx.CancellationToken).ConfigureAwait(false);
                if (hudWasHidden)
                {
                    await Task.Delay(HudSettleMilliseconds, ctx.CancellationToken).ConfigureAwait(false);
                }
                else
                {
                    note = "The HUD could not be hidden (the UI module was not available); the capture includes it.";
                }
            }

            using var captured = await textures
                .CreateFromImGuiViewportAsync(args, "xivmcp-screenshot", ctx.CancellationToken)
                .ConfigureAwait(false);

            var (bytes, width, height, sourceWidth, sourceHeight) =
                await EncodeAsync(captured, maxDimension, ctx.CancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            var dto = new ScreenshotDto(
                width,
                height,
                sourceWidth,
                sourceHeight,
                width != sourceWidth || height != sourceHeight,
                bytes.Length,
                PngMimeType,
                "viewport",
                addonName,
                hideUi && hudWasHidden,
                includeOverlays,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1),
                DateTimeOffset.UtcNow,
                note);

            return Image(bytes, PngMimeType, dto,
                $"Screenshot {width}x{height} (from {sourceWidth}x{sourceHeight}), {bytes.Length / 1024} KiB, captured in {dto.CaptureMs:0.#} ms" +
                (addonName != null ? $", addon {addonName}" : "") + ".");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (McpToolException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "take_screenshot failed");
            throw new McpToolException(
                "Could not capture the screen: " + ex.Message +
                ". The game may be minimised, between frames or using a renderer this capture path cannot read.");
        }
        finally
        {
            if (hudWasHidden)
            {
                try
                {
                    await ctx.Game.InvokeAsync(() => SetHudVisible(true), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.Error(ex, "Could not restore the HUD after take_screenshot");
                }
            }
        }
    }

    [McpTool("get_latest_screenshot",
        Title = "Get the newest saved screenshot",
        Permission = ToolPermission.Read,
        GameThread = false,
        RequiresLogin = false,
        Description =
            "Returns the newest screenshot the player has already saved (the files the game writes when they press the screenshot key), " +
            "as a PNG/JPEG image plus fileName, directory, modifiedUtc and fileBytes. It only reads files — it never takes a new picture; " +
            "use take_screenshot for that. The folder is the game's ScreenShot Dir setting, or <game user folder>/screenshots. " +
            "maxDimension downscales the longer side (64-4096, default 1024). maxAgeSeconds rejects anything older, so an agent can ask " +
            "'the shot the player just took' without picking up last week's. Fails when the folder does not exist or holds no image.")]
    public async Task<ToolResult> GetLatestScreenshot(
        [McpParam("Longer side of the returned image in pixels (64-4096).", Minimum = CaptureGeometry.MinDimension, Maximum = CaptureGeometry.MaxDimension)]
        int maxDimension = 1024,
        [McpParam("Only return a file modified within this many seconds (0 = any age).", Minimum = 0, Maximum = 2592000)]
        int maxAgeSeconds = 0,
        ToolContext? ctx = null)
    {
        if (ctx == null)
        {
            throw new McpToolException("Tool context unavailable.");
        }

        maxDimension = Math.Clamp(maxDimension, CaptureGeometry.MinDimension, CaptureGeometry.MaxDimension);
        var stopwatch = Stopwatch.StartNew();
        var directory = await ctx.Game.InvokeAsync(ScreenshotDirectory, ctx.CancellationToken).ConfigureAwait(false)
                        ?? throw new McpToolException("The game's screenshot folder could not be determined (no ScreenShot Dir setting and no user folder).");

        if (!Directory.Exists(directory))
        {
            throw new McpToolException($"The screenshot folder \"{directory}\" does not exist yet.");
        }

        var candidates = new DirectoryInfo(directory)
            .EnumerateFiles()
            .Where(f => ScreenshotFiles.IsImage(f.Name))
            .Select(f => (f.Name, Modified: (DateTimeOffset)f.LastWriteTimeUtc))
            .ToList();
        var newest = ScreenshotFiles.Newest(candidates)
                     ?? throw new McpToolException($"No PNG or JPEG screenshot in \"{directory}\".");

        if (maxAgeSeconds > 0 && (DateTimeOffset.UtcNow - newest.Modified).TotalSeconds > maxAgeSeconds)
        {
            throw new McpToolException(
                $"The newest screenshot (\"{newest.Name}\") is older than {maxAgeSeconds} s. Ask the player to take one, or call take_screenshot.");
        }

        var path = Path.Combine(directory, newest.Name);
        var fileBytes = new FileInfo(path).Length;
        byte[] raw;
        try
        {
            raw = await File.ReadAllBytesAsync(path, ctx.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new McpToolException($"Could not read \"{newest.Name}\": {ex.Message}");
        }

        using var loaded = await textures
            .CreateFromImageAsync(raw, "xivmcp-latest-screenshot", ctx.CancellationToken)
            .ConfigureAwait(false);
        var (bytes, width, height, sourceWidth, sourceHeight) =
            await EncodeAsync(loaded, maxDimension, ctx.CancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        var dto = new LatestScreenshotDto(
            newest.Name,
            directory,
            newest.Modified,
            fileBytes,
            width,
            height,
            sourceWidth,
            sourceHeight,
            width != sourceWidth || height != sourceHeight,
            bytes.Length,
            PngMimeType,
            Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1));

        return Image(bytes, PngMimeType, dto,
            $"{newest.Name} ({newest.Modified:u}), {width}x{height} from {sourceWidth}x{sourceHeight}, {bytes.Length / 1024} KiB.");
    }

    public void Dispose()
    {
        try
        {
            pluginInterface.UiBuilder.Draw -= CacheViewport;
        }
        catch
        {
            // Never throw out of Dispose.
        }
    }

    private static ToolResult Image(byte[] bytes, string mimeType, object structured, string summary) =>
        new()
        {
            Content =
            [
                ContentBlock.FromText(summary),
                ContentBlock.FromImage(bytes, mimeType),
            ],
            StructuredContent = System.Text.Json.JsonSerializer.SerializeToNode(structured, McpJson.Options),
        };

    /// <summary>Downscales if needed and encodes to PNG. Returns the bytes and both sizes.</summary>
    private async Task<(byte[] Bytes, int Width, int Height, int SourceWidth, int SourceHeight)> EncodeAsync(
        IDalamudTextureWrap source,
        int maxDimension,
        CancellationToken cancellationToken)
    {
        var sourceWidth = source.Width;
        var sourceHeight = source.Height;
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            throw new McpToolException("The capture came back empty (0x0); try again in a moment.");
        }

        var (width, height) = CaptureGeometry.Fit(sourceWidth, sourceHeight, maxDimension);
        IDalamudTextureWrap? resized = null;
        try
        {
            var wrap = source;
            if (width != sourceWidth || height != sourceHeight)
            {
                resized = await textures.CreateFromExistingTextureAsync(
                    source,
                    new TextureModificationArgs { NewWidth = width, NewHeight = height, MakeOpaque = true },
                    leaveWrapOpen: true,
                    "xivmcp-screenshot-scaled",
                    cancellationToken).ConfigureAwait(false);
                wrap = resized;
            }

            using var stream = new MemoryStream(Math.Max(4096, width * height / 4));
            await readback.SaveToStreamAsync(
                wrap,
                PngContainerGuid(),
                stream,
                props: null,
                leaveWrapOpen: true,
                leaveStreamOpen: true,
                cancellationToken).ConfigureAwait(false);
            return (stream.ToArray(), width, height, sourceWidth, sourceHeight);
        }
        finally
        {
            resized?.Dispose();
        }
    }

    private Guid PngContainerGuid()
    {
        if (pngProbed)
        {
            return pngContainer;
        }

        foreach (var codec in readback.GetSupportedImageEncoderInfos())
        {
            if (codec.MimeTypes.Any(m => string.Equals(m, PngMimeType, StringComparison.OrdinalIgnoreCase)))
            {
                pngContainer = codec.ContainerGuid;
                pngProbed = true;
                return pngContainer;
            }
        }

        throw new McpToolException("This client has no PNG encoder available, so screenshots cannot be encoded.");
    }

    /// <summary>Runs on the render thread from UiBuilder.Draw; only caches numbers.</summary>
    private void CacheViewport()
    {
        try
        {
            var viewport = ImGui.GetMainViewport();
            viewportId = viewport.ID;
            Volatile.Write(ref viewportWidth, (int)MathF.Round(viewport.Size.X));
            Volatile.Write(ref viewportHeight, (int)MathF.Round(viewport.Size.Y));
        }
        catch
        {
            // Never let an exception escape a draw handler.
        }
    }

    private static unsafe (float X, float Y, float Width, float Height)? AddonRect(string name)
    {
        var manager = RaptureAtkUnitManager.Instance();
        if (manager == null)
        {
            return null;
        }

        var addon = manager->GetAddonByName(name, 1);
        if (addon == null || !addon->IsVisible)
        {
            return null;
        }

        var root = addon->RootNode;
        if (root == null)
        {
            return null;
        }

        var scale = addon->Scale <= 0 ? 1f : addon->Scale;
        return (addon->X, addon->Y, root->Width * scale, root->Height * scale);
    }

    /// <summary>Sets the game HUD's visibility. Returns true when the call was actually made.</summary>
    private static unsafe bool SetHudVisible(bool visible)
    {
        var module = RaptureAtkModule.Instance();
        if (module == null)
        {
            return false;
        }

        module->SetUiVisibility(visible);
        return true;
    }

    private unsafe string? ScreenshotDirectory()
    {
        try
        {
            if (gameConfig.System.TryGetString("ScreenShotDir", out var configured) && !string.IsNullOrWhiteSpace(configured))
            {
                return configured.TrimEnd('\\', '/');
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "ScreenShotDir is not readable");
        }

        var framework = CsFramework.Instance();
        if (framework == null)
        {
            return null;
        }

        var userPath = framework->UserPathString;
        return string.IsNullOrWhiteSpace(userPath) ? null : Path.Combine(userPath.TrimEnd('\\', '/'), "screenshots");
    }
}
