using SonoffZigbee;

// Test console app for the Zigbee coordinator.
// Usage: SonoffZigbee.Demo [COMx] [--verbose]

// Serial port of the dongle (default COM5) and verbose mode (also shows ASH frames and raw callbacks).
string port = args.FirstOrDefault(a => a.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) ?? "COM5";
bool verbose = args.Contains("--verbose");

// The network configuration (PAN id, key, channel) lives next to the executable and is reused on every start.
var config = ZigbeeNetworkConfig.LoadOrCreate(Path.Combine(AppContext.BaseDirectory, "network.json"));
using var zb = new ZigbeeCoordinator(port, config);

// State kept by the demo, indexed by the device's short address.
var devices = new Dictionary<ushort, DeviceAnnounce>();
var models = new Dictionary<ushort, string>();          // model read from the Basic cluster (used to describe messages)
var interviewed = new HashSet<ushort>();                // devices whose endpoints we have read
var interviewing = new Dictionary<ushort, int>();       // interview attempts per device (see TryInterviewAsync)

// Show log messages; low-level details (ASH) only with --verbose.
zb.Log += m => { if (verbose || !m.StartsWith("ASH")) Console.WriteLine($"[log] {m}"); };

// The trust center sees the join before the device announces itself.
zb.DeviceJoined += (nwk, ieee, status) =>
    Console.WriteLine($"[tc] 0x{nwk:X4} {ieee:X16} status {status} (0=secured rejoin, 1=join, 2=left, 3=unsecured rejoin)");

// "Interview": asks the device for its endpoints and clusters.
// Battery devices sleep, so a request sent right after the join often fails. We retry on the
// next message, when we know the device is awake, at most 3 times.
// The counter in 'interviewing' means: n >= 0 = attempts made; negative value = interview in progress
// (-n-1 is the number of the attempt in progress), so two messages in quick succession do not start two interviews.
async Task TryInterviewAsync(ushort nwk)
{
    lock (interviewing)
    {
        interviewing.TryGetValue(nwk, out var n);
        if (interviewed.Contains(nwk) || n >= 3 || n < 0) return; // already done, attempts exhausted or already in progress
        interviewing[nwk] = -n - 1; // mark "in progress"
    }
    int attempt;
    lock (interviewing) attempt = -interviewing[nwk];
    try
    {
        foreach (var ep in await zb.GetActiveEndpointsAsync(nwk))
        {
            var sd = await zb.GetSimpleDescriptorAsync(nwk, ep);
            Console.WriteLine($"[join] 0x{nwk:X4} ep {ep} profile 0x{sd.ProfileId:X4} device 0x{sd.DeviceId:X4} " +
                              $"in:[{string.Join(",", sd.InClusters.Select(c => $"0x{c:X4}"))}] " +
                              $"out:[{string.Join(",", sd.OutClusters.Select(c => $"0x{c:X4}"))}]");
        }
        lock (interviewing) interviewed.Add(nwk);
    }
    catch (Exception)
    {
        Console.WriteLine($"[join] 0x{nwk:X4}: dispositivo a batteria, non risponde ora (tentativo {attempt}/3), riprovo al prossimo messaggio");
    }
    finally { lock (interviewing) interviewing[nwk] = attempt; } // back to "attempts made"
}

// The device announced itself on the network.
zb.DeviceAnnounced += a =>
{
    devices[a.NetworkAddress] = a;
    Console.WriteLine($"[join] device 0x{a.NetworkAddress:X4} IEEE {a.IeeeString}");
};

// Application message received (a sensor report, a reply to a read...).
zb.MessageReceived += m =>
{
    // If the message carries the device model (Basic, attribute 0x0005) we remember it.
    if (m.Zcl is { } z)
        foreach (var a in Zcl.ParseAttributes(z))
            if (m.ClusterId == ZclCluster.Basic && a.Id == 0x0005 && a.Value is string s) models[m.SourceAddress] = s;

    models.TryGetValue(m.SourceAddress, out var model);
    Console.WriteLine($"[msg] 0x{m.SourceAddress:X4} {Describe.Message(model, m)}  (cluster 0x{m.ClusterId:X4}, lqi {m.LinkQuality})");
    _ = TryInterviewAsync(m.SourceAddress); // the device just transmitted: it is awake, this is the right moment
};

Console.WriteLine($"Opening {port}...");
await zb.StartAsync();

Console.WriteLine("""

Commands:
  join [seconds]                     open the network for pairing (default 60)
  list                               devices seen since start
  on|off|toggle <addr> [ep]          on/off cluster (addr is hex short address, ep default 1)
  read <addr> <ep> <cluster> <attr>  read attribute (hex), e.g. read 1234 1 0402 0000
  quit
""");

// Keyboard command loop.
while (true)
{
    Console.Write("> ");
    var line = Console.ReadLine();
    if (line is null) { await Task.Delay(Timeout.Infinite); break; } // input closed (for example when piped): keep listening
    var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (p.Length == 0) continue;
    try
    {
        ushort Hex(int i) => Convert.ToUInt16(p[i], 16); // reads argument i as a hexadecimal number
        switch (p[0].ToLowerInvariant())
        {
            case "join":
                await zb.PermitJoinAsync(p.Length > 1 ? byte.Parse(p[1]) : (byte)60);
                Console.WriteLine("Permit join open. Put your device in pairing mode.");
                break;
            case "list":
                foreach (var d in devices.Values) Console.WriteLine($"0x{d.NetworkAddress:X4}  {d.IeeeString}");
                break;
            case "on": await zb.OnAsync(Hex(1), p.Length > 2 ? byte.Parse(p[2]) : (byte)1); break;
            case "off": await zb.OffAsync(Hex(1), p.Length > 2 ? byte.Parse(p[2]) : (byte)1); break;
            case "toggle": await zb.ToggleAsync(Hex(1), p.Length > 2 ? byte.Parse(p[2]) : (byte)1); break;
            case "read": await zb.ReadAttributeAsync(Hex(1), byte.Parse(p[2]), Hex(3), Hex(4)); break;
            case "quit": case "exit": return;
            default: Console.WriteLine("Unknown command."); break;
        }
    }
    catch (Exception ex) { Console.WriteLine($"Error: {ex.Message}"); }
}
