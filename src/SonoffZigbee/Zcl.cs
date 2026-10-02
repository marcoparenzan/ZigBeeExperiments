using System.Buffers.Binary;
using System.Text;

namespace SonoffZigbee;

/// <summary>Identifiers of the most used ZCL (Zigbee Cluster Library) clusters.</summary>
public static class ZclCluster
{
    public const ushort Basic = 0x0000, PowerConfiguration = 0x0001, OnOff = 0x0006, LevelControl = 0x0008,
        ColorControl = 0x0300, Temperature = 0x0402, Humidity = 0x0405, Occupancy = 0x0406, IasZone = 0x0500;
}

/// <summary>A parsed ZCL frame.</summary>
/// <param name="ClusterSpecific">True for a cluster-specific command (for example Toggle); false for a general one (read attributes...).</param>
/// <param name="Command">Command identifier.</param>
/// <param name="Sequence">Sequence number chosen by the sender.</param>
/// <param name="Payload">Command parameters.</param>
public sealed record ZclFrame(bool ClusterSpecific, byte Command, byte Sequence, byte[] Payload);

/// <summary>An attribute read from a ZCL frame. Raw is set for strings (type 0x41/0x42), whose bytes may not be text.</summary>
public sealed record ZclAttribute(ushort Id, byte Type, object? Value, byte[]? Raw = null);

/// <summary>Building and reading ZCL frames.</summary>
public static class Zcl
{
    private static byte _seq;

    /// <summary>Next ZCL sequence number (1..255, then wraps).</summary>
    public static byte NextSeq() => ++_seq;

    /// <summary>Builds a ZCL frame: [control][sequence][command][parameters].</summary>
    public static byte[] Build(bool clusterSpecific, byte command, byte[] payload, byte? seq = null)
    {
        var f = new byte[payload.Length + 3];
        f[0] = (byte)((clusterSpecific ? 0x01 : 0x00) | 0x10); // 0x10 = disable default response
        f[1] = seq ?? NextSeq();
        f[2] = command;
        payload.CopyTo(f, 3);
        return f;
    }

    /// <summary>Parses a ZCL frame; returns null if it is too short.</summary>
    public static ZclFrame? Parse(byte[] d)
    {
        if (d.Length < 3) return null;
        int i = 1;
        if ((d[0] & 0x04) != 0) i += 2; // manufacturer code present (2 bytes): skip it
        if (d.Length < i + 2) return null;
        return new ZclFrame((d[0] & 0x03) == 1, d[i + 1], d[i], d[(i + 2)..]);
    }

    /// <summary>Decodes a Read Attributes Response (0x01) or a Report Attributes (0x0A).</summary>
    public static List<ZclAttribute> ParseAttributes(ZclFrame f)
    {
        var list = new List<ZclAttribute>();
        if (f.ClusterSpecific || (f.Command != 0x01 && f.Command != 0x0A)) return list;
        var p = f.Payload;
        int i = 0;
        while (i + 3 <= p.Length)
        {
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(i)); i += 2;
            if (f.Command == 0x01)
            {
                // A read response carries a status before the value: if it is not 0 the value is missing.
                byte status = p[i++];
                if (status != 0) { list.Add(new ZclAttribute(id, 0, $"status 0x{status:X2}")); continue; }
            }
            byte type = p[i++];
            if (!TryReadValue(type, p, ref i, out var value, out var raw)) break; // unsupported type: stop here
            list.Add(new ZclAttribute(id, type, value, raw));
        }
        return list;
    }

    /// <summary>Reads a typed ZCL value at <paramref name="i"/> and advances the index.</summary>
    internal static bool TryReadValue(byte type, byte[] p, ref int i, out object? v, out byte[]? raw)
    {
        v = null;
        raw = null;
        // Size in bytes of the value for each ZCL type. For strings the first byte is the length.
        int size = type switch
        {
            0x10 or 0x18 or 0x20 or 0x28 or 0x30 => 1,
            0x19 or 0x21 or 0x29 or 0x31 => 2,
            0x22 or 0x2A => 3,
            0x1B or 0x23 or 0x2B or 0x39 => 4,
            0x41 or 0x42 => p.Length > i ? 1 + p[i] : int.MaxValue,
            _ => int.MaxValue, // unknown type
        };
        if (size == int.MaxValue || i + size > p.Length) return false;
        var s = p.AsSpan(i, size);
        v = type switch
        {
            0x10 => s[0] != 0,                                       // boolean
            0x18 or 0x20 or 0x30 => s[0],                            // unsigned byte / bitmap / enum
            0x28 => (sbyte)s[0],                                     // signed byte
            0x19 or 0x21 or 0x31 => BinaryPrimitives.ReadUInt16LittleEndian(s),
            0x29 => BinaryPrimitives.ReadInt16LittleEndian(s),       // for example temperature (hundredths of °C)
            0x1B or 0x23 => BinaryPrimitives.ReadUInt32LittleEndian(s),
            0x2B => BinaryPrimitives.ReadInt32LittleEndian(s),
            0x39 => BinaryPrimitives.ReadSingleLittleEndian(s),
            0x42 => Encoding.UTF8.GetString(s[1..]),                 // character string
            _ => Convert.ToHexString(s),
        };
        if (type is 0x41 or 0x42) raw = s[1..].ToArray();            // keep the original bytes (needed for Aqara 0xFF01)
        i += size;
        return true;
    }
}
