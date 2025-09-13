namespace streamer;

public record Segment(Uri Uri, double Duration, long Seq,DateTimeOffset Ts);

public struct Directives
{
    public bool IsLive;
    public int TargetDurationSeconds;
    public long MediaSequence;
}
