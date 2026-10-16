// Everything that goes to the Bluetooth controller: the audio stream (speaker Opus + HD haptics in
// extended output reports) and the game's output reports (rumble, triggers, LEDs). A port of the
// Steam Deck plugin's AudioStreamer (py_modules/dualsense_audio.py); protocol notes are there and in
// research/FINDINGS.md.
//
//   report     = [id][seq << 4][sub-packet]...[zero padding][CRC32 LE, seed 0xA2]
//   sub-packet = [0x80 | 0x40 if two blocks | type][block length][block x 1 or 2]
//   type 0x10, 63 bytes: settings block ("common" output fields)
//   type 0x11,  7 bytes: stream header "fe 30 30 30 30 30" + 8-bit counter
//   type 0x12, 64 bytes: haptics, 32 stereo frames of signed 8-bit (3 kHz)
//   type 0x13, 200 bytes: one Opus frame (stereo, 480 samples, CELT), zero padded
//
// The controller plays one report per 32 haptics frames at 3 kHz = 10.667 ms, so its audio runs at
// 480 samples per 10.667 ms = 45 kHz: the host's 48 kHz audio is resampled to 45 kHz and fed to the
// Opus encoder in 480-sample frames.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Concentus;
using Concentus.Enums;
using DualSenseWired.Hid;
using DualSenseWired.Protocol;

namespace DualSenseWired.Bridge;

public sealed class ControllerStream : IDisposable
{
    static readonly Dictionary<byte, int> ReportSizes = new()
    {
        [0x31] = 78, [0x32] = 142, [0x33] = 206, [0x34] = 270, [0x35] = 334, [0x36] = 398,
        [0x37] = 462, [0x38] = 526, [0x39] = 547,
    };

    const byte SubSettings = 0x10, SubStream = 0x11, SubHaptics = 0x12, SubOpus = 0x13;
    const byte SubPresent = 0x80, SubTwoBlocks = 0x40;
    const int SettingsSize = 63;
    const int HapticsBlock = 64;                 // 32 stereo frames, int8
    const int HapticsFrames = HapticsBlock / 2;
    const int HapticsRate = 3000;
    const int OpusFrameBytes = 200;
    const int OpusFrameSamples = 480;
    static readonly byte[] OpusSilence = { 0xF4, 0xFF, 0xFE };   // as sent by the controller host when silent

    const int HostRate = 48000;
    const int ControllerRate = 45000;
    const int Channels = 4;                      // 1-2 speaker, 3-4 the two haptic actuators
    const int HapticsDecimation = OpusFrameSamples / HapticsFrames;   // 45 kHz / 15 = 3 kHz
    const double HapticsLowpassHz = 1200;        // real haptics signals stay below ~1 kHz
    static readonly double Period = (double)OpusFrameSamples / ControllerRate;   // 10.667 ms

    // valid_flag0 bits of the audio fields only (headset volume, speaker volume, audio control), so
    // the stream does not override lightbar/triggers/rumble. Game output merged into the stream has
    // flag0 0x02 (rumble instead of haptics) and flag1 0x08 (release LEDs) cleared.
    const byte AudioOnlyFlag0 = 0xB0;
    const byte StreamBlockedFlag0 = 0x02;
    const byte StreamBlockedFlag1 = 0x08;

    // Audio waits in two places: our queue (Prebuffer frames of 10.7 ms before playing; Windows
    // delivers it evenly, so little is needed) and the controller's buffer (kept near TargetLevel).
    // Both add to the delay between the game and the speaker.
    const int Prebuffer = 2;
    const int MaxQueue = 24;
    // Byte 65 of the Bluetooth input report: how much audio the controller has buffered (ms).
    public const int InputAudioLevelOffset = 65;
    const int TargetLevel = 32;                  // DSX's default is 48 ms
    const int LevelLowMargin = 12;
    const int LevelHighMargin = 12;              // the controller drops its whole buffer at ~64+
    const double LevelSmoothing = 0.05;
    const int LevelAdjustInterval = 20;          // reports between corrections (~200 ms)

