using System.Diagnostics;
using System.Text;
using CliWrap;
using CliWrap.Buffered;

namespace PortStrider.Infrastructure.Platform;

internal static class ProcessUtil
{
    public static Task<bool> CommandExistsAsync(string command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(FindCommand(command) is not null);
    }

    public static string? FindCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        if (Path.IsPathRooted(command))
            return File.Exists(command) ? command : null;

        var names = new List<string> { command };
        if (HostOs.IsWindows && Path.GetExtension(command).Length == 0)
        {
            var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries);
            names.AddRange(extensions.Select(extension => command + extension.ToLowerInvariant()));
        }

        var customDirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "PortStrider", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortStrider", "bin"),
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "bin")
        };

        var directories = customDirs
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            .Concat(HostOs.IsWindows
                ? []
                : ["/usr/local/sbin", "/usr/local/bin", "/usr/sbin", "/usr/bin", "/sbin", "/bin"])
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(HostOs.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var directory in directories)
        {
            foreach (var name in names)
            {
                try
                {
                    var path = Path.Combine(directory, name);
                    if (File.Exists(path)) return path;
                }
                catch
                {
                    // ignore invalid path characters
                }
            }
        }

        return null;
    }

    public static async Task<BufferedCommandResult?> RunAsync(
        string file,
        IEnumerable<string> args,
        CancellationToken cancellationToken = default,
        string? stdin = null)
    {
        try
        {
            var resolved = FindCommand(file) ?? file;
            var cmd = Cli.Wrap(resolved)
                .WithArguments(args)
                .WithValidation(CommandResultValidation.None);

            return await cmd.ExecuteBufferedAsync(cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunCaptureAsync(
        string file,
        string arguments,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resolved = FindCommand(file) ?? file;
            var psi = new ProcessStartInfo
            {
                FileName = resolved,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is null) return (-1, "", "failed to start");
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }
}
