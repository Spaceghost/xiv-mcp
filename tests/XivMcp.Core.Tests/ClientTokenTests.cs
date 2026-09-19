using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using XivMcp.Core.Tests.Infrastructure;

namespace XivMcp.Core.Tests;

[McpProvider("whoami")]
public sealed class WhoAmIProvider
{
    [McpTool("whoami", Description = "Reports the caller identity.", GameThread = false, RequiresLogin = false)]
    public string WhoAmI(ToolContext ctx) => $"{ctx.ClientName}|{ctx.AuthenticatedClient ?? "-"}";
}

public class ClientTokenTests
{
    private const string CiToken = "ci-secret-token-value-0123456789";

    private static ClientToken Ci(string name = "ghostty-ci") => new(name, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CiToken))));

    private static async Task<(HttpStatusCode Status, JsonObject? Response)> CallAsWhoAmI(TestServer s, string? bearer)
    {
        var request = s.ModernPost("tools/call", new JsonObject { ["name"] = "whoami" });
        request.Headers.Authorization = bearer is null ? null : new AuthenticationHeaderValue("Bearer", bearer);
        using var response = await s.Http.SendAsync(request);
        if (response.StatusCode != HttpStatusCode.OK)
            return (response.StatusCode, null);
        return (response.StatusCode, (await TestServer.ReadRpcAsync(response)).Response);
    }

    private static string Text(JsonObject response) => response["result"]!["content"]![0]!["text"]!.GetValue<string>();

    [Fact]
    public async Task ClientTokenAuthorizesAndBindsTheIdentityTheClientCannotChoose()
    {
        var s = await TestServer.StartAsync(o => o.ClientTokens = [Ci()]);
        await using var server = s;
        s.Server.RegisterProvider(new WhoAmIProvider());

        var (ciStatus, ci) = await CallAsWhoAmI(s, CiToken);
        Assert.Equal(HttpStatusCode.OK, ciStatus);
        Assert.Equal("modern-test 2.0|ghostty-ci", Text(ci!));

        var (mainStatus, main) = await CallAsWhoAmI(s, TestServer.Token);
        Assert.Equal(HttpStatusCode.OK, mainStatus);
        Assert.Equal("modern-test 2.0|-", Text(main!)); // same self-reported name, no token identity

        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsWhoAmI(s, "ghostty-ci")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsWhoAmI(s, CiToken + "x")).Status);
    }

    [Fact]
    public async Task RevokingAClientTokenTakesEffectOnTheNextRequest()
    {
        var s = await TestServer.StartAsync(o => o.ClientTokens = [Ci(), new ClientToken("broken", "not-hex")]);
        await using var server = s;
        s.Server.RegisterProvider(new WhoAmIProvider());
        Assert.Equal(HttpStatusCode.OK, (await CallAsWhoAmI(s, CiToken)).Status);

        s.Server.Options.ClientTokens = [];
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsWhoAmI(s, CiToken)).Status);
        Assert.Equal(HttpStatusCode.OK, (await CallAsWhoAmI(s, TestServer.Token)).Status);
    }

    [Fact]
    public async Task ApproverSeesTheAuthenticatedClient()
    {
        var s = await TestServer.StartAsync(o => o.ClientTokens = [Ci()]);
        await using var server = s;
        s.Server.RegisterProvider(new ApprovalProvider());
        s.Host.Permissions[ToolPermission.Action] = true;
        var approver = new SessionAwareApprover();
        s.Server.Approver = approver;

        foreach (var bearer in new[] { CiToken, TestServer.Token })
        {
            var request = s.ModernPost("tools/call", new JsonObject { ["name"] = "approve_action", ["arguments"] = new JsonObject { ["text"] = "x" } });
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var response = await s.Http.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var requests = approver.Requests.ToArray();
        Assert.Equal("ghostty-ci", requests[0].AuthenticatedClient);
        Assert.Null(requests[1].AuthenticatedClient);
    }
}
