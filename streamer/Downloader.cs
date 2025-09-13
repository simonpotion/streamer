using System.Globalization;
using System.Text.RegularExpressions;

namespace streamer;

public static class Downloader
{
    public static async Task<int> Run(string[] args)
    {
        // Ensure proper usage
     //startHourOffset 6 --duractionHours 

        var m3u8Url = Program.GetArg(args, "--url");
        var sh=Program.GetArg(args, "--startHourOffset");
        var du =Program.GetArg(args, "--durationHours");
        
        if (!double.TryParse(sh, out var startHourOffset) || startHourOffset < 0)
        {
            Console.WriteLine("Invalid start hour offset. Please provide a valid number.");
            return 0;
        }

        if (!double.TryParse(du, out var durationHours) || durationHours <= 0)
        {
            Console.WriteLine("Invalid duration. Please provide a valid number.");
            return 0;
        }

        var tsFiles = await DownloadPredictedSegmentsAsync(m3u8Url, startHourOffset, durationHours);

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

  static async Task<List<string>> DownloadPredictedSegmentsAsync(string m3u8Url, double startHourOffset, double durationHours)
    {
        using HttpClient client = new();
        var playlist = await client.GetStringAsync(m3u8Url);

        var lines = playlist.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var mediaSequence = 0;
        double segmentDuration = 0;
        var segmentPrefix = string.Empty;

        // Parse the media sequence, segment duration, and determine the segment prefix
        foreach (var line in lines)
        {
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE"))
            {
                var match = Regex.Match(line, @"#EXT-X-MEDIA-SEQUENCE:(\d+)");
                if (match.Success)
                {
                    mediaSequence = int.Parse(match.Groups[1].Value);
                }
            }
            else if (line.StartsWith("#EXTINF"))
            {
                var match = Regex.Match(line, @"#EXTINF:([\d\.]+),");
                if (match.Success && double.TryParse(match.Groups[1].Value, out var duration))
                {
                    segmentDuration = duration;
                }
            }
            else if (!line.StartsWith("#") && segmentPrefix == string.Empty)
            {
                // Extract the segment prefix from the first segment filename (without the numeric part)
                var match = Regex.Match(line, @"([a-zA-Z0-9_\-+=]+-\d+)-(\d+)\.ts");
                if (match.Success)
                {
                    segmentPrefix = match.Groups[1].Value;
                }
            }
        }

        if (mediaSequence == 0 || segmentDuration == 0 || string.IsNullOrEmpty(segmentPrefix))
        {
            Console.WriteLine("Failed to parse media sequence, segment duration, or segment prefix.");
            return new List<string>();
        }

        // Calculate the time range based on start hour offset and duration
        var startTime = DateTime.UtcNow.AddHours(-startHourOffset);
        var endTime = startTime.AddHours(durationHours);
        var nowTime = DateTime.UtcNow;

        
        // Calculate the number of segments in the desired time window
        var totalSeconds = (endTime - startTime).TotalSeconds;
        var offsetFromNow = (nowTime - startTime).TotalSeconds;

        var totalSegmentsToDownload = (int)(totalSeconds / segmentDuration);

        var segmentStart = (int)(offsetFromNow / segmentDuration);

        var downloadedSegments = new List<string>();

        // Calculate which segments fall within the specified time range
        for (var i = 0;i<totalSegmentsToDownload; i++)
        {
            var segmentNumber = (mediaSequence - segmentStart) + i;

            var tsFilename = $"{segmentPrefix}{segmentNumber}.ts";
            var tsUrl = $"{m3u8Url.Substring(0, m3u8Url.LastIndexOf('/'))}/{tsFilename}";
            var filePath = Path.Combine("Downloaded_TS_Files", $"{segmentNumber}.ts");

            if (!Directory.Exists("Downloaded_TS_Files"))
            {
                Directory.CreateDirectory("Downloaded_TS_Files");
            }

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

        // Write the file to disk
        await File.WriteAllBytesAsync(filePath, fileBytes);

        Console.WriteLine($"Downloaded: {filePath}");
    }
}