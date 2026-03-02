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
        foreach (var p in packets)
        {
            if (!stream.TryWritePacket(p))
            {
                return false;
            }
        }
        return true;
    }

    public static bool TryWritePacket(this HidStream stream, byte[] packet)
        => stream.WritePacket(packet) is { } response && response.Length > 0 && response.First() == packet[0];

    public static byte[] WritePacket(this HidStream stream, byte[] packet)
    {
        if (packet.Length < 1) return [];
        if (packet.Length > 63)
        {
            throw new Exception(string.Format("Packet {0}, probably should be of length < 64", PacketToString(packet)));
        }
        try
        {
            stream.Write([Packets.REPORT_ID, .. packet]);
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAILED TO WRITE PACKET {0}, {1}", packet, ex);
        }
        try
        {
            var response = stream.Read();
            return response.Skip(1).ToArray();
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAILED TO READ PACKET {0}", ex);
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
