using System;
using System.Collections.Generic;
using FlyleafLib.MediaFramework.MediaFrame;
using FlyleafLib.MediaFramework.MediaContext;
using FlyleafLib.MediaFramework.MediaStream;
using FlyleafLib;
using FlyleafLib.Interfaces;

namespace FlyleafLib.MediaPlayer;

public class Audio : NotifyPropertyChanged
{
    private static readonly Dictionary<AudioOutputMode, Func<LogHandler, IAudioOutput>> customOutputs = new();

    public static void RegisterOutput(AudioOutputMode mode, Func<LogHandler, IAudioOutput> factory)
    {
        lock (customOutputs)
        {
            customOutputs[mode] = factory;
        }
    }
    // TODO: Add Volume/Mute to Config.Audio (consider allowing saving and separate config field (flags) whether to load those?)

    public event EventHandler<AudioFrame> SamplesAdded;

    #region Properties
    /// <summary>
    /// Embedded Streams
    /// </summary>
    public ObservableCollection<AudioStream>
                    Streams         => decoder?.VideoDemuxer.AudioStreams; // TBR: We miss AudioDemuxer embedded streams

    public int      StreamIndex     { get => streamIndex;       internal set => Set(ref _StreamIndex, value); }
    int _StreamIndex, streamIndex = -1;

    /// <summary>
    /// Whether the input has audio and it is configured
    /// </summary>
    public bool     IsOpened        { get => isOpened;          internal set => Set(ref _IsOpened, value); }
    internal bool   _IsOpened, isOpened;

    public string   Codec           { get => codec;             internal set => Set(ref _Codec, value); }
    internal string _Codec, codec;

    ///// <summary>
    ///// Audio bitrate (Kbps)
    ///// </summary>
    public double   BitRate         { get => bitRate;           internal set => Set(ref _BitRate, value); }
    internal double _BitRate, bitRate;

    public int      Bits            { get => bits;              internal set => Set(ref _Bits, value); }
    internal int    _Bits, bits;

    public int      Channels        { get => channels;          internal set => Set(ref _Channels, value); }
    internal int    _Channels, channels;

    /// <summary>
    /// Audio player's channels out (currently 2 channels supported only)
    /// </summary>
    public int ChannelsOut => Config.Audio.Channels > 0 ? Config.Audio.Channels : (player.AudioDecoder != null ? player.AudioDecoder.AOutChannels : 2);

    public string   ChannelLayout   { get => channelLayout;     internal set => Set(ref _ChannelLayout, value); }
    internal string _ChannelLayout, channelLayout;

    ///// <summary>
    ///// Total Dropped Frames
    ///// </summary>
    public int      FramesDropped   { get => framesDropped;     internal set => Set(ref _FramesDropped, value); }
    internal int    _FramesDropped, framesDropped;

    public int      FramesDisplayed { get => framesDisplayed;   internal set => Set(ref _FramesDisplayed, value); }
    internal int    _FramesDisplayed, framesDisplayed;

    public string   SampleFormat    { get => sampleFormat;      internal set => Set(ref _SampleFormat, value); }
    internal string _SampleFormat, sampleFormat;

    /// <summary>
    /// Audio sample rate (in/out)
    /// </summary>
    public int      SampleRate      { get => sampleRate;        internal set => Set(ref _SampleRate, value); }
    internal int    _SampleRate, sampleRate;

    /// <summary>
    /// Audio player's volume / amplifier (valid values 0 - no upper limit)
    /// </summary>
    public int Volume
    {
        get
        {
            lock (locker)
                return Output == null || Mute ? _Volume : Output.Volume;
        }
        set
        {
            if (value > Config.Audio.VolumeMax || value < 0)
                return;

            if (value == 0)
                Mute = true;
            else if (Mute)
            {
                _Volume = value;
                Mute = false;
            }
            else
            {
                if (Output != null)
                    Output.Volume = value;
            }

            Set(ref _Volume, value, false);
        }
    }
    int _Volume;

    /// <summary>
    /// Audio player's mute
    /// </summary>
    public bool Mute
    {
        get => mute;
        set
        {
            lock (locker)
            {
                if (Output != null)
                    Output.Mute = value;
            }

            Set(ref mute, value, false);
        }
    }

    public float MasterVolume
    {
        get => Output?.MasterVolume ?? 1.0f;
        set { if (Output != null) Output.MasterVolume = value; }
    }
    private bool mute = false;

