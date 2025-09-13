using System.Globalization;
using System.Net;

namespace streamer;


public static class Common
{
    public static string MakeSafeFileName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s;
    }

    public static Directives ParsePlaylistDirectives(string[] lines)
    {
        Directives d = new Directives();
        d.IsLive = true;
        foreach (var line in lines)
        {
        
            if (line.StartsWith("#EXT-X-TARGETDURATION:"))
            {
                var v = line.Split(':', 2)[1].Trim();
                if (int.TryParse(v, out var td)) d.TargetDurationSeconds = td;
            }
            else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:"))
            {
                var v = line.Split(':', 2)[1].Trim();
                if (long.TryParse(v, out var seq)) d.MediaSequence = seq;
            }
            else if (line.StartsWith("#EXT-X-ENDLIST"))
            {
                d.IsLive = false;
            }
        }

        return d;
    }
    public static List<Segment> ParseSegments(long mediaSequence,string[] lines, Uri baseUri)
    {
        var list = new List<Segment>();
        double? pendingDur = null;
        var seq = mediaSequence;
        DateTimeOffset? pendingPdt = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("#EXTINF:"))
            {
                var val = line.Split(':', 2)[1];
                var comma = val.IndexOf(',');
                var durStr = comma >= 0 ? val[..comma] : val;
                if (double.TryParse(durStr, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var d))
                {
                    pendingDur = d;
                }
            }
            else if (line.StartsWith("#EXT-X-PROGRAM-DATE-TIME:", StringComparison.Ordinal))
            {
                var iso = line.Split(':',2)[1].Trim();
                if (DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                    pendingPdt = dto;
            }
            else if (!line.StartsWith("#"))
            {
                var when =
                    pendingPdt.HasValue ? pendingPdt.Value : DateTimeOffset.UtcNow;

                // Segment URI
                var u = ResolveUri(baseUri, WebUtility.UrlDecode(line.Trim()));
                list.Add(new Segment(u, pendingDur ?? 0, seq,when));
                seq++;
                pendingDur = null;
            }
        }

        return list;
    }
    
    public static Uri ResolveUri(Uri baseUri, string maybeRelative)
    {
        if (Uri.TryCreate(maybeRelative, UriKind.Absolute, out var abs)) return abs;
        return new Uri(baseUri, maybeRelative);
    }

}