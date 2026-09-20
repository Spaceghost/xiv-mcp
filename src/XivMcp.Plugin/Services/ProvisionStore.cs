namespace XivMcp.Plugin.Services;

/// <summary>
/// Watches the optional provisioning file and keeps <see cref="Configuration.Provision"/> in step with it.
///
/// Rules this enforces:
/// <list type="bullet">
/// <item>the file is only ever read — nothing here opens it for writing;</item>
/// <item>a malformed file keeps the last good values and surfaces <see cref="Error"/>, so a typo during a
/// config-management run can never drop the plugin back to an unprovisioned (possibly more open) bind;</item>
/// <item>the contents are never logged: only the path, the count of provisioned settings and parse errors are.</item>
/// </list>
/// Polled rather than watched with a <c>FileSystemWatcher</c>, which is unreliable across Wine's view of the
/// Linux filesystem; a few seconds of latency is fine for provisioning.
/// </summary>
public sealed class ProvisionStore
{
    private readonly string? path;
    private readonly Action<string, Exception?> log;
    private readonly Func<string, ProvisionDocument?> reader;
    private readonly Func<string, (bool Exists, DateTime Written, long Length)> stat;
    private readonly TimeSpan interval;
    private readonly Lock gate = new();

    private long nextPollTicks = long.MinValue;
    private (bool Exists, DateTime Written, long Length) lastSeen;
    private bool everRead;

    public ProvisionStore(
        string? path,
        Action<string, Exception?> log,
        TimeSpan interval = default,
        Func<string, ProvisionDocument?>? reader = null,
        Func<string, (bool Exists, DateTime Written, long Length)>? stat = null)
    {
        this.path = string.IsNullOrWhiteSpace(path) ? null : path;
        this.log = log;
        this.interval = interval <= TimeSpan.Zero ? TimeSpan.FromSeconds(3) : interval;
        this.reader = reader ?? ProvisionFile.Read;
        this.stat = stat ?? DefaultStat;
    }

    /// <summary>Where the file would be, whether or not it exists. Null when no environment variable named one.</summary>
    public string? Path => path;

    /// <summary>The settings currently in force, or null when there is no usable file.</summary>
    public ProvisionDocument? Current { get; private set; }

    /// <summary>Why the last read failed, or null. Kept until a later read succeeds or the file goes away.</summary>
    public string? Error { get; private set; }

    /// <summary>True once <see cref="Poll"/> has looked at least once.</summary>
    public bool HasChecked => everRead;

    /// <summary>
    /// Re-reads the file when it looks changed and returns true when the effective settings changed, so
    /// the caller can restart the listener. <paramref name="force"/> skips the poll interval (used on
    /// load and when the owner asks), not the change check: a file that has not been written is not
    /// re-read, so provisioning costs nothing per tick. Never throws.
    /// </summary>
    public bool Poll(Configuration config, bool force = false)
    {
        if (path is null)
        {
            everRead = true;
            return false;
        }

        lock (gate)
        {
            var now = Environment.TickCount64;
            if (!force && everRead && now < nextPollTicks)
                return false;
            nextPollTicks = now + (long)interval.TotalMilliseconds;

            var current = stat(path);
            if (everRead && current == lastSeen)
                return false;
            lastSeen = current;
            everRead = true;

            ProvisionDocument? document;
            try
            {
                document = reader(path);
            }
            catch (Exception ex)
            {
                // Keep the last good document: a half-written or mistyped file must not silently widen
                // the bind or drop a provisioned token.
                var message = ex is ArgumentException ? ex.Message : $"Provision file could not be read: {ex.Message}";
                if (message != Error)
                {
                    Error = message;
                    log($"provision file {path}: {message}", ex is ArgumentException ? null : ex);
                }

                // Re-read on the next tick even if the file did not change again.
                lastSeen = default;
                return false;
            }

            Error = null;
            if (document is { IsEmpty: true })
                document = null;

            var changed = !SameEffect(Current, document);
            Current = document;
            config.Provision = document;
            if (changed)
            {
                log(
                    document is null
                        ? $"provision file {path} is gone; the saved settings are in force again"
                        : $"provision file {path} applied ({document.Present.Count} setting(s): {string.Join(", ", document.Present.Order(StringComparer.Ordinal))})",
                    null);
            }

            return changed;
        }
    }

    /// <summary>Drops the overlay (used on unload so a reloaded plugin starts from the saved config).</summary>
    public void Detach(Configuration config)
    {
        lock (gate)
        {
            Current = null;
            config.Provision = null;
        }
    }

    /// <summary>True when both documents would produce the same effective configuration.</summary>
    private static bool SameEffect(ProvisionDocument? a, ProvisionDocument? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        return a.Present.SetEquals(b.Present)
               && a.Enabled == b.Enabled
               && a.BindMode == b.BindMode
               && a.CustomHost == b.CustomHost
               && a.Port == b.Port
               && a.Path == b.Path
               && a.RequireToken == b.RequireToken
               && a.BearerToken == b.BearerToken
               && a.CallTimeoutSeconds == b.CallTimeoutSeconds
               && a.ConfirmTimeoutSeconds == b.ConfirmTimeoutSeconds
               && SameList(a.AllowedOrigins, b.AllowedOrigins)
               && SameList(a.DisabledCategories, b.DisabledCategories);
    }

    private static bool SameList(List<string>? a, List<string>? b) =>
        a is null || b is null ? a is null && b is null : a.SequenceEqual(b, StringComparer.Ordinal);

    private static (bool Exists, DateTime Written, long Length) DefaultStat(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (true, info.LastWriteTimeUtc, info.Length) : (false, default, 0);
        }
        catch (Exception)
        {
            return (false, default, 0);
        }
    }
}
