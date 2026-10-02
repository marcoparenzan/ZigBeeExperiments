using System.Buffers.Binary;
using SonoffZigbee.Ezsp;

namespace SonoffZigbee;

/// <summary>
/// Zigbee coordinator running on a Sonoff ZBDongle-E (Silicon Labs EFR32MG21, EmberZNet NCP firmware).
/// The Zigbee stack proper (radio, network, security) lives in the dongle; this class drives it
/// over EZSP/ASH. It is written for EZSP v8 (EmberZNet 6.x), the firmware the dongle ships with.
/// </summary>
public sealed class ZigbeeCoordinator : IDisposable
{
    // EZSP command identifiers (version 8). Callbacks share the same number space.
    private const ushort F_AddEndpoint = 0x0002, F_SetConcentrator = 0x0010, F_NetworkInit = 0x0017,
        F_StackStatusHandler = 0x0019, F_FormNetwork = 0x001E, F_LeaveNetwork = 0x0020,
        F_PermitJoining = 0x0022, F_TrustCenterJoinHandler = 0x0024, F_GetEui64 = 0x0026,
        F_GetNetworkParameters = 0x0028, F_SendUnicast = 0x0034, F_SendBroadcast = 0x0036,
        F_MessageSentHandler = 0x003F, F_IncomingMessageHandler = 0x0045, F_SetConfigurationValue = 0x0053,
        F_SetPolicy = 0x0055, F_SetInitialSecurityState = 0x0068;

    // Stack status values (stackStatusHandler callback) and the "no network" code.
    private const byte NetworkUp = 0x90, NetworkDown = 0x91, NotJoined = 0x93;

    /// <summary>Coordinator endpoint: messages are sent from it and replies arrive on it.</summary>
    public const byte CoordinatorEndpoint = 1;
    private const ushort ProfileHa = 0x0104; // Home Automation profile

    /// <summary>"ZigBeeAlliance09" link key: the one Zigbee 3.0 devices use to join.</summary>
    private static readonly byte[] WellKnownLinkKey = "ZigBeeAlliance09"u8.ToArray();

    /// <summary>Wait for a specific callback: frame id, filter on the parameters and the task to complete.</summary>
    private sealed record Waiter(ushort Frame, Func<byte[], bool> Filter, TaskCompletionSource<byte[]> Tcs);

    private readonly EzspClient _ezsp;
    private readonly ZigbeeNetworkConfig _config;
    private readonly List<Waiter> _waiters = new();
    private byte _msgTag, _zdoSeq; // counters: tag of sent messages and sequence of ZDO requests

    public event Action<string>? Log;
    /// <summary>A device joined or rejoined and announced itself (ZDO Device_annce).</summary>
    public event Action<DeviceAnnounce>? DeviceAnnounced;
    /// <summary>The trust center saw a join/rejoin/leave (arrives before the announce). Arguments: short address, IEEE, status.</summary>
    public event Action<ushort, ulong, byte>? DeviceJoined;
    /// <summary>An application (non-ZDO) message arrived.</summary>
    public event Action<IncomingMessage>? MessageReceived;

    /// <summary>IEEE address of the dongle, known after StartAsync.</summary>
    public ulong IeeeAddress { get; private set; }
    public string FirmwareVersion { get; private set; } = "";

    public ZigbeeCoordinator(string portName, ZigbeeNetworkConfig config, int baudRate = 115200)
    {
        _config = config;
        _ezsp = new EzspClient(portName, baudRate);
        _ezsp.Log += m => Log?.Invoke(m);
        _ezsp.Callback += OnCallback;
    }

    // ------------------------------------------------------------------ startup

