// Stand-in for the Bluetooth controller during the first milestone: neutral input reports every
// 4 ms, plausible feature reports, and statistics about what the host sends (outputs, audio).

using System.Diagnostics;

namespace DualSenseWired.Device;

public sealed class TestLink : IControllerLink
{
    readonly Action<string> _log;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    long _nextInputTicks;
    byte _counter;
    long _audioBytes, _audioPeak, _outputs;
    readonly Timer _stats;

    public TestLink(Action<string> log)
    {
        _log = log;
        _stats = new Timer(_ => LogStats(), null, 5000, 5000);
    }

    public async Task<byte[]> NextInputReportAsync(CancellationToken ct)
    {
        // The real controller reports every 4 ms over USB.
        long now = _clock.ElapsedTicks;
        long period = Stopwatch.Frequency * 4 / 1000;
        _nextInputTicks = Math.Max(_nextInputTicks + period, now);
        long waitMs = (_nextInputTicks - now) * 1000 / Stopwatch.Frequency;
        if (waitMs > 0)
            await Task.Delay((int)waitMs, ct);
        var r = new byte[64];
        r[0] = 0x01;
        r[1] = r[2] = r[3] = r[4] = 0x80;   // sticks centred
        r[7] = _counter++;
        r[8] = 0x08;                        // d-pad released
        r[33] = r[37] = 0x80;               // no touch contacts
        r[53] = 0x08;                       // battery 80 %, discharging
        return r;
    }

    public void OutputReport(byte[] report)
    {
        if (Interlocked.Increment(ref _outputs) <= 3)
            _log($"output report {report[0]:x2}: {Convert.ToHexString(report, 0, Math.Min(report.Length, 16))}");
    }

    public byte[]? GetFeature(byte reportId, int length)
    {
        var data = new byte[length];
        data[0] = reportId;
        if (reportId == 0x09)
        {
            // Pairing info: a locally administered MAC address (stored reversed).
            new byte[] { 0x56, 0x34, 0x12, 0x00, 0xAA, 0x02 }.CopyTo(data, 1);
        }
        _log($"GET feature 0x{reportId:x2}");
        return data;
    }

    public void SetFeature(byte[] report) => _log($"SET feature 0x{report[0]:x2}");

    public void AudioOut(ReadOnlySpan<byte> pcm)
    {
        Interlocked.Add(ref _audioBytes, pcm.Length);
        long peak = 0;
        for (int i = 0; i + 1 < pcm.Length; i += 2)
            peak = Math.Max(peak, Math.Abs((short)(pcm[i] | pcm[i + 1] << 8)));
        if (peak > Interlocked.Read(ref _audioPeak))
            Interlocked.Exchange(ref _audioPeak, peak);
    }

    public void SpeakerVolume(bool mute, double decibels) => _log($"speaker volume {decibels:0.#} dB{(mute ? ", muted" : "")}");

    void LogStats()
    {
        long bytes = Interlocked.Exchange(ref _audioBytes, 0);
        long peak = Interlocked.Exchange(ref _audioPeak, 0);
        long outputs = Interlocked.Exchange(ref _outputs, 0);
        if (bytes > 0 || outputs > 0)
            _log($"audio {bytes / 5 / 1000} KB/s (peak {peak * 100 / 32768}%), output reports {outputs} in 5 s");
    }
}
