
namespace streamer;

public static class RewindDownloader
{
    public static async Task<int> Run(string[] args)
    {
        var baseUrl = Program.GetArg(args, "--url");
        var startHourOffsetArg=Program.GetArg(args, "--startHourOffset");
        var durationArg =Program.GetArg(args, "--durationHours");
        var outputDirectory = Program.GetArg(args, "--out") ?? "./out";
        
          
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            Console.WriteLine("Missing base m3u8 url.");
            return 0;
        }
        if (!double.TryParse(startHourOffsetArg, out var startHourOffset) || startHourOffset < 0)
        {
            Console.WriteLine("Invalid start hour offset. Please provide a valid number.");
            return 0;
        }

        if (!double.TryParse(durationArg, out var durationHours) || durationHours <= 0)
        {
            Console.WriteLine("Invalid duration. Please provide a valid number.");
            return 0;
        }
        
        if (!Directory.Exists(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var tsFiles = await DownloadPredictedSegmentsAsync(outputDirectory,baseUrl, startHourOffset, durationHours);

        if (tsFiles != null && tsFiles.Count > 0)
        {
            Console.WriteLine($"Downloaded {tsFiles.Count} segments.");
        }
        else
        {
            Console.WriteLine("No segments downloaded.");
        }

        return 1;
    }

  static async Task<List<string>> DownloadPredictedSegmentsAsync(string outputDirectory,string m3u8Url, double startHourOffset, double durationHours)
    {
        using HttpClient client = new();
        var playlist = await client.GetStringAsync(m3u8Url);

        var lines = playlist.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        
        var directives = Common.ParsePlaylistDirectives(lines);

        var segments = Common.ParseSegments(directives.MediaSequence, lines,new Uri(m3u8Url));
        
        if (segments.Count == 0 )
        {
            Console.WriteLine("Failed to parse media sequence, segment duration, or segment prefix.");
            return [];
        }

        var startTime = DateTime.UtcNow.AddHours(-startHourOffset);
        
        
        var endTime = startTime.AddHours(durationHours);
        
        Console.WriteLine($"Assuming time of {startTime} to {endTime}");
        
        var nowTime = DateTime.UtcNow;
        
        var totalSeconds = (endTime - startTime).TotalSeconds;
        var offsetFromNow = (nowTime - startTime).TotalSeconds;

        var totalSegmentsToDownload = (int)(totalSeconds / segments[0].Duration);

        var segmentStart = directives.MediaSequence - (int)(offsetFromNow / segments[0].Duration);

        Console.WriteLine($"Starting segment {segmentStart}, downloading {totalSegmentsToDownload} segments total");

        var downloadedSegments = new List<string>();

        var baseSegmentUrl = segments[0].Uri.ToString();
        var baseSequence = segments[0].Seq.ToString();

        for (var i = 0;i<totalSegmentsToDownload; i++)
        {
            var segmentNumber = segmentStart + i;
            var tsUrl = baseSegmentUrl.Replace(baseSequence, segmentNumber.ToString());
            
            var filePath = Path.Combine(outputDirectory, Common.MakeSafeFileName($"{segmentNumber:D10}.ts"));
            
            if (!File.Exists(filePath))
            {
                await DownloadFileAsync(client, tsUrl, filePath);
                await Task.Delay(2000);
            }

            downloadedSegments.Add(filePath);
        }

        return downloadedSegments;
    }


    static async Task DownloadFileAsync(HttpClient client, string fileUrl, string filePath)
    {
        using var response = await client.GetAsync(fileUrl);
        response.EnsureSuccessStatusCode();

        var fileBytes = await response.Content.ReadAsByteArrayAsync();

        await File.WriteAllBytesAsync(filePath, fileBytes);

        Console.WriteLine($"Downloaded: {filePath}");
    }
}