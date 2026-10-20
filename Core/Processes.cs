using System.Diagnostics;
using System.Text;

namespace FileWheel.Core;

public static class Processes
{
    /// <summary>Runs an engine with separate arguments (never a shell string). Throws with its error output on failure.</summary>
    /// <param name="firstErrorLine">Report the first line of the error output (PowerShell puts the message there) instead of the last.</param>
    public static string Run(string exe, IEnumerable<string> args, CancellationToken ct, TimeSpan timeout, Action<string>? onStdoutLine = null, bool firstErrorLine = false)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new IOException("Couldn't start " + Path.GetFileName(exe) + ".");
        var stderr = new StringBuilder();
        var stdout = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stdout) stdout.AppendLine(e.Data);
            onStdoutLine?.Invoke(e.Data);
        };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        using (ct.Register(() => { try { process.Kill(true); } catch { } }))
        {
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(true); } catch { }
                throw new IOException("The conversion took too long and was stopped.");
            }
            process.WaitForExit();
        }
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            var message = stderr.ToString().Trim();
            var lines = message.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
            var last = firstErrorLine ? lines.FirstOrDefault() : lines.LastOrDefault();
            throw new IOException(string.IsNullOrEmpty(last) ? Path.GetFileName(exe) + " failed." : last);
        }
        return stdout.ToString() + stderr.ToString();
    }
}
