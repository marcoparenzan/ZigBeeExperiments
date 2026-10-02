using System.Security.Cryptography;
using System.Text.Json;

namespace SonoffZigbee;

/// <summary>
/// Zigbee network parameters. Saved to a file so the same network (same key) is reused on every
/// start: if they changed, devices that were already paired could no longer connect.
/// </summary>
public sealed class ZigbeeNetworkConfig
{
    /// <summary>16-bit network identifier (PAN id).</summary>
    public ushort PanId { get; set; } = 0x1A62;

    /// <summary>64-bit extended network identifier, randomly generated the first time.</summary>
    public byte[] ExtendedPanId { get; set; } = RandomNumberGenerator.GetBytes(8);

    /// <summary>128-bit network key that encrypts all traffic. Keep it secret.</summary>
    public byte[] NetworkKey { get; set; } = RandomNumberGenerator.GetBytes(16);

    /// <summary>Zigbee channel 11-26. 15, 20 and 25 stay clear of the most used Wi-Fi channels (1, 6, 11).</summary>
    public int Channel { get; set; } = 20;

    /// <summary>
    /// Loads the configuration from the file; if it does not exist, creates a new one (with random
    /// keys) and saves it right away.
    /// </summary>
    public static ZigbeeNetworkConfig LoadOrCreate(string path)
    {
        if (File.Exists(path))
            return JsonSerializer.Deserialize<ZigbeeNetworkConfig>(File.ReadAllText(path)) ?? new();
        var cfg = new ZigbeeNetworkConfig();
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        return cfg;
    }
}

/// <summary>Announcement of a device that joined (or rejoined) the network.</summary>
/// <param name="NetworkAddress">16-bit short address assigned by the network; it can change on a new join.</param>
/// <param name="IeeeAddress">64-bit IEEE address, fixed and unique per device.</param>
/// <param name="Capabilities">Device capability byte (router/end device, power source...).</param>
public sealed record DeviceAnnounce(ushort NetworkAddress, ulong IeeeAddress, byte Capabilities)
{
    /// <summary>IEEE address as 16 hex digits.</summary>
    public string IeeeString => $"{IeeeAddress:X16}";
}

/// <summary>Application message received from a device (not ZDO).</summary>
/// <param name="SourceAddress">Short address of the sender.</param>
/// <param name="SourceEndpoint">Endpoint of the sender.</param>
/// <param name="DestinationEndpoint">Coordinator endpoint the message arrived on.</param>
/// <param name="ClusterId">ZCL cluster of the message (for example 0x0006 = On/Off).</param>
/// <param name="LinkQuality">Link quality indicator (LQI, 0-255).</param>
/// <param name="Data">Raw payload, usually a ZCL frame.</param>
public sealed record IncomingMessage(
    ushort SourceAddress, byte SourceEndpoint, byte DestinationEndpoint, ushort ClusterId,
    byte LinkQuality, byte[] Data)
{
    /// <summary>The payload parsed as a ZCL frame, or null if it is not one.</summary>
    public ZclFrame? Zcl => SonoffZigbee.Zcl.Parse(Data);
}

/// <summary>Descriptor of a device endpoint: what it can do (input and output clusters).</summary>
public sealed record SimpleDescriptor(
    ushort NetworkAddress, byte Endpoint, ushort ProfileId, ushort DeviceId,
    ushort[] InClusters, ushort[] OutClusters);