    /// <summary>Connects to the dongle, configures it and brings the network up (forming it or restoring it).</summary>
    public async Task StartAsync()
    {
        await _ezsp.ConnectAsync();
        if (_ezsp.ProtocolVersion != 8)
            Log?.Invoke($"WARNING: EZSP v{_ezsp.ProtocolVersion} - this library was written for v8, expect problems.");
        FirmwareVersion = $"EZSP v{_ezsp.ProtocolVersion}, EmberZNet {_ezsp.StackVersion >> 12}.{(_ezsp.StackVersion >> 8) & 0xF}.{(_ezsp.StackVersion >> 4) & 0xF}";
        Log?.Invoke($"Firmware: {FirmwareVersion}");

        IeeeAddress = BinaryPrimitives.ReadUInt64LittleEndian(await _ezsp.CommandAsync(F_GetEui64, []));

        // Stack configuration must be applied before the network is started.
        // If a value is rejected we log it and carry on.
        foreach (var (id, value) in new (byte, ushort)[]
        {
            (0x0C, 2),     // STACK_PROFILE: Zigbee PRO
            (0x0D, 5),     // SECURITY_LEVEL
            (0x2D, 1),     // SUPPORTED_NETWORKS
            (0x2A, 0x01),  // APPLICATION_ZDO_FLAGS: the host receives ZDO requests (for the device announce)
            (0x05, 16),    // ADDRESS_TABLE_SIZE
            (0x11, 32),    // MAX_END_DEVICE_CHILDREN
            (0x12, 7680),  // INDIRECT_TRANSMISSION_TIMEOUT (ms): how long the dongle keeps messages for sleeping devices
        })
            await SetConfigAsync(id, value);

        // Security and message-handling policies (pairs: policy id, decision).
        foreach (var (policy, decision) in new (byte, byte)[]
        {
            (0x00, 0x01),  // TRUST_CENTER_POLICY: allow joins
            (0x02, 0x20),  // UNICAST_REPLIES_POLICY: the stack handles replies
            (0x03, 0x30),  // POLL_HANDLER_POLICY: ignore polls
            (0x04, 0x40),  // MESSAGE_CONTENTS_IN_CALLBACK_POLICY: callbacks carry only the tag, not the contents
            (0x05, 0x51),  // TC_KEY_REQUEST_POLICY: allow key requests, send the current key
            (0x06, 0x50),  // APP_KEY_REQUEST_POLICY: deny
        })
        {
            var s = (await _ezsp.CommandAsync(F_SetPolicy, [policy, decision]))[0];
            if (s != 0) Log?.Invoke($"setPolicy 0x{policy:X2}=0x{decision:X2} -> status 0x{s:X2}");
        }

        // Endpoint 1: HA profile, "configuration tool" device type, no clusters declared.
        await Ok(F_AddEndpoint, [CoordinatorEndpoint, .. U16(ProfileHa), .. U16(0x0005), 0, 0, 0], allow: 0x00);

        // Register the wait for "network up" BEFORE starting, otherwise the callback could slip past us.
        var up = Expect(F_StackStatusHandler, d => d[0] == NetworkUp);
        var init = (await _ezsp.CommandAsync(F_NetworkInit, [0, 0]))[0];
        bool needForm = init == NotJoined;
        if (init == 0)
        {
            // The dongle already has a stored network: reuse it only if it matches our configuration.
            await up.WaitAsync(TimeSpan.FromSeconds(10));
            if (await MatchesConfigAsync()) Log?.Invoke("Restored existing network.");
            else
            {
                Log?.Invoke("Stored network differs from config: leaving it.");
                var down = Expect(F_StackStatusHandler, d => d[0] == NetworkDown);
                await Ok(F_LeaveNetwork, []);
                await down.WaitAsync(TimeSpan.FromSeconds(10));
                needForm = true;
            }
        }
        else if (!needForm) throw new InvalidOperationException($"networkInit failed: 0x{init:X2}");

        if (needForm)
        {
            Log?.Invoke("Forming a new network...");
            up = Expect(F_StackStatusHandler, d => d[0] == NetworkUp);
            await Ok(F_SetInitialSecurityState, SecurityState());
            await Ok(F_FormNetwork, NetworkParameters());
            await up.WaitAsync(TimeSpan.FromSeconds(60)); // forming includes a channel scan: it can take several seconds
        }

        // Many-to-one routing: routers learn the way to us. If it is rejected it is not a problem.
        var sc = (await _ezsp.CommandAsync(F_SetConcentrator, [1, 0xF9, 0xFF, 10, 0, 90, 0, 3, 1, 0]))[0];
        if (sc != 0) Log?.Invoke($"setConcentrator -> 0x{sc:X2}");

        Log?.Invoke($"Coordinator up. IEEE {IeeeAddress:X16}, channel {_config.Channel}, PAN 0x{_config.PanId:X4}");
    }

