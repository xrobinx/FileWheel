using System.Globalization;
using System.Text.RegularExpressions;

namespace FileWheel.Core;

public sealed record MediaInfo(double Duration, bool HasVideo, bool HasAudio, int Width, int Height, string Raw);

/// <summary>Audio and video work through the bundled ffmpeg.</summary>
public static partial class Media
{
    static string Engine => Formats.Ffmpeg ?? throw new IOException("The media engine (ffmpeg) is missing from this install.");

    public static MediaInfo Probe(string path)
    {
        string raw;
        try
        {
            // "ffmpeg -i" with no output exits with an error but prints the stream description.
            raw = Processes.Run(Engine, ["-hide_banner", "-i", path, "-f", "null", "-t", "0", "-"], CancellationToken.None, TimeSpan.FromSeconds(20));
        }
        catch (IOException e) { raw = e.Message; }
        double duration = 0;
        var d = DurationRegex().Match(raw);
        if (d.Success) duration = TimeSpan.ParseExact(d.Groups[1].Value, @"hh\:mm\:ss\.ff", CultureInfo.InvariantCulture).TotalSeconds;
        var v = VideoRegex().Match(raw);
        int w = 0, h = 0;
        if (v.Success) { w = int.Parse(v.Groups[1].Value); h = int.Parse(v.Groups[2].Value); }
        return new MediaInfo(duration, raw.Contains("Video:"), raw.Contains("Audio:"), w, h, raw);
    }

    public static string Convert(string source, string target, Action<double> progress, CancellationToken ct)
    {
        var info = Probe(source);
        var args = new List<string>();
        bool audioOnly = target is "mp3" or "wav" or "m4a" or "aac" or "flac" or "ogg" or "opus" or "aiff" or "wma";
        if (audioOnly)
        {
            if (!info.HasAudio) throw new IOException("This file has no audio track.");
            args.Add("-vn");
            args.AddRange(target switch
            {
                "mp3" => ["-c:a", "libmp3lame", "-q:a", "2"],
                "wav" => ["-c:a", "pcm_s16le"],
                "m4a" or "aac" => ["-c:a", "aac", "-b:a", "192k"],
                "wma" => ["-c:a", "wmav2", "-b:a", "192k"],
                "flac" => ["-c:a", "flac"],
                "ogg" => ["-c:a", "libvorbis", "-q:a", "5"],
                "opus" => ["-c:a", "libopus", "-b:a", "128k"],
                _ => ["-c:a", "pcm_s16be"],
            });
        }
        else
        {
            if (!info.HasVideo) throw new IOException("This file has no video track.");
            args.AddRange(target switch
            {
                "webm" => ["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4", "-c:a", "libopus", "-b:a", "128k"],
                "avi" => ["-c:v", "mpeg4", "-q:v", "3", "-c:a", "libmp3lame", "-q:a", "3"],
                "wmv" => ["-c:v", "wmv2", "-q:v", "3", "-c:a", "wmav2", "-b:a", "160k"],
                "gif" => ["-vf", "fps=12,scale='min(720,iw)':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=sierra2_4a", "-loop", "0", "-an"],
                _ => ["-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p", "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-c:a", "aac", "-b:a", "160k"],
            });
            if (target is "mp4" or "mov") args.AddRange(["-movflags", "+faststart"]);
        }
        return Encode(source, "", target, args, info.Duration, progress, ct);
    }

