namespace FlyleafLib.Interfaces;

public class AudioEndpoint
{
    public string Id { get; set; }
    public string Name { get; set; }

    public override string ToString() => Name;
}
