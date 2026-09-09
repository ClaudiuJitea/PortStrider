using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CliWrap;
using PortStrider.Core.Models;
using PortStrider.Core.Services;
using PortStrider.Infrastructure.Platform;

namespace PortStrider.Infrastructure.Probes;

public sealed class IperfService : IIperfService
{
    private CancellationTokenSource? _serverCts;
    private Task? _serverTask;

    public bool IsServerRunning => _serverTask is { IsCompleted: false };

    public async Task<IperfResult> RunClientAsync(string host, int seconds, int port, IProgress<string> progress, CancellationToken cancellationToken = default)
    {
        if (!await ProcessUtil.CommandExistsAsync("iperf3", cancellationToken))
        {
            return new IperfResult { Success = false, Error = "iperf3 is not installed or not on PATH." };
        }

        progress.Report($"iperf3 -c {host} -p {port} -t {seconds} -J");
        var result = await ProcessUtil.RunAsync("iperf3", ["-c", host, "-p", port.ToString(), "-t", seconds.ToString(), "-J"], cancellationToken);
        var output = (result?.StandardOutput ?? "") + result?.StandardError;
        if (result is null)
            return new IperfResult { Success = false, Error = "Failed to start iperf3", RawOutput = output };

        try
        {
            using var doc = JsonDocument.Parse(result.StandardOutput);
            var bps = doc.RootElement.GetProperty("end").GetProperty("sum_received").GetProperty("bits_per_second").GetDouble();
            return new IperfResult
            {
                Success = result.ExitCode == 0,
                BitsPerSecond = bps,
                Summary = $"{AdapterInfo.FormatSpeed((long)bps)} received",
                RawOutput = output
            };
        }
        catch
        {
            var match = Regex.Match(output, @"([\d.]+)\s+([GMK]?)bits/sec");
            return new IperfResult
            {
                Success = result.ExitCode == 0,
                Summary = match.Success ? match.Value : (result.ExitCode == 0 ? "Completed" : "iperf3 failed"),
                Error = result.ExitCode == 0 ? null : result.StandardError,
                RawOutput = output
            };
        }
    }

    public async Task StartServerAsync(int port, IProgress<string> progress, CancellationToken cancellationToken = default)
    {
        if (IsServerRunning) return;
        if (!await ProcessUtil.CommandExistsAsync("iperf3", cancellationToken))
            throw new InvalidOperationException("iperf3 is not installed or not on PATH.");
        _serverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _serverCts.Token;
        _serverTask = Task.Run(async () =>
        {
            progress.Report($"iperf3 -s -p {port}");
            var exe = ProcessUtil.FindCommand("iperf3") ?? "iperf3";
            try
            {
                await Cli.Wrap(exe)
                    .WithArguments(["-s", "-p", port.ToString()])
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteAsync(ct);
            }
            catch (OperationCanceledException)
            {
                progress.Report("iPerf server stopped");
            }
            catch (Exception ex)
            {
                progress.Report(ex.Message);
            }
        }, ct);
    }

    public async Task StopServerAsync()
    {
        if (_serverCts is null) return;
        await _serverCts.CancelAsync();
        try { if (_serverTask is not null) await _serverTask; }
        catch { /* cancelled */ }
        _serverCts.Dispose();
        _serverCts = null;
        _serverTask = null;
    }
}