    /// <summary>Runs ffmpeg with <paramref name="codecArgs"/> between input and output, reporting progress.</summary>
    public static string Encode(string source, string suffix, string ext, IEnumerable<string> codecArgs, double duration, Action<double> progress, CancellationToken ct, IEnumerable<string>? inputArgs = null)
    {
        return Output.Save(source, suffix, ext, temp =>
        {
            var args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-progress", "pipe:1", "-nostats" };
            if (inputArgs != null) args.AddRange(inputArgs);
            args.AddRange(["-i", source]);
            args.AddRange(codecArgs);
            args.Add(temp);
            Processes.Run(Engine, args, ct, TimeSpan.FromHours(2), line =>
            {
                if (duration <= 0) return;
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(12), out var us))
                    progress(Math.Clamp(us / 1_000_000.0 / duration, 0, 1));
            });
        });
    }

    public static string Compress(string source, int quality, Action<double> progress, CancellationToken ct)
    {
        var info = Probe(source);
        var ext = Formats.Ext(source);
        if (!info.HasVideo)
        {
            // quality 0..100 → 64..256 kbps
            int kbps = 64 + (int)(quality / 100.0 * 192);
            var target = ext is "mp3" or "m4a" or "ogg" or "opus" ? ext : "m4a";
            string[] codec = target switch
            {
                "mp3" => ["-c:a", "libmp3lame", "-b:a", kbps + "k"],
                "ogg" => ["-c:a", "libvorbis", "-b:a", kbps + "k"],
                "opus" => ["-c:a", "libopus", "-b:a", kbps + "k"],
                _ => ["-c:a", "aac", "-b:a", kbps + "k"],
            };
            return Encode(source, " Compressed", target, ["-vn", .. codec], info.Duration, progress, ct);
        }
        // quality 100 → crf 20, quality 0 → crf 36
        int crf = 36 - (int)Math.Round(quality / 100.0 * 16);
        var outExt = ext is "mov" or "mkv" ? ext : "mp4";
        return Encode(source, " Compressed", outExt,
            ["-c:v", "libx264", "-preset", "medium", "-crf", crf.ToString(), "-pix_fmt", "yuv420p", "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart"],
            info.Duration, progress, ct);
    }

    public static string Trim(string source, double start, double end, Action<double> progress, CancellationToken ct)
    {
        var ext = Formats.Ext(source);
        double length = Math.Max(0.1, end - start);
        string[] input = ["-ss", start.ToString("0.###", CultureInfo.InvariantCulture)];
        string[] codec = ["-t", length.ToString("0.###", CultureInfo.InvariantCulture)];
        // Re-encode so cuts are frame-accurate.
        var info = Probe(source);
        string[] encode = info.HasVideo
            ? ["-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k"]
            : ext switch { "mp3" => ["-c:a", "libmp3lame", "-q:a", "2"], "wav" => ["-c:a", "pcm_s16le"], "flac" => ["-c:a", "flac"], _ => ["-c:a", "aac", "-b:a", "192k"] };
        var outExt = info.HasVideo ? (ext is "mov" or "mkv" ? ext : "mp4") : (ext is "mp3" or "wav" or "flac" ? ext : "m4a");
        return Encode(source, " Trimmed", outExt, [.. codec, .. encode], length, progress, ct, input);
    }

    public static string Mute(string source, Action<double> progress, CancellationToken ct)
    {
        var info = Probe(source);
        return Encode(source, " Muted", Formats.Ext(source), ["-an", "-c:v", "copy"], info.Duration, progress, ct);
    }

    public static string StripMetadata(string source, CancellationToken ct) =>
        Encode(source, " Clean", Formats.Ext(source), ["-map", "0", "-map_metadata", "-1", "-c", "copy"], 0, _ => { }, ct);

    // ---------- Helpers for the tools below ----------

    static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Containers that hold H.264 + AAC keep their format; anything else becomes MP4.</summary>
    static string VideoOut(string ext) => ext is "mp4" or "mov" or "mkv" or "m4v" ? ext : "mp4";

    static readonly string[] VideoCodec = ["-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p"];

    /// <summary>The audio encoder that keeps a file in its own format.</summary>
    static string[] AudioCodec(string ext) => ext switch
    {
        "mp3" => ["-c:a", "libmp3lame", "-q:a", "2"],
        "wav" => ["-c:a", "pcm_s16le"],
        "flac" => ["-c:a", "flac"],
        "ogg" => ["-c:a", "libvorbis", "-q:a", "6"],
        "opus" => ["-c:a", "libopus", "-b:a", "160k"],
        "wma" => ["-c:a", "wmav2", "-b:a", "192k"],
        "aiff" => ["-c:a", "pcm_s16be"],
        _ => ["-c:a", "aac", "-b:a", "192k"],
    };

    static string AudioOut(string ext) => ext is "mp3" or "wav" or "flac" or "ogg" or "opus" or "wma" or "aiff" or "aac" ? ext : "m4a";

    /// <summary>Writes the frame at <paramref name="seconds"/> as a PNG.</summary>
    public static void Frame(string source, double seconds, string png)
    {
        Processes.Run(Engine, ["-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-ss", F(Math.Max(0, seconds)), "-i", source, "-frames:v", "1", png],
            CancellationToken.None, TimeSpan.FromMinutes(1));
        if (!File.Exists(png)) throw new IOException("Couldn't read a frame from this video.");
    }

    // ---------- Video tools ----------

    /// <summary>"Snapshot": the frame at a moment, as a full-resolution PNG.</summary>
    public static string Snapshot(string source, double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return Output.Save(source, $" Frame {(int)t.TotalMinutes}m{t.Seconds:00}s", "png", temp => Frame(source, seconds, temp));
    }

    /// <summary>"Speed": plays faster or slower; the sound keeps its pitch.</summary>
    public static string Speed(string source, double factor, Action<double> progress, CancellationToken ct)
    {
        var info = Probe(source);
        if (!info.HasVideo) throw new IOException("This file has no video track.");
        var tempo = new List<string>();
        double rest = factor;
        while (rest > 2) { tempo.Add("atempo=2"); rest /= 2; }
        while (rest < 0.5) { tempo.Add("atempo=0.5"); rest /= 0.5; }
        tempo.Add("atempo=" + F(rest));
        var graph = $"[0:v]setpts=PTS/{F(factor)}[v]" + (info.HasAudio ? $";[0:a]{string.Join(",", tempo)}[a]" : "");
        var args = new List<string> { "-filter_complex", graph, "-map", "[v]" };
        if (info.HasAudio) args.AddRange(["-map", "[a]", "-c:a", "aac", "-b:a", "192k"]);
        args.AddRange(VideoCodec);
        args.AddRange(["-movflags", "+faststart"]);
        return Encode(source, $" {F(factor)}x", VideoOut(Formats.Ext(source)), args, info.Duration / factor, progress, ct);
    }

    /// <summary>"Crop": keeps the chosen part of the picture for the whole video.</summary>
    public static string Crop(string source, NormRect r, Action<double> progress, CancellationToken ct)
    {
        var info = Probe(source);
        if (info.Width <= 0) throw new IOException("Couldn't read the video's size.");
        var (x, y, w, h) = Pixels(r, info);
        return Encode(source, " Cropped", VideoOut(Formats.Ext(source)),
            ["-vf", $"crop={w}:{h}:{x}:{y}", .. VideoCodec, "-c:a", "copy", "-movflags", "+faststart"], info.Duration, progress, ct);
    }

    /// <summary>A normalized rectangle in even pixel numbers (what H.264 needs).</summary>
    static (int X, int Y, int W, int H) Pixels(NormRect r, MediaInfo info)
    {
        int Even(double v) => Math.Max(0, (int)Math.Round(v / 2) * 2);
        int x = Even(r.X * info.Width), y = Even(r.Y * info.Height);
        int w = Math.Max(2, Math.Min(Even(r.W * info.Width), (info.Width - x) / 2 * 2));
        int h = Math.Max(2, Math.Min(Even(r.H * info.Height), (info.Height - y) / 2 * 2));
        return (x, y, w, h);
    }

    /// <summary>"Split": cuts the video in two at <paramref name="seconds"/> ("Part 1", "Part 2"), frame-accurately.</summary>
    public static List<string> Split(string source, double seconds, Action<double> progress, CancellationToken ct)
    {
        var info = Probe(source);
        if (seconds <= 0.05 || (info.Duration > 0 && seconds >= info.Duration - 0.05)) throw new IOException("Pick a point inside the video.");
        var ext = Formats.Ext(source);
        string[] codec = info.HasVideo ? [.. VideoCodec, "-c:a", "aac", "-b:a", "192k"] : AudioCodec(ext);
        var outExt = info.HasVideo ? VideoOut(ext) : AudioOut(ext);
        double total = Math.Max(0.1, info.Duration);
        var first = Encode(source, " Part 1", outExt, ["-t", F(seconds), .. codec], seconds, p => progress(p * seconds / total), ct);
        var second = Encode(source, " Part 2", outExt, codec, total - seconds, p => progress((seconds + p * (total - seconds)) / total), ct, ["-ss", F(seconds)]);
        return [first, second];
    }

    /// <summary>"Redact": blacks out, blurs or pixelates the chosen areas for the whole video.</summary>
    public static string Redact(string source, IReadOnlyList<(NormRect Rect, RedactStyle Style)> regions, Action<double> progress, CancellationToken ct)
    {
        var info = Probe(source);
        if (info.Width <= 0) throw new IOException("Couldn't read the video's size.");
        var graph = new System.Text.StringBuilder();
        string current = "0:v";
        for (int i = 0; i < regions.Count; i++)
        {
            var (x, y, w, h) = Pixels(regions[i].Rect, info);
            string next = $"v{i + 1}";
            switch (regions[i].Style)
            {
                case RedactStyle.Box:
                    graph.Append($"[{current}]drawbox=x={x}:y={y}:w={w}:h={h}:color=black:t=fill[{next}];");
                    break;
                case RedactStyle.Blur:
                    int radius = Math.Max(2, Math.Min(w, h) / 8);
                    graph.Append($"[{current}]split[b{i}][s{i}];[s{i}]crop={w}:{h}:{x}:{y},boxblur={radius}:2[f{i}];[b{i}][f{i}]overlay={x}:{y}[{next}];");
                    break;
                default:
                    int bw = Math.Max(1, w / 14), bh = Math.Max(1, h / 14);
                    graph.Append($"[{current}]split[b{i}][s{i}];[s{i}]crop={w}:{h}:{x}:{y},scale={bw}:{bh},scale={w}:{h}:flags=neighbor[f{i}];[b{i}][f{i}]overlay={x}:{y}[{next}];");
                    break;
            }
            current = next;
        }
        var filter = graph.ToString().TrimEnd(';');
        return Encode(source, " Redacted", VideoOut(Formats.Ext(source)),
            ["-filter_complex", filter, "-map", $"[{current}]", "-map", "0:a?", "-map_metadata", "-1", .. VideoCodec, "-c:a", "copy", "-movflags", "+faststart"],
            info.Duration, progress, ct);
    }

    // ---------- Audio tools ----------

    /// <summary>"Normalize": evens out the loudness to streaming level (-16 LUFS).</summary>
    public static string Normalize(string source, Action<double> progress, CancellationToken ct)
    {
        var info = Probe(source);
        if (!info.HasAudio) throw new IOException("This file has no audio track.");
        var ext = AudioOut(Formats.Ext(source));
        return Encode(source, " Normalized", ext, ["-vn", "-af", "loudnorm=I=-16:TP=-1.5:LRA=11", "-ar", "48000", .. AudioCodec(ext)], info.Duration, progress, ct);
    }

    /// <summary>"Channels": mono, stereo, swap left/right, or one side on both speakers.</summary>
    public static string Channels(string source, string mode, Action<double> progress, CancellationToken ct)
    {
        var info = Probe(source);
        if (!info.HasAudio) throw new IOException("This file has no audio track.");
        var ext = AudioOut(Formats.Ext(source));
        string[] mix = mode switch
        {
            "mono" => ["-ac", "1"],
            "stereo" => ["-ac", "2"],
            "swap" => ["-af", "pan=stereo|c0=c1|c1=c0"],
            "left" => ["-af", "pan=stereo|c0=c0|c1=c0"],
            "right" => ["-af", "pan=stereo|c0=c1|c1=c1"],
            _ => throw new IOException("Unknown channel option."),
        };
        var suffix = mode switch { "swap" => " Swapped", "left" => " Left", "right" => " Right", _ => " " + char.ToUpperInvariant(mode[0]) + mode[1..] };
        return Encode(source, suffix, ext, ["-vn", .. mix, .. AudioCodec(ext)], info.Duration, progress, ct);
    }

    /// <summary>"Visualize": a waveform picture (PNG), or a video of the sound with a moving waveform (MP4).</summary>
    public static string Visualize(string source, bool video, string color, Action<double> progress, CancellationToken ct)
    {
        var info = Probe(source);
        if (!info.HasAudio) throw new IOException("This file has no audio track.");
        if (!video)
            return Encode(source, " Waveform", "png", ["-filter_complex", $"[0:a]aformat=channel_layouts=mono,showwavespic=s=1800x480:colors=0x{color}:draw=full", "-frames:v", "1"], 0, _ => { }, ct);
        return Encode(source, " Waveform", "mp4",
            ["-filter_complex", $"[0:a]showwaves=s=1280x720:mode=cline:rate=30:colors=0x{color}:scale=sqrt:draw=full,format=yuv420p[v]",
             "-map", "[v]", "-map", "0:a", "-c:v", "libx264", "-preset", "veryfast", "-crf", "22", "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart"],
            info.Duration, progress, ct);
    }

    /// <summary>"Bleep": silences a stretch and plays a 1 kHz tone over it.</summary>
    public static string Bleep(string source, double start, double end, Action<double> progress, CancellationToken ct)
    {
        var info = Probe(source);
        if (!info.HasAudio) throw new IOException("This file has no audio track.");
        var ext = AudioOut(Formats.Ext(source));
        double length = Math.Max(0.05, end - start);
        int delay = (int)Math.Round(start * 1000);
        var graph = $"[0:a]volume=enable='between(t,{F(start)},{F(end)})':volume=0[quiet];" +
                    $"sine=frequency=1000:sample_rate=48000:duration={F(length)},volume=0.35,adelay=delays={delay}:all=1[tone];" +
                    "[quiet][tone]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[a]";
        return Encode(source, " Bleeped", ext, ["-filter_complex", graph, "-map", "[a]", "-ar", "48000", .. AudioCodec(ext)], info.Duration, progress, ct);
    }

    [GeneratedRegex(@"Duration:\s*(\d{2}:\d{2}:\d{2}\.\d{2})")]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"Video:.*?(\d{2,5})x(\d{2,5})")]
    private static partial Regex VideoRegex();
}
