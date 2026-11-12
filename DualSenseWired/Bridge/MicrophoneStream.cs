// The controller microphone over Bluetooth, played into the virtual wired DualSense's microphone.
// With the microphone flag in the audio stream header (ControllerStream.MicEnabled) the controller
// interleaves extra 0x31 input reports with byte 1 bit 1 set: a 71-byte Opus frame (48 kHz mono,
// 10 ms) at offset 3 instead of controller state. The layout was learned from the open DS5Dongle
// project (github.com/awalol/DS5Dongle); ported from the Steam Deck plugin's dualsense_mic.py.

using Concentus;

namespace DualSenseWired.Bridge;

public sealed class MicrophoneStream
{
    const int Offset = 3;
    const int FrameBytes = 71;
    const int Rate = 48000;
    const int FrameSamples = Rate / 100;            // 10 ms
    // Jitter buffer: start playing once this much is queued, never keep more than the maximum.
    const int StartSamples = FrameSamples * 3;
    const int MaxSamples = FrameSamples * 12;

    readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(Rate, 1, null);
    readonly short[] _frame = new short[FrameSamples];
    readonly short[] _ring = new short[MaxSamples];
    readonly object _lock = new();
    int _head, _count;
    bool _playing;
    long _frames, _errors, _underruns;
    int _resetDecoder;

    /// <summary>A Bluetooth input report that carries a microphone frame instead of controller state.</summary>
    public static bool IsMicReport(byte[] report) => report.Length > 2 && report[0] == 0x31 && (report[1] & 0x02) != 0;

    public long Frames => Interlocked.Read(ref _frames);

    /// <summary>A microphone input report from the controller.</summary>
    public void Feed(byte[] report)
    {
        if (report.Length < Offset + FrameBytes)
            return;
        if (Interlocked.Exchange(ref _resetDecoder, 0) != 0)
            _decoder.ResetState();      // only on the thread that decodes
        int n;
        try
        {
            n = _decoder.Decode(report.AsSpan(Offset, FrameBytes), _frame, FrameSamples, false);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            Interlocked.Increment(ref _errors);
            return;
        }
        if (n <= 0)
        {
            Interlocked.Increment(ref _errors);
            return;
        }
        Interlocked.Increment(ref _frames);
        lock (_lock)
        {
            for (int i = 0; i < n; i++)
            {
                if (_count == MaxSamples)
                {
                    // Nobody is taking it fast enough: drop the oldest audio (keeps the delay bounded).
                    _head = (_head + 1) % MaxSamples;
                    _count--;
                }
                _ring[(_head + _count) % MaxSamples] = _frame[i];
                _count++;
            }
        }
    }

    /// <summary>Fill stereo 16-bit frames for the host (silence while nothing is buffered).</summary>
    public void Read(Span<byte> stereoPcm)
    {
        int frames = stereoPcm.Length / 4;
        stereoPcm.Clear();
        lock (_lock)
        {
            if (!_playing && _count >= StartSamples)
                _playing = true;
            if (!_playing)
                return;
            if (_count < frames)
            {
                // Ran dry (the controller stopped sending, or a gap on the link): wait for a reserve.
                _playing = false;
                _underruns++;
                return;
            }
            for (int i = 0; i < frames; i++)
            {
                short s = _ring[_head];
                _head = (_head + 1) % MaxSamples;
                stereoPcm[4 * i] = stereoPcm[4 * i + 2] = (byte)s;
                stereoPcm[4 * i + 1] = stereoPcm[4 * i + 3] = (byte)(s >> 8);
            }
            _count -= frames;
        }
    }

    /// <summary>Forget what is buffered (microphone turned off, controller gone).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _head = _count = 0;
            _playing = false;
        }
        Interlocked.Exchange(ref _resetDecoder, 1);
    }

    public string TakeSummary()
    {
        long frames = Interlocked.Exchange(ref _frames, 0);
        long errors = Interlocked.Exchange(ref _errors, 0);
        long underruns;
        lock (_lock)
        {
            underruns = _underruns;
            _underruns = 0;
        }
        return $"mic frames {frames}, bad {errors}, gaps {underruns}";
    }
}
