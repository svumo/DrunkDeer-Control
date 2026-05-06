using HidSharp;

namespace Driver;

public static class HidDeviceExtensions
{
    public static TResult Using<TResult, T>(
        this T factory,
        Func<T, TResult> use) where T : IDisposable
    {
        using var disposable = factory;
        return use(disposable);
    }

    public static string PacketToString(this byte[] packet)
        => string.Format("[{0}]", string.Join(" ", packet.Select(b => string.Format("{0:x2}", b))));

    public static bool WritePacket(this HidStream stream, byte[][] packets)
    {
        DebugLogger.Log($"WritePacket batch start ({packets.Length} packets)");
        int failed = 0;
        for (int i = 0; i < packets.Length; i++)
        {
            if (!stream.TryWritePacket(packets[i]))
            {
                DebugLogger.Log($"  packet {i}/{packets.Length} failed (continuing batch)");
                failed++;
            }
        }
        if (failed == 0)
            DebugLogger.Log($"WritePacket batch complete ({packets.Length} packets ok)");
        else
            DebugLogger.Log($"WritePacket batch complete ({failed}/{packets.Length} packets failed)");
        return failed == 0;
    }

    public static bool TryWritePacket(this HidStream stream, byte[] packet)
    {
        var response = stream.WritePacket(packet);
        var ok = response.Length > 0 && response.First() == packet[0];
        if (!ok)
        {
            DebugLogger.Log($"  TryWritePacket mismatch: sent[0]=0x{packet[0]:x2} resp.len={response.Length} resp[0]={(response.Length > 0 ? $"0x{response[0]:x2}" : "n/a")}");
        }
        return ok;
    }

    public static byte[] WritePacket(this HidStream stream, byte[] packet)
    {
        if (packet.Length < 1) return [];
        if (packet.Length > 63)
        {
            throw new Exception(string.Format("Packet {0}, probably should be of length < 64", PacketToString(packet)));
        }
        DebugLogger.Log($"  -> {packet.PacketToString()}");
        try
        {
            stream.Write([Packets.REPORT_ID, .. packet]);
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"  WRITE FAILED: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
        try
        {
            var response = stream.Read();
            var trimmed = response.Skip(1).ToArray();
            DebugLogger.Log($"  <- {trimmed.PacketToString()}");
            return trimmed;
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"  READ FAILED: {ex.GetType().Name}: {ex.Message}");
        }
        return [];
    }

    public static bool Ping(this HidStream stream)
        => stream.TryWritePacket(Packets.IDENTITY_PACKET);

    public static KeyboardSpecs GetKeyboardSpecs(this HidStream stream)
        => new(stream.WritePacket(Packets.IDENTITY_PACKET));

    public static bool IsCompatible(this KeyboardSpecs specs)
        => specs.KeyboardType is not null;
}