    private async Task SetConfigAsync(byte id, ushort value)
    {
        var s = (await _ezsp.CommandAsync(F_SetConfigurationValue, [id, .. U16(value)]))[0];
        if (s != 0) Log?.Invoke($"setConfigurationValue 0x{id:X2}={value} -> status 0x{s:X2}");
    }

    /// <summary>Compares the network stored in the dongle (extended PAN id, PAN id, channel) with our configuration.</summary>
    private async Task<bool> MatchesConfigAsync()
    {
        var r = await _ezsp.CommandAsync(F_GetNetworkParameters, []);
        if (r[0] != 0) return false;
        var p = r.AsSpan(2); // skip status and node type: the network parameters start after them
        return p[..8].SequenceEqual(_config.ExtendedPanId)
            && BinaryPrimitives.ReadUInt16LittleEndian(p[8..]) == _config.PanId
            && p[11] == _config.Channel;
    }

    /// <summary>Parameters for formNetwork, in the order EZSP requires.</summary>
    private byte[] NetworkParameters() =>
    [
        .. _config.ExtendedPanId,
        .. U16(_config.PanId),
        8,                          // transmit power (dBm)
        (byte)_config.Channel,
        0,                          // join method: MAC association
        0, 0,                       // network manager id
        0,                          // network update id
        .. BitConverter.GetBytes(1u << _config.Channel), // channel mask: a single bit set
    ];

    /// <summary>Initial security state: the keys that will protect the network.</summary>
    private byte[] SecurityState() =>
    [
        .. U16(0x0304),             // TRUST_CENTER_GLOBAL_LINK_KEY | HAVE_PRECONFIGURED_KEY | HAVE_NETWORK_KEY
        .. WellKnownLinkKey,        // ZigBeeAlliance09: the link key Zigbee 3.0 devices use to join
        .. _config.NetworkKey,      // the network key we chose
        0,                          // network key sequence number
        0, 0, 0, 0, 0, 0, 0, 0,     // trust center EUI64 (unused)
    ];

    // ------------------------------------------------------------------ public API

    /// <summary>Opens the network to new devices for the given time (0 closes it, maximum 254 seconds).</summary>
    public async Task PermitJoinAsync(byte seconds)
    {
        await Ok(F_PermitJoining, [seconds]); // opens the coordinator itself
        // Also ask the routers in the network to open (ZDO Mgmt_Permit_Joining_req, broadcast to routers).
        byte[] zdo = [++_zdoSeq, seconds, 1];
        byte tag = ++_msgTag;
        var r = await _ezsp.CommandAsync(F_SendBroadcast, [.. U16(0xFFFC), .. Aps(0, 0x0036, 0, 0, 0), 30, tag, (byte)zdo.Length, .. zdo]);
        if (r[0] != 0) Log?.Invoke($"permit-join broadcast -> 0x{r[0]:X2}");
    }

    /// <summary>Asks a device for the list of its active endpoints (ZDO Active_EP_req).</summary>
    public async Task<byte[]> GetActiveEndpointsAsync(ushort nwk)
    {
        var d = await ZdoRequestAsync(nwk, 0x0005, U16(nwk));
        if (d[1] != 0) throw new InvalidOperationException($"Active EP request failed: 0x{d[1]:X2}");
        return d[5..(5 + d[4])];
    }