    // Audio reports only while there is something to play: a constant stream of silence competes
    // with Wi-Fi for the radio, and a congested link drops the controller.
    static readonly TimeSpan StreamHold = TimeSpan.FromSeconds(1.5);
    static readonly TimeSpan StallThreshold = TimeSpan.FromMilliseconds(200);
    static readonly TimeSpan StallBackoff = TimeSpan.FromSeconds(2);
    const double ResyncLateness = 0.1;
    static readonly TimeSpan StatsPeriod = TimeSpan.FromSeconds(5);

    readonly Action<string> _log;
    readonly OutputState _gameOutput = new();
    readonly object _queueLock = new();
    readonly Queue<short[]> _queue = new();
    readonly IResampler _resampler = ResamplerFactory.CreateResampler(Channels, HostRate, ControllerRate, 5, null);
    readonly short[] _resampled = new short[OpusFrameSamples * Channels];
    int _resampledFrames;
    readonly IOpusEncoder _encoder;
    readonly Thread _thread;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    volatile bool _stop;
    volatile HidDevice? _device;
    volatile int _level = -1;
    volatile float _speakerGain = 1;
    TimeSpan _backoffUntil;
    int _seq;
    long _dropped, _stalls, _underruns, _inputFrames;
    int _loggedOutputs;

    /// <summary>Controller speaker volume (0..0x7F); the host's volume control scales the audio itself.</summary>
    public int Volume { get; set; } = 0x7F * 80 / 100;
    /// <summary>Haptics strength, 1 = as the game plays it.</summary>
    public double HapticsGain { get; set; } = 1;

