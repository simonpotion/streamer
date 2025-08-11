using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var url = GetArg(args, "--url");
        var outDir = GetArg(args, "--out") ?? "./out";
        var intervalArg = GetArg(args, "--interval");
        var maxSegmentsArg = GetArg(args, "--maxSegments");
        var durationArg = GetArg(args, "--durationSeconds");

        if (string.IsNullOrWhiteSpace(url))
        {
            Console.WriteLine("Usage: --url <m3u8> [--out <dir>] [--interval <sec>] [--maxSegments N] [--durationSeconds S]");
            return 1;
        }

        Directory.CreateDirectory(outDir);

        using var http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        });
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36");
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            var grabber = new HlsGrabber(http, new Uri(url), outDir)
            {
                PollIntervalOverrideSeconds = intervalArg is null ? null : int.Parse(intervalArg),
                MaxSegments = maxSegmentsArg is null ? null : int.Parse(maxSegmentsArg),
                MaxDurationSeconds = durationArg is null ? null : int.Parse(durationArg)
            };
            await grabber.RunAsync(cts.Token);
            Console.WriteLine("Done.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Canceled.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 2;
        }
    }

    static string? GetArg(string[] args, string name)
    {
        var idx = Array.IndexOf(args, name);
        if (idx >= 0 && idx + 1 < args.Length) return args[idx + 1];
        return null;
    }
}

public class HlsGrabber
{
    private readonly HttpClient _http;
    private Uri _playlistUri;
    private readonly string _outDir;

    // State
    private readonly HashSet<string> _seenSegments = new(StringComparer.Ordinal);
    private long _mediaSequence = 0;
    private bool _isLive = true;
    private int? _targetDurationSeconds;
    private DateTime _start = DateTime.UtcNow;
    private int _downloaded = 0;

    public int? PollIntervalOverrideSeconds { get; set; }
    public int? MaxSegments { get; set; }
    public int? MaxDurationSeconds { get; set; }

    public HlsGrabber(HttpClient http, Uri playlistUri, string outDir)
    {
        _http = http;
        _playlistUri = playlistUri;
        _outDir = outDir;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // If master playlist, resolve to the first (or best) media playlist
        _playlistUri = await EnsureMediaPlaylistAsync(_playlistUri, ct);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var text = await GetStringAsync(_playlistUri, ct);
            if (!text.StartsWith("#EXTM3U"))
                throw new InvalidOperationException("Not a valid M3U8 playlist.");

            var lines = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            ParsePlaylistDirectives(lines);

            var segments = ParseSegments(lines, baseUri: _playlistUri);
            if (segments.Count == 0 && !_isLive)
                break;

            // Download only unseen segments
            foreach (var seg in segments)
            {
                if (_seenSegments.Contains(seg.Uri.AbsoluteUri))
                    continue;

                await DownloadSegmentAsync(seg, ct);
                _seenSegments.Add(seg.Uri.AbsoluteUri);
                _downloaded++;

                if (MaxSegments is not null && _downloaded >= MaxSegments) return;
                if (MaxDurationSeconds is not null &&
                    (DateTime.UtcNow - _start).TotalSeconds >= MaxDurationSeconds) return;
            }

            if (!_isLive) break;

            // Poll for new segments
            var delay = PollIntervalOverrideSeconds
                        ?? Math.Max(1, (_targetDurationSeconds ?? 6) / 2);
            await Task.Delay(TimeSpan.FromSeconds(delay), ct);
        }
    }

    private async Task<Uri> EnsureMediaPlaylistAsync(Uri uri, CancellationToken ct)
    {
        var text = await GetStringAsync(uri, ct);
        if (!text.StartsWith("#EXTM3U"))
            throw new InvalidOperationException("Not a valid M3U8.");

        // Master playlist if it has EXT-X-STREAM-INF or EXT-X-MEDIA without segments
        if (text.Contains("#EXT-X-STREAM-INF") || (text.Contains("#EXT-X-MEDIA") && !text.Contains("#EXTINF:")))
        {
            var lines = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            // Pick the first variant; you could add logic to pick highest BANDWIDTH
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("#EXT-X-STREAM-INF"))
                {
                    // Next non-comment line is the URI
                    for (int j = i + 1; j < lines.Length; j++)
                    {
                        if (lines[j].StartsWith("#")) continue;
                        var mediaUri = ResolveUri(uri, lines[j]);
                        return mediaUri;
                    }
                }
            }
            throw new InvalidOperationException("Master playlist had no media variants.");
        }

        return uri; // already media playlist
    }

    private void ParsePlaylistDirectives(string[] lines)
    {
        _isLive = true;
        foreach (var line in lines)
        {
            if (line.StartsWith("#EXT-X-TARGETDURATION:"))
            {
                var v = line.Split(':', 2)[1].Trim();
                if (int.TryParse(v, out var td)) _targetDurationSeconds = td;
            }
            else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:"))
            {
                var v = line.Split(':', 2)[1].Trim();
                if (long.TryParse(v, out var seq)) _mediaSequence = seq;
            }
            else if (line.StartsWith("#EXT-X-ENDLIST"))
            {
                _isLive = false;
            }
        }
    }

    private static Uri ResolveUri(Uri baseUri, string maybeRelative)
    {
        if (Uri.TryCreate(maybeRelative, UriKind.Absolute, out var abs)) return abs;
        return new Uri(baseUri, maybeRelative);
    }

    private record Segment(Uri Uri, double Duration, long Seq,DateTimeOffset Ts);

    private List<Segment> ParseSegments(string[] lines, Uri baseUri)
    {
        var list = new List<Segment>();
        double? pendingDur = null;
        long seq = _mediaSequence;
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
                DateTimeOffset when =
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

    private async Task DownloadSegmentAsync(Segment seg, CancellationToken ct)
    {
        var name = MakeSafeFileName($"{seg.Seq:D10}.ts");
        
        var local = TimeZoneInfo.ConvertTime(seg.Ts, TimeZoneInfo.Utc);
        string subdir = Path.Combine(
            _outDir,
            local.Year.ToString("D4", CultureInfo.InvariantCulture),
            local.Month.ToString("D2", CultureInfo.InvariantCulture),
            local.Day.ToString("D2", CultureInfo.InvariantCulture),
            local.Hour.ToString("D2", CultureInfo.InvariantCulture)
        );
        Directory.CreateDirectory(subdir);
        
        var path = Path.Combine(subdir, name);

        Console.WriteLine($"Downloading {seg.Uri} -> {name} (dur ~{seg.Duration:0.###}s)");
        using var resp = await _http.GetAsync(seg.Uri, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, useAsync: true);
        await resp.Content.CopyToAsync(fs, ct);
    }

    private static string MakeSafeFileName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s;
    }

    private async Task<string> GetStringAsync(Uri uri, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        req.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        // many playlists are UTF-8; fall back to ISO-8859-1 if needed
        return Encoding.UTF8.GetString(bytes);
    }
}