    /// <summary>Asks a device for the descriptor of one of its endpoints (profile, type and supported clusters).</summary>
    public async Task<SimpleDescriptor> GetSimpleDescriptorAsync(ushort nwk, byte endpoint)
    {
        var d = await ZdoRequestAsync(nwk, 0x0004, [.. U16(nwk), endpoint]);
        if (d[1] != 0) throw new InvalidOperationException($"Simple descriptor request failed: 0x{d[1]:X2}");
        int i = 11;
        int nIn = d[i++];
        var ins = new ushort[nIn];
        for (int k = 0; k < nIn; k++, i += 2) ins[k] = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(i));
        int nOut = d[i++];
        var outs = new ushort[nOut];
        for (int k = 0; k < nOut; k++, i += 2) outs[k] = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(i));
        return new SimpleDescriptor(nwk, endpoint, BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(6)),
            BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(8)), ins, outs);
    }

    /// <summary>Sends an APS message to a device endpoint and waits for the stack's delivery result.</summary>
    public Task SendAsync(ushort nwk, byte dstEndpoint, ushort cluster, byte[] payload, ushort profile = ProfileHa) =>
        UnicastAsync(nwk, profile, cluster, CoordinatorEndpoint, dstEndpoint, payload, 0x1140); // options: APS retry + route and address discovery

    /// <summary>Sends a cluster-specific ZCL command (for example On/Off/Toggle).</summary>
    public Task SendClusterCommandAsync(ushort nwk, byte endpoint, ushort cluster, byte command, byte[]? payload = null) =>
        SendAsync(nwk, endpoint, cluster, Zcl.Build(true, command, payload ?? []));

    public Task OnAsync(ushort nwk, byte ep) => SendClusterCommandAsync(nwk, ep, ZclCluster.OnOff, 0x01);
    public Task OffAsync(ushort nwk, byte ep) => SendClusterCommandAsync(nwk, ep, ZclCluster.OnOff, 0x00);
    public Task ToggleAsync(ushort nwk, byte ep) => SendClusterCommandAsync(nwk, ep, ZclCluster.OnOff, 0x02);

    /// <summary>Sends a ZCL Read Attributes request; the answer arrives as a MessageReceived event.</summary>
    public Task ReadAttributeAsync(ushort nwk, byte ep, ushort cluster, ushort attribute) =>
        SendAsync(nwk, ep, cluster, Zcl.Build(false, 0x00, U16(attribute)));

    // ------------------------------------------------------------------ messaging internals

    /// <summary>Sends a unicast and waits for the messageSentHandler callback with the delivery result.</summary>
    private async Task UnicastAsync(ushort nwk, ushort profile, ushort cluster, byte srcEp, byte dstEp, byte[] payload, ushort options)
    {
        byte tag = ++_msgTag; // the tag ties the request to the result callback
        var sent = Expect(F_MessageSentHandler, d => d.Length > 15 && d[14] == tag);
        var r = await _ezsp.CommandAsync(F_SendUnicast,
            [0, .. U16(nwk), .. Aps(profile, cluster, srcEp, dstEp, options), tag, (byte)payload.Length, .. payload]);
        if (r[0] != 0) throw new InvalidOperationException($"sendUnicast failed: EmberStatus 0x{r[0]:X2}");
        var d = await sent.WaitAsync(TimeSpan.FromSeconds(20));
        if (d[15] != 0) throw new InvalidOperationException($"Delivery failed: EmberStatus 0x{d[15]:X2}");
    }

    /// <summary>Sends a ZDO request and returns the response payload (which starts with the ZDO sequence byte).</summary>
    private async Task<byte[]> ZdoRequestAsync(ushort nwk, ushort cluster, byte[] body)
    {
        byte seq = ++_zdoSeq;
        ushort rspCluster = (ushort)(cluster | 0x8000); // the ZDO response uses the same cluster with the top bit set
        var rsp = Expect(F_IncomingMessageHandler, d => Incoming(d) is { Profile: 0 } m && m.Cluster == rspCluster && m.Sender == nwk && m.Data[0] == seq);
        await UnicastAsync(nwk, 0, cluster, 0, 0, [seq, .. body], 0x1100);
        var d = await rsp.WaitAsync(TimeSpan.FromSeconds(10));
        return Incoming(d)!.Data;
    }

    /// <summary>Incoming message, parsed from the incomingMessageHandler callback.</summary>
    private sealed record Rx(byte Type, ushort Profile, ushort Cluster, byte SrcEp, byte DstEp, byte Lqi, ushort Sender, byte[] Data);

    private static Rx? Incoming(byte[] d)
    {
        // Layout: type, APS frame (11 bytes), lqi, rssi, sender (2), binding index, address index, length, message.
        if (d.Length < 19) return null;
        int len = d[18];
        if (d.Length < 19 + len) return null;
        return new Rx(d[0], BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(1)), BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(3)),
            d[5], d[6], d[12], BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(14)), d[19..(19 + len)]);
    }

    /// <summary>Handles dongle callbacks: first it unblocks whoever is waiting for them, then it raises the public events.</summary>
    private void OnCallback(ushort id, byte[] p)
    {
        Log?.Invoke($"ASH-EZSP callback 0x{id:X4} [{Convert.ToHexString(p)}]");
        lock (_waiters)
        {
            foreach (var w in _waiters.ToArray())
                if (w.Frame == id && w.Filter(p)) { _waiters.Remove(w); w.Tcs.TrySetResult(p); }
        }

        switch (id)
        {
            case F_IncomingMessageHandler when Incoming(p) is { } m:
                if (m.Profile == 0)
                {
                    // ZDO Device_annce (cluster 0x0013): sequence, short address (2), IEEE (8), capabilities.
                    if (m.Cluster == 0x0013 && m.Data.Length >= 12)
                        DeviceAnnounced?.Invoke(new DeviceAnnounce(
                            BinaryPrimitives.ReadUInt16LittleEndian(m.Data.AsSpan(1)),
                            BinaryPrimitives.ReadUInt64LittleEndian(m.Data.AsSpan(3)), m.Data[11]));
                }
                else MessageReceived?.Invoke(new IncomingMessage(m.Sender, m.SrcEp, m.DstEp, m.Cluster, m.Lqi, m.Data));
                break;

            case F_TrustCenterJoinHandler when p.Length >= 11:
                // Short address (2), EUI64 (8), status (0 secured rejoin, 1 join, 2 left, 3 unsecured rejoin), decision, parent (2).
                DeviceJoined?.Invoke(BinaryPrimitives.ReadUInt16LittleEndian(p), BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(2)), p[10]);
                break;
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Registers a wait for a callback. Call it BEFORE sending the command that triggers it,
    /// otherwise the response could arrive before anyone is waiting for it.
    /// </summary>
    private Task<byte[]> Expect(ushort frame, Func<byte[], bool> filter)
    {
        var w = new Waiter(frame, filter, new(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_waiters) _waiters.Add(w);
        return w.Tcs.Task;
    }

    /// <summary>Sends a command and throws if the response status is not 0 (nor the allowed value <paramref name="allow"/>).</summary>
    private async Task Ok(ushort frame, byte[] parameters, byte allow = 0)
    {
        var r = await _ezsp.CommandAsync(frame, parameters);
        if (r.Length > 0 && r[0] != 0 && r[0] != allow)
            throw new InvalidOperationException($"EZSP 0x{frame:X4} failed: EmberStatus 0x{r[0]:X2}");
    }

    /// <summary>APS frame (11 bytes): profile, cluster, source and destination endpoint, options, group, sequence.</summary>
    private static byte[] Aps(ushort profile, ushort cluster, byte srcEp, byte dstEp, ushort options) =>
        [.. U16(profile), .. U16(cluster), srcEp, dstEp, .. U16(options), 0, 0, 0];

    /// <summary>16-bit integer in little endian (as EZSP requires).</summary>
    private static byte[] U16(ushort v) => [(byte)v, (byte)(v >> 8)];

    public void Dispose() => _ezsp.Dispose();
}
