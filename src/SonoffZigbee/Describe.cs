using System.Globalization;

namespace SonoffZigbee;

/// <summary>
/// Aqara/Xiaomi proprietary report (attribute 0xFF01 of the Basic cluster): a sequence of
/// [tag][type][value] records inside a string. Holds battery, internal temperature and more.
/// </summary>
public sealed record AqaraReport(int? BatteryMillivolts, int? DeviceTemperatureC, IReadOnlyDictionary<byte, object?> Fields)
{
    /// <summary>CR2032 charge estimate: 2.85 V = empty, 3.00 V = full (same range as Zigbee2MQTT).</summary>
    public int? BatteryPercent => BatteryMillivolts is { } mv ? Math.Clamp((mv - 2850) * 100 / 150, 0, 100) : null;

    /// <summary>Decodes the tag/type/value records. Returns null if the blob is not in this format.</summary>
    public static AqaraReport? TryParse(byte[] raw)
    {
        var fields = new Dictionary<byte, object?>();
        int i = 0;
        while (i + 2 <= raw.Length)
        {
            byte tag = raw[i++], type = raw[i++];
            if (!Zcl.TryReadValue(type, raw, ref i, out var v, out _)) break; // unsupported type: stop here
            fields[tag] = v;
        }
        if (fields.Count == 0) return null;
        // Tag 0x01 = battery voltage (mV, uint16); tag 0x03 = internal temperature (°C, int8).
        int? mv = fields.TryGetValue(0x01, out var b) && b is ushort u ? u : null;
        int? temp = fields.TryGetValue(0x03, out var t) && t is sbyte s ? s : null;
        return new AqaraReport(mv, temp, fields);
    }
}

/// <summary>Turns attribute reports into short, readable text.</summary>
public static class Describe
{
    /// <param name="model">Model identifier from the Basic cluster, if known (for example "lumi.sensor_magnet.aq2"). Needed to interpret clusters whose meaning depends on the device.</param>
    /// <param name="m">The received message.</param>
    public static string Message(string? model, IncomingMessage m)
    {
        if (m.Zcl is not { } z) return "";
        var parts = new List<string>();

        foreach (var a in Zcl.ParseAttributes(z))
            parts.Add(Attribute(model, m.ClusterId, a));

        // No attributes: it is a cluster-specific command (for example a button press); show it raw.
        if (parts.Count == 0)
            return $"comando cluster 0x{m.ClusterId:X4} cmd 0x{z.Command:X2} {Convert.ToHexString(z.Payload)}";
        return string.Join(", ", parts);
    }

    private static string Attribute(string? model, ushort cluster, ZclAttribute a)
    {
        bool magnet = model?.Contains("sensor_magnet", StringComparison.OrdinalIgnoreCase) == true;
        bool vibration = model?.Contains("vibration", StringComparison.OrdinalIgnoreCase) == true;

        switch (cluster, a.Id)
        {
            case (ZclCluster.Basic, 0x0005): return $"modello {a.Value}";
            case (ZclCluster.Basic, 0xFF01) when a.Raw is { } raw && AqaraReport.TryParse(raw) is { } r:
                return "report Aqara: " + string.Join(", ", new[]
                {
                    r.BatteryMillivolts is { } mv ? $"batteria {mv} mV (~{r.BatteryPercent}%)" : null,
                    r.DeviceTemperatureC is { } c ? $"temperatura interna {c} °C" : null,
                }.Where(s => s is not null).DefaultIfEmpty($"{r.Fields.Count} campi"));
            case (ZclCluster.OnOff, 0x0000) when a.Value is bool on:
                // On the Aqara door/window sensor "on" means the contact is open.
                return magnet ? (on ? "PORTA/FINESTRA APERTA" : "porta/finestra chiusa") : (on ? "ON" : "OFF");
            case (0x0101, 0x0055) when vibration:
                // Aqara vibration sensor: attribute 0x0055 of cluster 0x0101 is the event type.
                return Convert.ToInt32(a.Value) switch
                {
                    1 => "VIBRAZIONE",
                    2 => "INCLINAZIONE",
                    3 => "CADUTA",
                    _ => $"evento vibrazione {a.Value}",
                };
            case (ZclCluster.Temperature, 0x0000) when a.Value is short t:
                return $"temperatura {(t / 100.0).ToString("0.0", CultureInfo.InvariantCulture)} °C"; // value in hundredths of °C
            case (ZclCluster.Humidity, 0x0000) when a.Value is ushort h:
                return $"umidità {(h / 100.0).ToString("0.0", CultureInfo.InvariantCulture)} %";      // value in hundredths of %
            case (ZclCluster.Occupancy, 0x0000):
                return $"presenza {a.Value}";
            default:
                return a.Raw is { } rw ? $"attr 0x{a.Id:X4}=[{Convert.ToHexString(rw)}]" : $"attr 0x{a.Id:X4}={a.Value}";
        }
    }
}
