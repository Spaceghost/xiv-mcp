using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Providers.Actions;
using XivMcp.Plugin.Tests.Infrastructure;

namespace XivMcp.Plugin.Tests;

/// <summary>target_party_member: who a name or slot number means, and the refusals.</summary>
public class PartyTargetingTests
{
    private static readonly PartySlot[] Party =
    [
        new(1, "Warrior Light", "Balmung", 0x1001),
        new(2, "Alphinaud Leveilleur", "Mateus", 0x1002),
        new(3, "Alisaie Leveilleur", "Mateus", 0x1003),
        new(4, "Warrior Light", "Zalera", 0x1004),
    ];

    private static McpToolException Rejects(Action call) => Assert.Throws<McpToolException>(call);

    [Theory]
    [InlineData("alphinaud leveilleur", 2)]
    [InlineData("  ALISAIE LEVEILLEUR ", 3)]
    [InlineData("Alisaie", 3)]
    [InlineData("Warrior Light@Zalera", 4)]
    [InlineData("warrior light @ balmung", 1)]
    [InlineData("Alphinaud@mateus", 2)]
    public void ResolvesByName(string name, int expectedSlot) => Assert.Equal(expectedSlot, PartyTargeting.Resolve(Party, name, null).Slot);

    [Theory]
    [InlineData("Warrior Light")]
    [InlineData("Leveilleur")]
    [InlineData("Leveilleur@Mateus")]
    public void AmbiguousNamesAreNotGuessed(string name)
    {
        var ex = Rejects(() => PartyTargeting.Resolve(Party, name, null));
        Assert.Equal(McpErrorCodes.InvalidArguments, ex.Code);
        Assert.Contains("matches 2 party members", ex.Message, StringComparison.Ordinal);
        Assert.Contains("@", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Estinien")]
    [InlineData("Alisaie@Balmung")]
    public void UnknownNamesAreNotFound(string name) => Assert.Equal(McpErrorCodes.NotFound, Rejects(() => PartyTargeting.Resolve(Party, name, null)).Code);

    [Theory]
    [InlineData("@Balmung")]
    [InlineData("Warrior Light@")]
    [InlineData("a@b@c")]
    public void MalformedNames(string name) => Assert.Equal(McpErrorCodes.InvalidArguments, Rejects(() => PartyTargeting.Resolve(Party, name, null)).Code);

    [Fact]
    public void ResolvesBySlot()
    {
        Assert.Equal("Alisaie Leveilleur", PartyTargeting.Resolve(Party, null, 3).Name);
        Assert.Equal(McpErrorCodes.NotFound, Rejects(() => PartyTargeting.Resolve(Party, null, 5)).Code);
        Assert.Equal(McpErrorCodes.InvalidArguments, Rejects(() => PartyTargeting.Resolve(Party, null, 0)).Code);
        Assert.Equal(McpErrorCodes.InvalidArguments, Rejects(() => PartyTargeting.Resolve(Party, null, 9)).Code);
    }

    [Fact]
    public void ExactlyOneSelector()
    {
        Assert.Equal(McpErrorCodes.InvalidArguments, Rejects(() => PartyTargeting.Resolve(Party, null, null)).Code);
        Assert.Equal(McpErrorCodes.InvalidArguments, Rejects(() => PartyTargeting.Resolve(Party, "Alisaie", 3)).Code);
        Assert.Equal(McpErrorCodes.NotFound, Rejects(() => PartyTargeting.Resolve([], "Alisaie", null)).Code);
    }

    [Fact]
    public void ParseName()
    {
        Assert.Equal(("Warrior Light", "Balmung"), PartyTargeting.ParseName(" Warrior Light@Balmung "));
        Assert.Equal(("Warrior Light", null), PartyTargeting.ParseName("Warrior Light"));
    }

    [Fact]
    public void ProviderTargetsTheNamedMemberAndNobodyElse()
    {
        IGameObject? targeted = null;
        var nearby = FakeProxy.Create<IGameObject>(new()
        {
            ["get_IsTargetable"] = _ => true,
            ["get_GameObjectId"] = _ => 0x1002UL,
            ["get_Position"] = _ => new Vector3(1, 0, 0),
        });
        var objects = FakeProxy.Create<IObjectTable>(new()
        {
            ["SearchByEntityId"] = args => (uint)args![0]! == 0x1002 ? nearby : null,
        });
        var targets = FakeProxy.Create<ITargetManager>(new()
        {
            ["set_Target"] = args =>
            {
                targeted = (IGameObject?)args![0];
                return null;
            },
        });
        var provider = new PartyTargetProvider(FakeProxy.Create<IPartyList>(), targets, objects)
        {
            HudRows = () => [(0x1001u, "Warrior Light"), (0x1002u, "Alphinaud Leveilleur"), (0x1003u, "Alisaie Leveilleur")],
        };

        var result = provider.TargetPartyMember(name: "alphinaud");
        Assert.Same(nearby, targeted);
        Assert.Equal(2, result.Slot);
        Assert.Equal("4098", result.GameObjectId);
        Assert.Equal(3, result.Party.Count);

        targeted = null;
        var far = Rejects(() => provider.TargetPartyMember(slot: 3));
        Assert.Equal(McpErrorCodes.Unavailable, far.Code);
        Assert.Null(targeted);

        Assert.Equal(McpErrorCodes.InvalidArguments, Rejects(() => provider.TargetPartyMember()).Code);
        Assert.Equal(McpErrorCodes.NotFound, Rejects(() => provider.TargetPartyMember(slot: 4)).Code);
    }
}
