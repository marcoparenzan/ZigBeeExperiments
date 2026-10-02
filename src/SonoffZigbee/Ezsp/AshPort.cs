using System.IO.Ports;

namespace SonoffZigbee.Ezsp;

/// <summary>
/// ASH (Asynchronous Serial Host): the transport layer used by the dongle's EmberZNet firmware
/// (Sonoff ZBDongle-E, EFR32MG21). It makes delivery of EZSP frames reliable and ordered over a
/// UART, which on its own can lose or corrupt bytes: it adds a CRC, sequence numbers,
/// acknowledgements (ACK) and retransmissions.
/// </summary>
public sealed class AshPort : IDisposable
{
    // Reserved protocol bytes. Flag ends every frame; Escape precedes a "protected" byte.
    private const byte Flag = 0x7E, Escape = 0x7D, Cancel = 0x1A, Substitute = 0x18, Xon = 0x11, Xoff = 0x13;
    // Control bytes of the service frames: reset, reset acknowledgement, error.
    private const byte CtrlRst = 0xC0, CtrlRstAck = 0xC1, CtrlError = 0xC2;

    private readonly SerialPort _serial;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1); // only one data frame in flight at a time
    private readonly object _gate = new();                // protects state shared with the read thread

    private int _txNext;            // number (0-7) of the next data frame we will send
    private int _rxExpected;        // number of the next frame we expect from the dongle (also the ACK we answer with)
    private TaskCompletionSource<bool>? _ackWait;   // completed when the expected ACK arrives
    private int _ackExpected;                        // ACK number we are waiting for
    private TaskCompletionSource<byte>? _rstWait;   // completed when RSTACK arrives after a reset

    /// <summary>An EZSP frame received from the dongle, already validated and de-randomized.</summary>
    public event Action<byte[]>? DataReceived;
    public event Action<string>? Log;

    public AshPort(string portName, int baudRate = 115200)
    {
        _serial = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            // DTR and RTS drive the dongle's bootloader/reset pins: with DTR asserted the dongle stays
            // in the bootloader and does not answer as Zigbee. They must be left de-asserted.
            DtrEnable = false,
            RtsEnable = false,
            Handshake = Handshake.None, // no hardware flow control or XON/XOFF
        };
    }

    public void Open()
    {
        _serial.Open();
        _serial.DiscardInBuffer();
        _ = Task.Run(ReadLoopAsync);
    }

    /// <summary>Resets the dongle and the ASH sequence numbers. Returns the dongle's reset code.</summary>
    public async Task<byte> ResetAsync(TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) { _rstWait = tcs; _txNext = 0; _rxExpected = 0; }
        WriteRaw(Frame(CtrlRst, []), prefixCancel: true); // the Cancel byte discards any half-received frame
        return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(3));
    }

    /// <summary>Sends an EZSP frame and waits for the dongle to acknowledge it, retransmitting if needed (up to 4 attempts).</summary>
    public async Task SendAsync(byte[] ezsp)
    {
        await _sendLock.WaitAsync();
        try
        {
            int frm = _txNext;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                int ack;
                lock (_gate)
                {
                    _ackWait = tcs;
                    _ackExpected = (frm + 1) & 7; // the dongle will acknowledge with "next frame expected"
                    ack = _rxExpected;            // in our frame we acknowledge what we have received
                }
                // Control byte of a data frame: [frame number (3 bits)][retransmit (1 bit)][ACK (3 bits)].
                byte ctrl = (byte)((frm << 4) | (attempt > 0 ? 0x08 : 0) | ack);
                WriteRaw(Frame(ctrl, Randomize(ezsp)));
                try
                {
                    await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(1500));
                    _txNext = (frm + 1) & 7;
                    return;
                }
                catch (TimeoutException) { Log?.Invoke("ASH: ack timeout, retransmitting"); }
            }
            throw new TimeoutException("ASH: NCP did not acknowledge frame");
        }
        finally
        {
            lock (_gate) _ackWait = null;
            _sendLock.Release();
        }
    }

    // ------------------------------------------------------------ receiving

    /// <summary>Reads bytes from the serial port and rebuilds the frames between one 0x7E delimiter and the next.</summary>
    private async Task ReadLoopAsync()
    {
        var buf = new byte[512];
        var frame = new List<byte>(160);
        bool escaped = false, discard = false;
        var stream = _serial.BaseStream;
        while (!_cts.IsCancellationRequested)
        {
            int n;
            try { n = await stream.ReadAsync(buf, _cts.Token); }
            catch { break; } // port closed or cancelled: exit
            for (int i = 0; i < n; i++)
            {
                byte b = buf[i];
                switch (b)
                {
                    case Flag:                      // end of frame: process it
                        if (frame.Count > 0 && !discard) Process(frame.ToArray());
                        frame.Clear(); escaped = false; discard = false;
                        break;
                    case Cancel: frame.Clear(); escaped = false; discard = false; break; // drop the current frame
                    case Substitute: frame.Clear(); escaped = false; discard = true; break; // line error: drop until the next Flag
                    case Xon or Xoff: break;        // software flow control: ignored
                    case Escape: escaped = true; break; // the next byte must be un-escaped
                    default:
                        frame.Add(escaped ? (byte)(b ^ 0x20) : b);
                        escaped = false;
                        break;
                }
            }
        }
    }

    /// <summary>Processes a complete frame (already without delimiters and escapes): [control][data...][CRC hi][CRC lo].</summary>
    private void Process(byte[] f)
    {
        if (f.Length < 3 || Crc(f.AsSpan(0, f.Length - 2)) != (ushort)((f[^2] << 8) | f[^1]))
        {
            Log?.Invoke("ASH: bad CRC/short frame");
            return;
        }
        byte ctrl = f[0];

        if ((ctrl & 0x80) == 0) // DATA frame (top bit 0)
        {
            int frm = (ctrl >> 4) & 7, ack = ctrl & 7;
            OnAck(ack); // a data frame also carries the acknowledgement of what we sent
            bool deliver = false;
            lock (_gate)
            {
                if (frm == _rxExpected) { _rxExpected = (_rxExpected + 1) & 7; deliver = true; }
            }
            if (deliver)
            {
                SendAck();
                DataReceived?.Invoke(Randomize(f[1..^2])); // randomization is symmetric
            }
            else if ((ctrl & 0x08) != 0) SendAck(); // duplicate of a retransmission: re-acknowledge without delivering again
            else WriteRaw(Frame((byte)(0xA0 | _rxExpected), [])); // out-of-sequence frame: NAK
        }
        else if ((ctrl & 0xE0) == 0x80) OnAck(ctrl & 7); // ACK frame
        else if ((ctrl & 0xE0) == 0xA0) Log?.Invoke("ASH: NAK from NCP");
        else if (ctrl == CtrlRstAck)
        {
            Log?.Invoke($"ASH: RSTACK version {f[1]} code 0x{f[2]:X2}");
            lock (_gate) { _rxExpected = 0; _txNext = 0; }
            _rstWait?.TrySetResult(f[2]);
        }
        else if (ctrl == CtrlError) Log?.Invoke($"ASH: NCP ERROR code 0x{f[2]:X2}");
    }

    /// <summary>Unblocks the send in progress if the received ACK number is the expected one.</summary>
    private void OnAck(int ackNum)
    {
        lock (_gate)
        {
            if (_ackWait is { } w && ackNum == _ackExpected) w.TrySetResult(true);
        }
    }

    /// <summary>Tells the dongle we received all frames up to _rxExpected - 1.</summary>
    private void SendAck()
    {
        int ack;
        lock (_gate) ack = _rxExpected;
        WriteRaw(Frame((byte)(0x80 | ack), []));
    }

    // ------------------------------------------------------------ frame building

    /// <summary>Builds [control][data][CRC] (still without escapes and delimiters).</summary>
    private static byte[] Frame(byte ctrl, byte[] data)
    {
        var raw = new byte[data.Length + 3];
        raw[0] = ctrl;
        data.CopyTo(raw, 1);
        ushort crc = Crc(raw.AsSpan(0, raw.Length - 2));
        raw[^2] = (byte)(crc >> 8);
        raw[^1] = (byte)crc;
        return raw;
    }

    /// <summary>Writes to the serial port applying byte stuffing and the final delimiter.</summary>
    private void WriteRaw(byte[] raw, bool prefixCancel = false)
    {
        var o = new List<byte>(raw.Length + 8);
        if (prefixCancel) o.Add(Cancel);
        foreach (var b in raw)
        {
            // Reserved bytes inside the data are replaced by Escape + (byte XOR 0x20).
            if (b is Flag or Escape or Xon or Xoff or Substitute or Cancel) { o.Add(Escape); o.Add((byte)(b ^ 0x20)); }
            else o.Add(b);
        }
        o.Add(Flag);
        var bytes = o.ToArray();
        lock (_serial) _serial.Write(bytes, 0, bytes.Length); // one thread at a time writes to the port
    }

    /// <summary>
    /// ASH data randomization: XOR with a pseudo-random sequence (LFSR, seed 0x42) to avoid long runs
    /// of identical or reserved bytes. It is symmetric: the same function encrypts and decrypts.
    /// </summary>
    private static byte[] Randomize(byte[] data)
    {
        var o = new byte[data.Length];
        byte r = 0x42;
        for (int i = 0; i < data.Length; i++)
        {
            o[i] = (byte)(data[i] ^ r);
            r = (r & 1) == 0 ? (byte)(r >> 1) : (byte)((r >> 1) ^ 0xB8);
        }
        return o;
    }

    /// <summary>CRC-16 CCITT (polynomial 0x1021, initial value 0xFFFF), computed over control + data.</summary>
    private static ushort Crc(ReadOnlySpan<byte> d)
    {
        ushort crc = 0xFFFF;
        foreach (var b in d)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++) crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _serial.Close(); } catch { }
        _serial.Dispose();
    }
}
