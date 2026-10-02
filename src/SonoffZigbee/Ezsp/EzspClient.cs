namespace SonoffZigbee.Ezsp;

/// <summary>
/// EZSP (EmberZNet Serial Protocol): the command layer on top of ASH. You send a command (identified
/// by a "frame id" with its parameters) and get a response; the dongle also sends spontaneous
/// notifications ("callbacks"), for example when a message arrives.
/// </summary>
public sealed class EzspClient : IDisposable
{
    private readonly AshPort _ash;
    private readonly object _gate = new();
    private readonly Dictionary<byte, TaskCompletionSource<byte[]>> _pending = new(); // commands awaiting a response, by sequence number
    private readonly SemaphoreSlim _cmdLock = new(1, 1); // one command at a time
    private byte _seq;
    private bool _extended; // true after negotiation: 5-byte header (EZSP v8 and later)

    /// <summary>Spontaneous notification from the dongle: (frame id, parameters). Called on the read thread: do not block.</summary>
    public event Action<ushort, byte[]>? Callback;
    public event Action<string>? Log;

    public byte ProtocolVersion { get; private set; }
    public byte StackType { get; private set; }
    public ushort StackVersion { get; private set; }

    public EzspClient(string portName, int baudRate = 115200)
    {
        _ash = new AshPort(portName, baudRate);
        _ash.Log += m => Log?.Invoke(m);
        _ash.DataReceived += OnData;
    }

    /// <summary>Opens the port, resets the dongle and negotiates the protocol version.</summary>
    public async Task ConnectAsync()
    {
        _ash.Open();
        await _ash.ResetAsync();

        // The first 'version' command must be sent in the "legacy" format (short header): the dongle
        // answers with its real version. From v8 on the extended format is used, so we repeat the
        // command to confirm the version with the right format.
        var r = await CommandAsync(0x0000, [4]);
        ProtocolVersion = r[0];
        if (ProtocolVersion >= 8)
        {
            _extended = true;
            r = await CommandAsync(0x0000, [ProtocolVersion]);
        }
        StackType = r[1];
        StackVersion = (ushort)(r[2] | (r[3] << 8));
    }

    /// <summary>Sends an EZSP command and returns the parameters of the response.</summary>
    public async Task<byte[]> CommandAsync(ushort frameId, byte[] parameters, TimeSpan? timeout = null)
    {
        await _cmdLock.WaitAsync();
        byte seq = 0;
        try
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) { seq = _seq++; _pending[seq] = tcs; } // the response will carry the same sequence number

            // Legacy header: [seq][control][id]. Extended header: [seq][control lo][control hi][id lo][id hi].
            byte[] frame = _extended
                ? [seq, 0x00, 0x01, (byte)frameId, (byte)(frameId >> 8), .. parameters]
                : [seq, 0x00, (byte)frameId, .. parameters];
            await _ash.SendAsync(frame);
            return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(5));
        }
        finally
        {
            lock (_gate) _pending.Remove(seq);
            _cmdLock.Release();
        }
    }

    /// <summary>Routes a received frame: response to one of our commands or spontaneous callback.</summary>
    private void OnData(byte[] d)
    {
        byte seq = d[0];
        byte fcLo = d[1]; // control byte: bit 7 = response, bits 4-3 = callback type
        ushort id;
        byte[] p;
        if (_extended) { id = (ushort)(d[3] | (d[4] << 8)); p = d[5..]; }
        else { id = d[2]; p = d[3..]; }

        // It is a callback if it is not a response, or if it is flagged as an asynchronous callback.
        bool isCallback = (fcLo & 0x80) == 0 || (fcLo & 0x18) == 0x10;
        if (!isCallback)
        {
            TaskCompletionSource<byte[]>? tcs;
            lock (_gate) _pending.TryGetValue(seq, out tcs);
            if (tcs is not null) { tcs.TrySetResult(p); return; }
        }
        try { Callback?.Invoke(id, p); } catch (Exception ex) { Log?.Invoke($"callback error: {ex}"); }
    }

    public void Dispose() => _ash.Dispose();
}
