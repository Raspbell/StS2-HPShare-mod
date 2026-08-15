using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;

namespace HPShare;

/// <summary>Reliable host-to-client synchronization message for HP Share gameplay settings.</summary>
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

    /// <inheritdoc />
    public void Serialize(PacketWriter writer)
        => writer.WriteUShort((ushort)Math.Clamp(EnemyAttackCoefficientHundredths, 50, 300), 9);

    /// <inheritdoc />
    public void Deserialize(PacketReader reader)
        => EnemyAttackCoefficientHundredths = reader.ReadUShort(9);
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
            ModConfig.RestoreLocalCoefficient();
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
        };

    private static void OnConfigMessage(HPShareConfigMessage message, ulong senderId)
    {
        if (_registeredService is NetClientGameService client && senderId != client.HostNetId)
            return;
        ModConfig.ApplyHostCoefficient(message.EnemyAttackCoefficientHundredths / 100m);
        Console.WriteLine($"[HPShare] Applied host enemy attack coefficient {ModConfig.EnemyAttackCoefficient:0.00}");
    }
}
