using System.IO;
using System.Media;

namespace FrogLauncher.Services;

/// <summary>
/// Звук сезона при запуске. Файлов нет — звуки синтезируются (WAV 22 кГц моно) и играются тихо, один раз:
/// весна — птицы, лето — кваканье, осень — шорох листьев, зима — хруст снега, Новый год — Jingle Bells
/// (мелодия 1857 года, общественное достояние).
/// </summary>
public sealed class SoundService
{
    public const int Rate = 22050;
    private const double Volume = 0.22;
    private SoundPlayer? _player;

    public void PlayStartup(Season season)
    {
        try
        {
            var wav = Wav(Synthesize(season));
            _player?.Dispose();
            _player = new SoundPlayer(new MemoryStream(wav));
            _player.Play();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException) { /* нет аудиоустройства */ }
    }

    public static float[] Synthesize(Season season) => season switch
    {
        Season.Spring => Birds(),
        Season.Summer => Croak(),
        Season.Autumn => Rustle(),
        Season.Winter => SnowSteps(),
        _ => JingleBells(),
    };

    // ---------- Звуки ----------

    private static float[] Birds()
    {
        var buf = new float[(int)(Rate * 1.8)];
        var rnd = new Random(7);
        double t0 = 0.05;
        for (var phrase = 0; phrase < 3; phrase++, t0 += 0.55)
            for (var i = 0; i < 5; i++)
            {
                // Чирик: быстрый подъём частоты, огибающая-«колокол».
                var start = t0 + i * 0.075;
                var f0 = 2600 + rnd.Next(600);
                Chirp(buf, start, 0.055, f0, f0 + 1800 + rnd.Next(900), 0.8);
            }
        return buf;
    }

    private static void Chirp(float[] buf, double start, double dur, double f0, double f1, double amp)
    {
        int s = (int)(start * Rate), n = (int)(dur * Rate);
        double phase = 0;
        for (var i = 0; i < n && s + i < buf.Length; i++)
        {
            var x = (double)i / n;
            phase += 2 * Math.PI * (f0 + (f1 - f0) * x) / Rate;
            buf[s + i] += (float)(amp * Math.Sin(Math.PI * x) * Math.Sin(phase));
        }
    }

    private static float[] Croak()
    {
        var buf = new float[(int)(Rate * 1.2)];
        foreach (var (start, dur) in new[] { (0.05, 0.22), (0.38, 0.30) })
        {
            int s = (int)(start * Rate), n = (int)(dur * Rate);
            for (var i = 0; i < n; i++)
            {
                var t = (double)i / Rate;
                var x = (double)i / n;
                // «Ква»: низкий тон с гармониками, порезанный на частые импульсы (30 Гц), частота чуть падает.
                var f = 140 - 30 * x;
                var tone = Math.Sin(2 * Math.PI * f * t) + 0.5 * Math.Sin(4 * Math.PI * f * t) + 0.3 * Math.Sin(6 * Math.PI * f * t);
                var pulse = Math.Pow(Math.Max(0, Math.Sin(2 * Math.PI * 30 * t)), 3);
                buf[s + i] += (float)(0.55 * tone * pulse * Math.Sin(Math.PI * x));
            }
        }
        return buf;
    }

