using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace XivMcp.Plugin.Tests;

public sealed class ConfigurationTests
{
    // Dalamud's own serializer settings for plugin configs.
    private static readonly JsonSerializerSettings DalamudSettings = new()
    {
        TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
        TypeNameHandling = TypeNameHandling.Objects,
    };

    private const string V1File = """
        {"$type":"XivMcp.Plugin.Configuration, XivMcp","Version":1,"Enabled":true,"Host":"127.0.0.1","Port":41800,
         "BearerToken":"KEEP_THIS_TOKEN_abcdefghijklmnopqrstuvwxyz0","RequireToken":true,"AllowedOrigins":[],"CallTimeoutSeconds":30,
         "AllowRead":true,"AllowUi":true,"AllowAction":true,"AllowChat":true,"ConfirmActions":true,"ConfirmTimeoutSeconds":20,
         "DisabledCategories":[],"ChatBufferSize":500,"ActivityLogLevel":3,"ShowDtrEntry":true,"NotifyAgentCompletion":true,
         "AgentBoardExpiryMinutes":120,"HostIsLoopback":true,"EndpointUrl":"http://127.0.0.1:41800/mcp"}
        """;

    [Fact]
    public void ComputedPropertiesAreNotSerialized()
    {
        var json = JObject.Parse(JsonConvert.SerializeObject(new Configuration(), DalamudSettings));
        Assert.Null(json["HostIsLoopback"]);
        Assert.Null(json["EndpointUrl"]);
        Assert.NotNull(json["BearerToken"]);
    }

    [Fact]
    public void V1MigrationKeepsTokenAndTiers()
    {
        var config = JsonConvert.DeserializeObject<Configuration>(V1File, DalamudSettings)!;
        Assert.True(config.Normalize());
        Assert.Equal(Configuration.CurrentVersion, config.Version);
        Assert.Equal("KEEP_THIS_TOKEN_abcdefghijklmnopqrstuvwxyz0", config.BearerToken);
        Assert.True(config.AllowAction);
        Assert.True(config.AllowChat);
        Assert.Equal(ActivityLogLevel.All, config.ActivityLogLevel);
        Assert.False(config.Normalize());

        var saved = JObject.Parse(JsonConvert.SerializeObject(config, DalamudSettings));
        Assert.Null(saved["EndpointUrl"]);
    }

    [Fact]
    public void DefaultsAreSafe()
    {
        var config = new Configuration();
        config.Normalize();
        Assert.True(config.Enabled);
        Assert.Equal("127.0.0.1", config.Host);
        Assert.Equal(41800, config.Port);
        Assert.True(config.RequireToken);
        Assert.Equal(43, config.BearerToken.Length);
        Assert.True(config.AllowRead);
        Assert.True(config.AllowUi);
        Assert.False(config.AllowAction);
        Assert.False(config.AllowChat);
        Assert.True(config.ConfirmActions);
    }

    [Fact]
    public void NormalizeClampsAndCleans()
    {
        var config = new Configuration
        {
            BearerToken = "t",
            Host = "  ",
            Port = 0,
            CallTimeoutSeconds = 1,
            ConfirmTimeoutSeconds = 9999,
            ChatBufferSize = 1,
            AgentBoardExpiryMinutes = -5,
            ActivityLogLevel = (ActivityLogLevel)42,
            AllowedOrigins = [" http://a ", "", "http://A"],
            DisabledCategories = null!,
        };
        Assert.True(config.Normalize());
        Assert.Equal("t", config.BearerToken);
        Assert.Equal("127.0.0.1", config.Host);
        Assert.Equal(1, config.Port);
        Assert.Equal(5, config.CallTimeoutSeconds);
        Assert.Equal(300, config.ConfirmTimeoutSeconds);
        Assert.Equal(50, config.ChatBufferSize);
        Assert.Equal(0, config.AgentBoardExpiryMinutes);
        Assert.Equal(ActivityLogLevel.Failures, config.ActivityLogLevel);
        Assert.Equal(["http://a"], config.AllowedOrigins);
        Assert.Empty(config.DisabledCategories);
    }

    [Fact]
    public void RecoverKeepsTokenFromDamagedFiles()
    {
        // Valid JSON with a bad member and an unknown $type.
        var bad = V1File.Replace("\"Port\":41800", "\"Port\":\"oops\"").Replace("XivMcp.Plugin.Configuration, XivMcp", "Nope, Nope");
        var recovered = Configuration.Recover(bad);
        Assert.Equal("KEEP_THIS_TOKEN_abcdefghijklmnopqrstuvwxyz0", recovered.BearerToken);
        Assert.True(recovered.AllowChat);

        // Truncated file.
        var truncated = Configuration.Recover(V1File[..(V1File.IndexOf("RequireToken", StringComparison.Ordinal))]);
        Assert.Equal("KEEP_THIS_TOKEN_abcdefghijklmnopqrstuvwxyz0", truncated.BearerToken);
        Assert.False(truncated.AllowChat);
    }
}
