using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;

namespace HPShare;

/// <summary>Reliable host-to-client synchronization message for Share Everything gameplay settings.</summary>
public sealed class HPShareConfigMessage : INetMessage, IPacketSerializable
{
    /// <inheritdoc />
    public bool ShouldBroadcast => false;
    /// <inheritdoc />
    public NetTransferMode Mode => NetTransferMode.Reliable;
    /// <inheritdoc />
    public LogLevel LogLevel => LogLevel.Debug;
    /// <inheritdoc />
    public bool ShouldBuffer => true;

    /// <summary>Enemy attack coefficient in hundredths.</summary>
    public int EnemyAttackCoefficientHundredths { get; set; } = 110;
    /// <summary>Whether player buffs and debuffs are copied to the whole party.</summary>
    public bool ShareBuffsAndDebuffs { get; set; }
    /// <summary>Whether all players use the same gold balance.</summary>
    public bool ShareGold { get; set; }
    /// <summary>Whether player and Osty HP are shared.</summary>
    public bool ShareHp { get; set; } = true;
    /// <summary>Whether player Block is shared.</summary>
    public bool ShareBlock { get; set; } = true;
    /// <summary>Whether permanent decks are shared.</summary>
    public bool ShareDeck { get; set; }
    /// <summary>Whether current and maximum energy are shared.</summary>
    public bool ShareEnergy { get; set; }
    /// <summary>Whether relic inventories are shared.</summary>
    public bool ShareRelics { get; set; }
    /// <summary>Whether potion slots and contents are shared.</summary>
    public bool SharePotions { get; set; }

    /// <inheritdoc />
    public void Serialize(PacketWriter writer)
    {
        writer.WriteUShort((ushort)Math.Clamp(EnemyAttackCoefficientHundredths, 50, 300), 9);
        writer.WriteUShort((ushort)(ShareBuffsAndDebuffs ? 1 : 0), 1);
        writer.WriteUShort((ushort)(ShareGold ? 1 : 0), 1);
        writer.WriteUShort((ushort)(ShareHp ? 1 : 0), 1);
        writer.WriteUShort((ushort)(ShareBlock ? 1 : 0), 1);
        writer.WriteUShort((ushort)(ShareDeck ? 1 : 0), 1);
        writer.WriteUShort((ushort)(ShareEnergy ? 1 : 0), 1);
        writer.WriteUShort((ushort)(ShareRelics ? 1 : 0), 1);
        writer.WriteUShort((ushort)(SharePotions ? 1 : 0), 1);
    }

    /// <inheritdoc />
    public void Deserialize(PacketReader reader)
    {
        EnemyAttackCoefficientHundredths = reader.ReadUShort(9);
        ShareBuffsAndDebuffs = reader.ReadUShort(1) != 0;
        ShareGold = reader.ReadUShort(1) != 0;
        ShareHp = reader.ReadUShort(1) != 0;
        ShareBlock = reader.ReadUShort(1) != 0;
        ShareDeck = reader.ReadUShort(1) != 0;
        ShareEnergy = reader.ReadUShort(1) != 0;
        ShareRelics = reader.ReadUShort(1) != 0;
        SharePotions = reader.ReadUShort(1) != 0;
    }
}

internal static class NetworkConfigSync
{
    private static INetGameService? _registeredService;
    private static readonly MessageHandlerDelegate<HPShareConfigMessage> Handler = OnConfigMessage;

    public static void Apply(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(RunManager), "InitializeRunLobby"),
            postfix: new HarmonyMethod(typeof(NetworkConfigSync), nameof(InitializeRunLobbyPostfix)));
        harmony.Patch(AccessTools.Method(typeof(RunLobby), "OnConnectedToClientAsHost"),
            postfix: new HarmonyMethod(typeof(NetworkConfigSync), nameof(ClientConnectedPostfix)));
    }

    private static void InitializeRunLobbyPostfix(INetGameService netService)
    {
        Register(netService);
        if (netService.Type is NetGameType.Host or NetGameType.Singleplayer)
        {
            ModConfig.RestoreLocalSettings();
            if (netService.Type == NetGameType.Host)
                netService.SendMessage(CreateMessage());
        }
    }

    private static void ClientConnectedPostfix(ulong playerId)
    {
        INetGameService? service = RunManager.Instance?.NetService;
        if (service?.Type == NetGameType.Host)
            service.SendMessage(CreateMessage(), playerId);
    }

    private static void Register(INetGameService service)
    {
        if (ReferenceEquals(_registeredService, service))
            return;
        if (_registeredService is not null)
        {
            try
            {
                _registeredService.UnregisterMessageHandler(Handler);
            }
            catch
            {
                // The previous networking service may already have been disposed.
            }
        }
        _registeredService = service;
        service.RegisterMessageHandler(Handler);
    }

    private static HPShareConfigMessage CreateMessage()
        => new()
        {
            EnemyAttackCoefficientHundredths = decimal.ToInt32(
                decimal.Round(ModConfig.EnemyAttackCoefficient * 100m, 0, MidpointRounding.AwayFromZero)),
            ShareBuffsAndDebuffs = ModConfig.ShareBuffsAndDebuffs,
            ShareGold = ModConfig.ShareGold,
            ShareHp = ModConfig.ShareHp,
            ShareBlock = ModConfig.ShareBlock,
            ShareDeck = ModConfig.ShareDeck,
            ShareEnergy = ModConfig.ShareEnergy,
            ShareRelics = ModConfig.ShareRelics,
            SharePotions = ModConfig.SharePotions,
        };

    private static void OnConfigMessage(HPShareConfigMessage message, ulong senderId)
    {
        if (_registeredService is NetClientGameService client && senderId != client.HostNetId)
            return;
        ModConfig.ApplyHostSettings(
            message.EnemyAttackCoefficientHundredths / 100m,
            message.ShareHp,
            message.ShareBlock,
            message.ShareBuffsAndDebuffs,
            message.ShareGold,
            message.ShareDeck,
            message.ShareEnergy,
            message.ShareRelics,
            message.SharePotions);
        Console.WriteLine(
            $"[ShareEverything] Applied host settings: enemy coefficient={ModConfig.EnemyAttackCoefficient:0.00}, " +
            $"HP={ModConfig.ShareHp}, Block={ModConfig.ShareBlock}, powers={ModConfig.ShareBuffsAndDebuffs}, " +
            $"gold={ModConfig.ShareGold}, deck={ModConfig.ShareDeck}, energy={ModConfig.ShareEnergy}, " +
            $"relics={ModConfig.ShareRelics}, potions={ModConfig.SharePotions}");
    }
}