    /// <summary>
    /// <para>Audio player's current device (available devices can be found on <see cref="Engine.Audio"/>)/></para>
    /// </summary>
    public AudioEndpoint Device
    {
        get => _Device;
        set
        {
            if ((value == null && _Device == Engine.Audio.DefaultDevice) || value == _Device)
                return;

            _Device = value ?? Engine.Audio.DefaultDevice;
            Initialize();
            RaiseUI(nameof(Device));
        }
    }
    internal AudioEndpoint _Device = Engine.Audio.DefaultDevice;
    #endregion

    #region Declaration
    public Player Player => player;

    Player                  player;
    Config                  Config => player.Config;
    DecoderContext          decoder => player?.decoder;

    Action                  uiAction;
    internal readonly object
                            locker = new();

    public IAudioOutput     Output { get; private set; }
    public AudioOutputMode CurrentOutputMode => currentMode;
    private AudioOutputMode currentMode;

    internal double         Timebase;
    internal ulong          submittedSamples;
    int                     curSampleRate = -1;
    #endregion

    public Audio(Player player)
    {
        this.player = player;

        uiAction = () =>
        {
            StreamIndex     = streamIndex;
            IsOpened        = isOpened;
            Codec           = codec;
            BitRate         = bitRate;
            Bits            = bits;
            Channels        = channels;
            ChannelLayout   = channelLayout;
            SampleFormat    = sampleFormat;
            SampleRate      = sampleRate;

            FramesDisplayed = framesDisplayed;
            FramesDropped   = framesDropped;
        };

        Volume = Config.Audio.VolumeMax / 2;
    }


    internal void Initialize()
    {
        lock (locker)
        {
            if (Engine.Audio.Failed)
            {
                Config.Audio.Enabled = false;
                return;
            }

            if (!isOpened || sampleRate <= 0)
                return;

            // Check if we need to switch/create output provider
            if (Output == null || currentMode != Config.Audio.OutputMode)
            {
                Dispose();
                currentMode = Config.Audio.OutputMode;

                lock (customOutputs)
                {
                    if (customOutputs.TryGetValue(currentMode, out var factory))
                    {
                        Output = factory(player.Log);
                    }
                    else
                    {
                        player.Log.Warn($"[Audio] Mode {currentMode} not registered. Falling back to default if available.");

                        // Default to Shared if not found
                        if (currentMode != AudioOutputMode.Shared && customOutputs.TryGetValue(AudioOutputMode.Shared, out var sharedFactory))
                        {
                            currentMode = AudioOutputMode.Shared;
                            Output = sharedFactory(player.Log);
                        }
                        else
                        {
                            player.Log.Error($"[Audio] Failed to find a valid audio output for {currentMode}. Audio will be disabled.");
                            Config.Audio.Enabled = false;
                            return;
                        }
                    }
                }
            }

            int targetChannels = ChannelsOut;
            if (targetChannels <= 0) targetChannels = 2;

            Timebase = 1000 * 10000.0 / sampleRate;

            player.Log.Info($"Initializing audio at {sampleRate}Hz ({targetChannels} Channels) ({Device.Id}:{Device.Name}) [{currentMode}]");

            try
            {
                Output.Volume = _Volume;
                Output.Mute = mute;
                Output.MasterVolume = Config.Audio.VolumeMax / 100.0f;
                Output.Initialize(sampleRate, targetChannels, player.Config.Audio.BitDepth, Device);
                
                curSampleRate = sampleRate;
            }
            catch (Exception e)
            {
                player.Log.Error($"[Audio] {currentMode} initialization failed: {e.Message}");
                
                if (currentMode != AudioOutputMode.Shared)
                {
                    player.Log.Info("[Audio] Falling back to Shared mode...");
                    currentMode = AudioOutputMode.Shared;
                    Output?.Dispose();

                    lock (customOutputs)
                    {
                        if (customOutputs.TryGetValue(AudioOutputMode.Shared, out var factory))
                        {
                            Output = factory(player.Log);
                            try
                            {
                                Output.Volume = _Volume;
                                Output.Mute = mute;
                                Output.MasterVolume = Config.Audio.VolumeMax / 100.0f;
                                // Use default device for fallback to ensure compatibility
                                Output.Initialize(sampleRate, targetChannels, player.Config.Audio.BitDepth, Engine.Audio.DefaultDevice);
                                curSampleRate = sampleRate;
                                Timebase = 1000 * 10000.0 / sampleRate;
                                if (player.AudioDecoder != null) player.AudioDecoder.SetupFiltersOrSwr();
                            }
                            catch (Exception ex)
                            {
                                player.Log.Error($"[Audio] Shared fallback also failed ({ex.Message})");
                                Config.Audio.Enabled = false;
                            }
                        }
                        else
                        {
                            player.Log.Error("[Audio] Shared mode not registered for fallback. Audio will be disabled.");
                            Config.Audio.Enabled = false;
                        }
                    }
                }
                else
                {
                    Config.Audio.Enabled = false;
                }
            }
        }
    }
    internal void Dispose()
    {
        lock (locker)
        {
            if (Output == null)
                return;

            Output.Dispose();
            Output = null;
        }
    }