    private static float[] Rustle()
    {
        var buf = new float[(int)(Rate * 1.6)];
        var rnd = new Random(3);
        double lp = 0, hpPrev = 0, hp = 0;
        for (var i = 0; i < buf.Length; i++)
        {
            var t = (double)i / Rate;
            // Шум, полосовой фильтр и «порывы» — несколько волн огибающей.
            var noise = rnd.NextDouble() * 2 - 1;
            lp += 0.35 * (noise - lp);
            hp = 0.9 * (hp + lp - hpPrev);
            hpPrev = lp;
            var gust = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 1.3 * t) * Math.Sin(2 * Math.PI * 0.4 * t + 1);
            var crackle = rnd.NextDouble() < 0.004 ? (rnd.NextDouble() - 0.5) * 1.5 : 0;
            buf[i] = (float)((hp * 1.4 * gust + crackle) * Math.Sin(Math.PI * t / 1.6));
        }
        return buf;
    }

    private static float[] SnowSteps()
    {
        var buf = new float[(int)(Rate * 1.7)];
        var rnd = new Random(11);
        for (var step = 0; step < 4; step++)
        {
            int s = (int)((0.05 + step * 0.4) * Rate), n = (int)(0.2 * Rate);
            double lp = 0;
            for (var i = 0; i < n && s + i < buf.Length; i++)
            {
                var x = (double)i / n;
                // Хруст: шум с редкими щелчками, резкая атака и быстрый спад.
                lp += 0.6 * ((rnd.NextDouble() * 2 - 1) - lp);
                var click = rnd.NextDouble() < 0.03 ? (rnd.NextDouble() - 0.5) * 2 : 0;
                buf[s + i] += (float)((lp * 0.9 + click) * Math.Exp(-5 * x) * Math.Min(1, x * 40));
            }
        }
        return buf;
    }

    private static float[] JingleBells()
    {
        // E E E | E E E | E G C D | E — припев, 4 доли в такте.
        (string note, double beats)[] melody =
        {
            ("E5", 1), ("E5", 1), ("E5", 2), ("E5", 1), ("E5", 1), ("E5", 2),
            ("E5", 1), ("G5", 1), ("C5", 1.5), ("D5", 0.5), ("E5", 4),
        };
        const double beat = 0.2;
        var total = melody.Sum(m => m.beats) * beat + 0.6;
        var buf = new float[(int)(Rate * total)];
        double t = 0.05;
        foreach (var (note, beats) in melody)
        {
            Bell(buf, t, Note(note), Math.Min(1.2, beats * beat + 0.4));
            t += beats * beat;
        }
        // Бубенцы на каждую долю.
        var rnd = new Random(5);
        for (var b = 0.05; b < total - 0.3; b += beat)
        {
            int s = (int)(b * Rate);
            for (var i = 0; i < Rate * 0.05 && s + i < buf.Length; i++)
                buf[s + i] += (float)((rnd.NextDouble() - 0.5) * 0.25 * Math.Exp(-i / (Rate * 0.012)) * Math.Sin(2 * Math.PI * 7000 * i / Rate));
        }
        return buf;
    }

    private static void Bell(float[] buf, double start, double f, double dur)
    {
        int s = (int)(start * Rate), n = (int)(dur * Rate);
        for (var i = 0; i < n && s + i < buf.Length; i++)
        {
            var t = (double)i / Rate;
            var env = Math.Exp(-3.2 * t) * Math.Min(1, i / 60.0);
            var v = Math.Sin(2 * Math.PI * f * t) + 0.45 * Math.Sin(2 * Math.PI * f * 2.0 * t) * Math.Exp(-6 * t)
                    + 0.25 * Math.Sin(2 * Math.PI * f * 3.01 * t) * Math.Exp(-9 * t);
            buf[s + i] += (float)(0.5 * env * v);
        }
    }

    internal static double Note(string n)
    {
        var semis = n[0] switch { 'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, _ => 11 };
        var octave = n[^1] - '0';
        return 440 * Math.Pow(2, (semis + 12 * (octave + 1) - 69) / 12.0);
    }

    // ---------- WAV ----------

    /// <summary>16-бит PCM моно; громкость — общая, тихая; пик нормируется.</summary>
    public static byte[] Wav(float[] samples)
    {
        var peak = Math.Max(1e-6f, samples.Max(Math.Abs));
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + samples.Length * 2);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(Rate);
        w.Write(Rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(samples.Length * 2);
        foreach (var s in samples) w.Write((short)(s / peak * Volume * short.MaxValue));
        w.Flush();
        return ms.ToArray();
    }
}