    public ControllerStream(Action<string> log)
    {
        _log = log;
        _encoder = OpusCodecFactory.CreateEncoder(HostRate, 2, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY, null);
        _encoder.Bitrate = 160000;      // CBR: 200 bytes per 10 ms frame, exactly the Opus block
        _encoder.UseVBR = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "controller stream", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    /// <summary>The controller to send to, or null while it is away.</summary>
    public HidDevice? Device
    {
        get => _device;
        set
        {
            _device = value;
            _level = -1;
        }
    }

    /// <summary>Common part (47 bytes) of a game output report, sent with the next report.</summary>
    public void SubmitOutput(ReadOnlySpan<byte> common) => _gameOutput.Update(common);

    /// <summary>The controller's audio buffer level from a Bluetooth input report.</summary>
    public void InputReport(byte[] btReport)
    {
        if (btReport.Length > InputAudioLevelOffset && btReport[0] == DualSenseProtocol.BtInputReport)
            _level = btReport[InputAudioLevelOffset];
    }

    public void SpeakerVolume(bool mute, double decibels) =>
        _speakerGain = mute ? 0 : (float)Math.Pow(10, Math.Min(0, decibels) / 20);

    /// <summary>Host audio, 48 kHz, 4 channels, s16le; resampled to 45 kHz and queued in 480-frame blocks.</summary>
    public void AudioIn(ReadOnlySpan<byte> pcm)
    {
        var samples = MemoryMarshal.Cast<byte, short>(pcm).ToArray();
        Interlocked.Add(ref _inputFrames, samples.Length / Channels);
        float gain = _speakerGain;
        if (gain < 1)
            for (int i = 0; i < samples.Length; i += Channels)
            {
                samples[i] = (short)(samples[i] * gain);
                samples[i + 1] = (short)(samples[i + 1] * gain);
            }
        lock (_queueLock)
        {
            int offset = 0;
            int frames = samples.Length / Channels;
            while (offset < frames)
            {
                int inLen = frames - offset;
                int outLen = OpusFrameSamples - _resampledFrames;
                _resampler.ProcessInterleaved(samples.AsSpan(offset * Channels), ref inLen,
                                              _resampled.AsSpan(_resampledFrames * Channels), ref outLen);
                offset += inLen;
                _resampledFrames += outLen;
                if (_resampledFrames == OpusFrameSamples)
                {
                    _queue.Enqueue((short[])_resampled.Clone());
                    _resampledFrames = 0;
                    while (_queue.Count > MaxQueue)
                    {
                        _queue.Dequeue();
                        _dropped++;
                    }
                }
                if (inLen == 0 && outLen == 0)
                    break;
            }
        }
    }

    // -- report building --

    static byte[] BuildReport(byte reportId, int seq, params (byte Type, byte[][] Blocks)[] subpackets)
    {
        int size = ReportSizes[reportId];
        var report = new byte[size];
        report[0] = reportId;
        report[1] = (byte)((seq & 0x0F) << 4);
        int offset = 2;
        foreach (var (type, blocks) in subpackets)
        {
            int length = blocks[0].Length;
            report[offset] = (byte)(SubPresent | type | (blocks.Length == 2 ? SubTwoBlocks : 0));
            report[offset + 1] = (byte)length;
            offset += 2;
            foreach (var block in blocks)
            {
                block.CopyTo(report, offset);
                offset += block.Length;
            }
        }
        if (offset > size - 4)
            throw new InvalidOperationException($"sub-packets do not fit into report 0x{reportId:x2}");
        DualSenseProtocol.WriteCrc(report, DualSenseProtocol.OutputCrcSeed);
        return report;
    }

    static byte[] StreamHeader(int counter) => new byte[] { 0xFE, 0x30, 0x30, 0x30, 0x30, 0x30, (byte)counter };

    /// <summary>Settings block: audio fields from us, everything else from the game (if it sent anything).</summary>
    byte[] Settings()
    {
        var block = new byte[SettingsSize];
        block[0] = AudioOnlyFlag0;
        block[4] = block[5] = (byte)Volume;     // headset, speaker
        block[6] = 0xFF;                        // microphone volume (as observed)
        block[7] = 0x09;                        // audio control (as observed)
        block[9] = 0x0F;                        // power save control (as observed)
        block[37] = 0x0A;                       // constant in captures
        block[41] = 0x02;                       // lightbar setup (as observed)
        block[46] = 0xFF;
        var game = _gameOutput.Take();
        if (game != null)
        {
            if (_loggedOutputs++ < 3)
                _log($"game output into the stream: {Convert.ToHexString(game, 0, 12)}...");
            byte audioControl = (game[0] & 0x80) != 0 ? game[7] : block[7];
            game.CopyTo(block, 0);
            block[0] = (byte)((block[0] & ~StreamBlockedFlag0) | AudioOnlyFlag0);
            block[1] &= unchecked((byte)~StreamBlockedFlag1);
            block[4] = block[5] = (byte)Volume;
            block[7] = audioControl;
        }
        return block;
    }

    // -- encoding --

    readonly byte[] _opusOut = new byte[1500];
    readonly short[] _speaker = new short[OpusFrameSamples * 2];
    readonly double[] _hapticsY = new double[2];
    static readonly double HapticsAlpha = 1 - Math.Exp(-2 * Math.PI * HapticsLowpassHz / HapticsRate);

    (byte[] Opus, byte[] Haptics) Encode(short[]? frame)
    {
        var opus = new byte[OpusFrameBytes];
        var haptics = new byte[HapticsBlock];
        OpusSilence.CopyTo(opus, 0);
        if (frame == null)
            return (opus, haptics);
        bool speakerSound = false, hapticsSignal = false;
        for (int i = 0; i < OpusFrameSamples; i++)
        {
            _speaker[2 * i] = frame[Channels * i];
            _speaker[2 * i + 1] = frame[Channels * i + 1];
            speakerSound |= frame[Channels * i] != 0 || frame[Channels * i + 1] != 0;
            hapticsSignal |= frame[Channels * i + 2] != 0 || frame[Channels * i + 3] != 0;
        }
        if (speakerSound)
        {
            int n = _encoder.Encode(_speaker, OpusFrameSamples, _opusOut, _opusOut.Length);
            if (n > 0 && n <= OpusFrameBytes)
            {
                Array.Clear(opus);
                _opusOut.AsSpan(0, n).CopyTo(opus);
            }
        }
        if (hapticsSignal)
        {
            double gain = HapticsGain / 32768.0 * 127;
            for (int ch = 0; ch < 2; ch++)
            {
                double y = _hapticsY[ch];
                for (int i = 0; i < HapticsFrames; i++)
                {
                    double sum = 0;
                    for (int k = 0; k < HapticsDecimation; k++)
                        sum += frame[Channels * (i * HapticsDecimation + k) + 2 + ch];
                    y += HapticsAlpha * (sum / HapticsDecimation - y);
                    haptics[2 * i + ch] = (byte)(sbyte)Math.Clamp((int)(y * gain), -128, 127);
                }
                _hapticsY[ch] = y;
            }
        }
        return (opus, haptics);
    }

    static bool Audible(short[]? frame) => frame != null && Array.Exists(frame, s => s != 0);

    // -- the stream: one report every 10.667 ms --

    void Write(HidDevice device, byte[] report)
    {
        var started = _clock.Elapsed;
        try
        {
            device.WriteAsync(report, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (IOException e)
        {
            _log($"write to the controller failed: {e.Message}");
            return;
        }
        var took = _clock.Elapsed - started;
        if (took > StallThreshold)
        {
            // The Bluetooth link is congested: give it time to drain instead of piling up more.
            _backoffUntil = _clock.Elapsed + StallBackoff;
            _stalls++;
            _log($"Bluetooth write took {took.TotalMilliseconds:0} ms, pausing the audio stream for {StallBackoff.TotalSeconds:0} s");
        }
    }

    void Run()
    {
        timeBeginPeriod(1);
        // Scheduled like an audio engine thread (MMCSS), so a busy foreground app does not delay
        // reports; a burst of late reports overflows the controller's buffer, which then drops it.
        uint taskIndex = 0;
        IntPtr mmcss = AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
        if (mmcss == IntPtr.Zero)
            _log($"MMCSS not available (error {Marshal.GetLastWin32Error()}), using a high priority thread");
        _timer = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        try
        {
            Loop();
        }
        catch (Exception e)
        {
            _log($"controller stream failed: {e}");
        }
        finally
        {
            if (mmcss != IntPtr.Zero)
                AvRevertMmThreadCharacteristics(mmcss);
            if (_timer != IntPtr.Zero)
                CloseHandle(_timer);
            timeEndPeriod(1);
        }
    }

    void Loop()
    {
        bool primed = false, streaming = false;
        long tick = 0, lastAdjust = 0;
        double? smoothed = null;
        double start = _clock.Elapsed.TotalSeconds;
        var lastAudible = -StreamHold - StreamHold;
        var nextStats = _clock.Elapsed + StatsPeriod;
        int doubles = 0, skips = 0, sent = 0, minLevel = int.MaxValue, maxLevel = 0;
        long lastDropped = 0, lastUnderruns = 0;
        HidDevice? lastDevice = null;

        while (!_stop)
        {
            var device = _device;
            if (device != lastDevice)
            {
                lastDevice = device;
                smoothed = null;
                primed = false;
            }
            int level = _level;
            if (level >= 0)
            {
                smoothed = smoothed == null ? level : smoothed + LevelSmoothing * (level - smoothed.Value);
                minLevel = Math.Min(minLevel, level);
                maxLevel = Math.Max(maxLevel, level);
            }

            short[]? frame = null, frame2 = null;
            bool low, high;
            lock (_queueLock)
            {
                if (!primed && _queue.Count >= Prebuffer)
                    primed = true;
                if (primed && _queue.Count > 0)
                    frame = _queue.Dequeue();
                bool mayAdjust = smoothed != null && tick - lastAdjust >= LevelAdjustInterval;
                low = mayAdjust && smoothed < TargetLevel - LevelLowMargin;
                high = mayAdjust && smoothed > TargetLevel + LevelHighMargin;
                // Catch up only while our own queue keeps a reserve.
                if (frame != null && low && _queue.Count >= 2)
                    frame2 = _queue.Dequeue();
            }

            var now = _clock.Elapsed;
            if (Audible(frame) || Audible(frame2))
                lastAudible = now;
            bool idle = device == null || now - lastAudible > StreamHold || now < _backoffUntil;
            if (primed && frame == null && !idle)
            {
                _underruns++;
                _log($"underrun at {now.TotalSeconds:0.000} s");
                primed = false;
            }
            if (!idle && !streaming)
                _log("audio stream to the controller started");
            else if (idle && streaming && device != null)
            {
                _log("audio stream to the controller paused (silence)");
                // Leave the actuators and speaker quiet.
                Write(device, BuildReport(0x32, _seq++, (SubSettings, new[] { Settings() }), (SubHaptics, new[] { new byte[HapticsBlock] })));
            }
            streaming = !idle;

            double late = now.TotalSeconds - (start + tick * Period);
            if (late > ResyncLateness)
                start = now.TotalSeconds - tick * Period;     // after a stall: carry on, don't burst

            if (device == null)
            {
                // Nothing to send to; keep the game's last output for when the controller is back.
            }
            else if (idle)
            {
                // Only game output (triggers, LEDs, rumble) goes to the controller.
                if (now >= _backoffUntil)
                {
                    var game = _gameOutput.Take();
                    if (game != null)
                        Write(device, DualSenseProtocol.CommonToBt(game, _seq++));
                }
            }
            else if (frame != null && high)
            {
                skips++;            // the controller has too much queued: drop this frame
                lastAdjust = tick;
            }
            else if (frame2 != null)
            {
                lastAdjust = tick;
                var (o1, h1) = Encode(frame);
                var (o2, h2) = Encode(frame2);
                Write(device, BuildReport(0x39, _seq, (SubStream, new[] { StreamHeader(_seq) }),
                                          (SubOpus, new[] { o1, o2 }), (SubHaptics, new[] { h1, h2 })));
                _seq++;
                doubles++;
                sent++;
            }
            else
            {
                var (opus, haptics) = Encode(frame);
                Write(device, BuildReport(0x36, _seq, (SubSettings, new[] { Settings() }), (SubStream, new[] { StreamHeader(_seq) }),
                                          (SubOpus, new[] { opus }), (SubHaptics, new[] { haptics })));
                _seq++;
                sent++;
            }

            if (now >= nextStats)
            {
                if (sent > 0)
                {
                    int queued;
                    lock (_queueLock)
                        queued = _queue.Count;
                    _log($"audio stream: {sent} reports, controller buffer " +
                         (maxLevel > 0 ? $"{minLevel}..{maxLevel}" : "?") +
                         $" ms, doubles {doubles}, skipped {skips}, underruns +{_underruns - lastUnderruns}, " +
                         $"dropped +{_dropped - lastDropped}, stalls {_stalls}, queue {queued}, host audio {Interlocked.Read(ref _inputFrames) / StatsPeriod.TotalSeconds:0} frames/s");
                }
                Interlocked.Exchange(ref _inputFrames, 0);
                lastDropped = _dropped;
                lastUnderruns = _underruns;
                sent = doubles = skips = maxLevel = 0;
                minLevel = int.MaxValue;
                nextStats = now + StatsPeriod;
            }

            tick++;
            double delay = start + tick * Period - _clock.Elapsed.TotalSeconds;
            if (delay > 0)
                Wait(delay);
        }

        var last = _device;
        if (last != null && streaming)
            Write(last, BuildReport(0x32, _seq++, (SubSettings, new[] { Settings() }), (SubHaptics, new[] { new byte[HapticsBlock] })));
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(1));
    }

    IntPtr _timer;

    /// <summary>Sleep with sub-millisecond precision (high resolution waitable timer, else Sleep).</summary>
    void Wait(double seconds)
    {
        long dueTime = -(long)(seconds * 10_000_000);    // relative, 100 ns units
        if (_timer != IntPtr.Zero && SetWaitableTimer(_timer, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
            WaitForSingleObject(_timer, 1000);
        else
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
    }

    const uint CreateWaitableTimerHighResolution = 0x2, TimerAllAccess = 0x1F0003;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll")]
    static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr AvSetMmThreadCharacteristics(string task, ref uint taskIndex);
    [DllImport("avrt.dll")] static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint period);
}
