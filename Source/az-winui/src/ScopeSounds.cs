using System;
using System.IO;
using System.Media;

namespace AccessibilityZoom
{
    // Original, short mechanical scope cues. PCM is generated once and preloaded;
    // toggling zoom only starts asynchronous playback, with no disk or network I/O.
    internal sealed class ScopeSounds : IDisposable
    {
        private readonly MemoryStream inAudio = CreateCue(true);
        private readonly MemoryStream outAudio = CreateCue(false);
        private readonly SoundPlayer scopeIn;
        private readonly SoundPlayer scopeOut;

        internal ScopeSounds()
        {
            scopeIn = new SoundPlayer(inAudio);
            scopeOut = new SoundPlayer(outAudio);
            scopeIn.Load();
            scopeOut.Load();
        }

        internal void Play(bool active)
        {
            // Sound feedback must never interrupt magnification or trigger an
            // emergency display reset if Windows audio becomes unavailable.
            try { (active ? scopeIn : scopeOut).Play(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        private static MemoryStream CreateCue(bool entering)
        {
            const int rate = 22050;
            int count = (int)(rate * (entering ? .19 : .14));
            MemoryStream stream = new MemoryStream(44 + count * 2);
            using (BinaryWriter writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, true))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + count * 2);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16); writer.Write((short)1); writer.Write((short)1);
                writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(count * 2);
                Random random = new Random(entering ? 73 : 91);
                double lowNoise = 0;
                for (int i = 0; i < count; i++)
                {
                    double t = i / (double)rate;
                    double noise = random.NextDouble() * 2 - 1;
                    lowNoise += .24 * (noise - lowNoise);
                    double click = Math.Exp(-t * 180) * (.65 * noise + .35 * Math.Sin(2 * Math.PI * 1700 * t));
                    double latchTime = t - (entering ? .095 : .065);
                    double latch = latchTime < 0 ? 0 : Math.Exp(-latchTime * 150) *
                        (.7 * noise + .3 * Math.Sin(2 * Math.PI * 1100 * latchTime));
                    double sweep = Math.Sin(Math.PI * i / count) * Math.Exp(-t * 12) * lowNoise;
                    double value = (entering ? .42 : .30) * (click + .65 * latch + .65 * sweep);
                    double fade = Math.Min(1, (count - 1 - i) / (rate * .012));
                    writer.Write((short)(Math.Clamp(value * fade, -.8, .8) * short.MaxValue));
                }
            }
            stream.Position = 0;
            return stream;
        }

        public void Dispose()
        {
            scopeIn.Stop(); scopeOut.Stop();
            scopeIn.Dispose(); scopeOut.Dispose();
            inAudio.Dispose(); outAudio.Dispose();
        }
    }
}
