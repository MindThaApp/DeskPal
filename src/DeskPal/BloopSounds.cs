using System.Runtime.InteropServices;
using DeskPal.Core;
using Vortice.Multimedia;
using Vortice.XAudio2;

namespace DeskPal;

// Short interaction sounds for Bloop, played through XAudio2 from a few preloaded voices so a tap is heard at once.
// Each play picks one of three pitch ratios so repeats do not sound identical. Silent when the setting is off,
// during a call or Focus Assist hold, or when the Windows output is muted. Only reads the bundled WAV files.
internal static class BloopSounds
{
    public static readonly string[] Names = ["squish", "boing", "giggle", "purr", "whoosh", "clap", "hop", "bonk"];
    private static readonly float[] Pitches = [0.94f, 1f, 1.07f];
    private const float Volume = 0.35f;
    private const int VoiceCount = 4;

    private static SettingsHub? _hub;
    private static NudgeScheduler? _scheduler;
    private static IXAudio2? _engine;
    private static IXAudio2MasteringVoice? _master;
    private static readonly IXAudio2SourceVoice?[] _voices = new IXAudio2SourceVoice?[VoiceCount];
    private static readonly Dictionary<string, AudioBuffer> _buffers = [];
    private static int _nextVoice;
    private static bool _failed;
    private static long _muteCheckedAt = long.MinValue;
    private static bool _muted;
    private static readonly Random _random = new();

    public static void Attach(SettingsHub hub, NudgeScheduler scheduler)
    {
        _hub = hub;
        _scheduler = scheduler;
    }

    public static void Play(string name)
    {
        try
        {
            if (_hub is not { Settings.InteractionSounds: true } || Quiet()) return;
            if (!EnsureEngine() || !_buffers.TryGetValue(name, out var buffer)) return;
            var voice = _voices[_nextVoice];
            _nextVoice = (_nextVoice + 1) % VoiceCount;
            if (voice is null) return;
            voice.Stop();
            voice.FlushSourceBuffers();
            voice.SetFrequencyRatio(Pitches[_random.Next(Pitches.Length)], 0);
            voice.SetVolume(Volume);
            voice.SubmitSourceBuffer(buffer, null);
            voice.Start();
        }
        catch (Exception ex)
        {
            _failed = true;
            Logger.Log("bloop sound failed: " + ex.Message);
        }
    }

    private static bool Quiet()
    {
        if (_scheduler is { } s)
        {
            var now = DateTimeOffset.Now;
            foreach (var h in s.Holds)
                if (h.Until > now && h.Source is "dnd" or NudgeScheduler.CallSource) return true;
        }
        // The endpoint mute state is read at most once a second.
        var tick = Environment.TickCount64;
        if (tick - _muteCheckedAt > 1000)
        {
            _muteCheckedAt = tick;
            _muted = OutputMuted();
        }
        return _muted;
    }

    private static bool EnsureEngine()
    {
        if (_failed) return false;
        if (_engine is not null) return true;
        _engine = XAudio2.XAudio2Create();
        _master = _engine.CreateMasteringVoice();
        var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds");
        WaveFormat? format = null;
        foreach (var n in Names)
        {
            var path = Path.Combine(dir, n + ".wav");
            if (!File.Exists(path)) continue;
            var (fmt, pcm) = ReadWav(path);
            format ??= fmt;
            _buffers[n] = new AudioBuffer(pcm, BufferFlags.EndOfStream);
        }
        if (format is null) { _failed = true; return false; }
        for (var i = 0; i < VoiceCount; i++) _voices[i] = _engine.CreateSourceVoice(format);
        return true;
    }

    // Plain PCM WAV only (what tools/sounds/make_sounds.py writes).
    private static (WaveFormat, byte[]) ReadWav(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        if (new string(r.ReadChars(4)) != "RIFF") throw new InvalidDataException("not a WAV file");
        r.ReadInt32();
        if (new string(r.ReadChars(4)) != "WAVE") throw new InvalidDataException("not a WAV file");
        int rate = 22050, bits = 16, channels = 1;
        byte[]? data = null;
        while (r.BaseStream.Position + 8 <= r.BaseStream.Length && data is null)
        {
            var id = new string(r.ReadChars(4));
            var size = r.ReadInt32();
            if (id == "fmt ")
            {
                r.ReadInt16();
                channels = r.ReadInt16();
                rate = r.ReadInt32();
                r.ReadInt32();
                r.ReadInt16();
                bits = r.ReadInt16();
                r.BaseStream.Seek(size - 16, SeekOrigin.Current);
            }
            else if (id == "data") data = r.ReadBytes(size);
            else r.BaseStream.Seek(size, SeekOrigin.Current);
        }
        return (new WaveFormat(rate, bits, channels), data ?? throw new InvalidDataException("no audio data"));
    }

    // Default render endpoint mute (or volume at zero) through the Core Audio endpoint volume interface.
    private static bool OutputMuted()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(0, 1, out var device) != 0 || device is null) return false;
                try
                {
                    var iid = typeof(IAudioEndpointVolume).GUID;
                    if (device.Activate(ref iid, 23, IntPtr.Zero, out var obj) != 0 || obj is not IAudioEndpointVolume volume) return false;
                    try
                    {
                        volume.GetMute(out var mute);
                        volume.GetMasterVolumeLevelScalar(out var level);
                        return mute || level <= 0.001f;
                    }
                    finally { Marshal.ReleaseComObject(volume); }
                }
                finally { Marshal.ReleaseComObject(device); }
            }
            finally { Marshal.ReleaseComObject(enumerator); }
        }
        catch
        {
            return false;
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? obj);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
