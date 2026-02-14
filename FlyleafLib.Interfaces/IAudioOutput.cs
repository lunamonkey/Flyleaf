namespace FlyleafLib.Interfaces;

public interface IAudioOutput : IDisposable
{
    void Initialize(int sampleRate, int channels, AudioBitDepth bitDepth, AudioEndpoint device);
    void AddSamples(IntPtr dataPtr, int dataLen);
    void ClearBuffer();
    long GetBufferedDuration();
    long GetDeviceDelay();

    int Volume { get; set; }
    bool Mute { get; set; }
    float MasterVolume { get; set; }
}
