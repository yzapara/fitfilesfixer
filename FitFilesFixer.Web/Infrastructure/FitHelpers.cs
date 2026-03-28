using Dynastream.Fit;

namespace FitFilesFixer.Web.Infrastructure;

public static class FitHelpers
{
    public static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6_371_000.0;
        double dLat = ToRad(lat2 - lat1);
        double dLon = ToRad(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2))
                 * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;

    public static int DegreesToSemicircles(double degrees)
        => (int)(degrees * (Math.Pow(2, 31) / 180.0));

    public static double SemicirclesToDegrees(int semicircles)
        => semicircles * (180.0 / Math.Pow(2, 31));

    public static List<Mesg> ReadAllMessages(string filePath)
    {
        var all = new List<Mesg>();
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        var decode = new Decode();
        var broadcaster = new MesgBroadcaster();
        broadcaster.MesgEvent += (_, e) => all.Add(e.mesg);
        decode.MesgEvent += broadcaster.OnMesg;
        decode.MesgDefinitionEvent += broadcaster.OnMesgDefinition;
        decode.Read(fs);
        return all;
    }

    public static void WriteAllMessages(List<Mesg> messages, string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite);
        var encoder = new Encode(ProtocolVersion.V20);
        encoder.Open(fs);
        foreach (var m in messages)
            encoder.Write(m);
        encoder.Close();
    }
}
