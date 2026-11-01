using System.IO;
using System.Media;

namespace FocusGarden.App;

internal static class DailyAlarm
{
    internal static Task PlayAsync() => Task.Run(() =>
    {
        using var stream = new MemoryStream(CreateWave());
        using var sound = new SoundPlayer(stream); sound.PlaySync();
    });
    internal static byte[] CreateWave()
    {
        const int rate = 22050, samples = rate * 2;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, true))
        {
            writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
            writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(samples * 2);
            for (int i = 0; i < samples; i++)
            {
                double t = (double)i / rate, phase = t % .66;
                double envelope = phase < .45 ? Math.Min(1, phase / .02) * Math.Exp(-phase * 5) : 0;
                writer.Write((short)(9500 * envelope * Math.Sin(2 * Math.PI * (t < .66 ? 660 : t < 1.32 ? 825 : 990) * t)));
            }
        }
        return stream.ToArray();
    }
}