    // TBR: Very rarely could crash the app on audio device change while playing? Requires two locks (Audio's locker and aFrame)
    // The process was terminated due to an internal error in the .NET Runtime at IP 00007FFA6725DA03 (00007FFA67090000) with exit code c0000005.
    [System.Security.SecurityCritical]
    internal void AddSamples(AudioFrame aFrame)
    {
        lock (locker) // required for submittedSamples only? (ClearBuffer() can be called during audio decocder circular buffer reallocation)
        {
            try
            {
                if (CanTrace)
                    player.Log.Trace($"[A] Presenting {TicksToTime(player.aFrame.Timestamp)}");

                framesDisplayed++;

                SamplesAdded?.Invoke(this, aFrame);

                if (Output != null)
                    Output.AddSamples(aFrame.dataPtr, aFrame.dataLen);
            }
            catch (Exception e) // Happens on audio device changed/removed
            {
                if (CanDebug)
                    player.Log.Debug($"[Audio] Submitting samples failed ({e.Message})");

                ClearBuffer();
            }
        }
    }
    internal long GetBufferedDuration() { lock (locker) { return Output?.GetBufferedDuration() ?? 0; } }
    internal long GetDeviceDelay()
    {
        lock (locker)
        {
            return Output?.GetDeviceDelay() ?? 0;
        }
    }
    internal void ClearBuffer()
    {
        lock (locker)
        {
            if (Output != null)
                Output.ClearBuffer();
        }
    }

    internal void Reset()
    {
        streamIndex     = -1;
        codec           = null;
        bitRate         = 0;
        bits            = 0;
        channels        = 0;
        channelLayout   = null;
        sampleFormat    = null;
        isOpened        = false;

        ClearBuffer();
        player.UIAdd(uiAction);
    }
    internal void Refresh(bool fromCodec = false)
    {
        if (decoder.AudioStream == null)
        {
            Reset();
            return;
        }

        streamIndex     = decoder.AudioStream.StreamIndex;
        codec           = decoder.AudioStream.Codec;
        bits            = decoder.AudioStream.Bits;
        channels        = decoder.AudioStream.Channels;
        channelLayout   = decoder.AudioStream.ChannelLayoutStr;
        sampleFormat    = decoder.AudioStream.SampleFormatStr;
        isOpened        =!decoder.AudioDecoder.Disposed;
        sampleRate      = decoder.AudioStream.SampleRate;

        framesDisplayed = 0;
        framesDropped   = 0;

        Timebase = 1000 * 10000.0 / sampleRate;

        if (fromCodec)
        {
            if (sampleRate <= 0)
            {   // Possible with AllowFindStreamInfo = false
                Disable();
                return;
            }

            if (sampleRate != curSampleRate || (Output != null && currentMode != Config.Audio.OutputMode))
                Initialize();
        }
        
        player.UIAdd(uiAction);
    }
    internal void Enable()
    {
        bool wasPlaying = player.IsPlaying;

        decoder.OpenSuggestedAudio();

        player.ReSync(decoder.AudioStream, (int) (player.curTime / 10000), true);

        Refresh();
        player.UIAll();

        if (wasPlaying || Config.Player.AutoPlay)
            player.Play();
    }
    internal void Disable()
    {
        if (!IsOpened)
            return;

        decoder.CloseAudio();
        player.UpdateMainDemuxer(); // possible in Reset (consider close event)?
        if (!player.Video.IsOpened)
        {
            player.canPlay = false;
            player.UIAdd(() => player.CanPlay = player.CanPlay);
        }

        Reset();
        player.UIAll();
    }
}
