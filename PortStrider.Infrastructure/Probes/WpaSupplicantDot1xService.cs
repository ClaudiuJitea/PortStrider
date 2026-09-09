using System.Diagnostics;
using System.Text;
using CliWrap;
using PortStrider.Core.Enums;
using PortStrider.Core.Models;
using PortStrider.Core.Services;
using PortStrider.Infrastructure.Platform;

namespace PortStrider.Infrastructure.Probes;

/// <summary>
/// 802.1X port authentication using wpa_supplicant's wired driver. A temporary configuration is generated from the
/// profile (PEAP/TTLS/TLS/MD5), the supplicant is started on the interface, and EAP success/failure is read from its
/// event log. The supplicant is kept running for the rest of the AutoTest so the port stays authorized, then stopped.
/// </summary>
public sealed class WpaSupplicantDot1xService : IDot1xService
{
    public bool IsSupported => HostOs.IsLinux && ProcessUtil.FindCommand("wpa_supplicant") is not null;

    public async Task<Dot1xSession> AuthenticateAsync(string interfaceName, TestProfile profile, TimeSpan timeout, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            return Idle(new Dot1xResult
            {
                Status = TestStatus.Skipped,
                Summary = HostOs.IsLinux ? "wpa_supplicant is not installed." : "802.1X is only implemented on Linux (wpa_supplicant wired driver).",
                EapMethod = profile.EapType.ToString().ToUpperInvariant()
            });
        }

        var identity = profile.Username8021X ?? "";
        if (profile.EapType != EapType.Tls && (string.IsNullOrWhiteSpace(identity) || string.IsNullOrEmpty(profile.Password8021X)))
        {
            return Idle(new Dot1xResult
            {
                Status = TestStatus.Fail,
                Summary = "802.1X enabled but username/password are missing in the profile.",
                EapMethod = profile.EapType.ToString().ToUpperInvariant(),
                Identity = identity
            });
        }

        if (profile.EapType == EapType.Tls && (string.IsNullOrWhiteSpace(profile.ClientCertPath8021X) || string.IsNullOrWhiteSpace(profile.ClientKeyPath8021X)))
        {
            return Idle(new Dot1xResult
            {
                Status = TestStatus.Fail,
                Summary = "EAP-TLS needs client certificate and private key paths in the profile.",
                EapMethod = "TLS",
                Identity = identity
            });
        }

        var workDir = Path.Combine(Path.GetTempPath(), $"portstrider-8021x-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var configPath = Path.Combine(workDir, "wpa.conf");
        await File.WriteAllTextAsync(configPath, BuildConfig(profile, workDir), cancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(configPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* not fatal */ }
        }

        var log = new StringBuilder();
        var outcome = new TaskCompletionSource<(bool success, string reason)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var killCts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        string method = profile.EapType.ToString().ToUpperInvariant();

        void OnLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (log)
            {
                if (log.Length < 64_000) log.AppendLine(line);
            }

            progress?.Report(line.Trim());
            if (line.Contains("CTRL-EVENT-EAP-METHOD", StringComparison.Ordinal))
            {
                var idx = line.IndexOf("method", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) method = line[(idx + 6)..].Trim().Split(' ')[0].Trim('(', ')');
            }

            if (line.Contains("CTRL-EVENT-EAP-SUCCESS", StringComparison.Ordinal) || line.Contains("CTRL-EVENT-CONNECTED", StringComparison.Ordinal))
                outcome.TrySetResult((true, "EAP success"));
            else if (line.Contains("CTRL-EVENT-EAP-FAILURE", StringComparison.Ordinal))
                outcome.TrySetResult((false, "EAP failure (authenticator rejected credentials)"));
            else if (line.Contains("CTRL-EVENT-EAP-TLS-CERT-ERROR", StringComparison.Ordinal))
                outcome.TrySetResult((false, "TLS certificate error"));
            else if (line.Contains("Could not read interface", StringComparison.OrdinalIgnoreCase)
                     || line.Contains("Failed to initialize driver", StringComparison.OrdinalIgnoreCase)
                     || line.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase))
                outcome.TrySetResult((false, line.Trim()));
        }

        var command = PrivilegedProcess.Build("wpa_supplicant", ["-D", "wired", "-i", interfaceName, "-c", configPath, "-d"])
            .WithStandardOutputPipe(PipeTarget.ToDelegate(OnLine))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(OnLine));

        Task<CommandResult> run;
        try
        {
            run = command.ExecuteAsync(killCts.Token).Task;
        }
        catch (Exception ex)
        {
            Cleanup(workDir);
            return Idle(new Dot1xResult { Status = TestStatus.Fail, Summary = $"wpa_supplicant could not start: {ex.Message}", EapMethod = method, Identity = identity });
        }

        // If the supplicant exits early (bad config, no privileges) surface that instead of waiting for the timeout.
        _ = run.ContinueWith(t =>
        {
            var reason = t.IsFaulted ? t.Exception?.GetBaseException().Message ?? "exited" : $"wpa_supplicant exited with code {(t.IsCompletedSuccessfully ? t.Result.ExitCode : -1)}";
            outcome.TrySetResult((false, reason));
        }, TaskContinuationOptions.ExecuteSynchronously);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        (bool success, string reason) result;
        try
        {
            result = await outcome.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = (false, $"No EAP result within {timeout.TotalSeconds:0}s — the port may not run 802.1X, or the authenticator is silent");
        }
        catch (OperationCanceledException)
        {
            await StopAsync(killCts, run, workDir);
            throw;
        }

        sw.Stop();
        string rawLog;
        lock (log) rawLog = log.ToString();
        var dot1x = new Dot1xResult
        {
            Status = result.success ? TestStatus.Pass : TestStatus.Fail,
            Summary = result.success
                ? $"Authenticated as {identity} via {method} in {sw.ElapsedMilliseconds} ms"
                : $"{result.reason} ({method}, {identity})",
            EapMethod = method,
            Identity = identity,
            ElapsedMs = sw.ElapsedMilliseconds,
            RawLog = rawLog
        };

        if (!result.success)
        {
            await StopAsync(killCts, run, workDir);
            return Idle(dot1x);
        }

        return new Dot1xSession(dot1x, () => StopAsync(killCts, run, workDir));
    }

    private static async Task StopAsync(CancellationTokenSource killCts, Task run, string workDir)
    {
        try { killCts.Cancel(); } catch { /* ignore */ }
        try { await run.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* killed or timed out */ }
        killCts.Dispose();
        Cleanup(workDir);
    }

    private static void Cleanup(string workDir)
    {
        try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
    }

    private static Dot1xSession Idle(Dot1xResult result) => new(result, () => Task.CompletedTask);

    internal static string BuildConfig(TestProfile profile, string workDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"ctrl_interface={workDir}");
        sb.AppendLine("ap_scan=0");
        sb.AppendLine("network={");
        sb.AppendLine("    key_mgmt=IEEE8021X");
        sb.AppendLine("    eapol_flags=0");
        switch (profile.EapType)
        {
            case EapType.Peap:
                sb.AppendLine("    eap=PEAP");
                sb.AppendLine("    phase1=\"peaplabel=0\"");
                sb.AppendLine("    phase2=\"auth=MSCHAPV2\"");
                break;
            case EapType.Ttls:
                sb.AppendLine("    eap=TTLS");
                sb.AppendLine("    phase2=\"auth=MSCHAPV2 auth=PAP\"");
                break;
            case EapType.Md5:
                sb.AppendLine("    eap=MD5");
                break;
            case EapType.Tls:
                sb.AppendLine("    eap=TLS");
                break;
        }

        if (!string.IsNullOrWhiteSpace(profile.Username8021X))
            sb.AppendLine($"    identity=\"{Escape(profile.Username8021X)}\"");
        if (!string.IsNullOrWhiteSpace(profile.AnonymousIdentity8021X))
            sb.AppendLine($"    anonymous_identity=\"{Escape(profile.AnonymousIdentity8021X)}\"");
        if (profile.EapType != EapType.Tls && !string.IsNullOrEmpty(profile.Password8021X))
            sb.AppendLine($"    password=\"{Escape(profile.Password8021X)}\"");
        if (!string.IsNullOrWhiteSpace(profile.CaCertPath8021X))
            sb.AppendLine($"    ca_cert=\"{Escape(profile.CaCertPath8021X)}\"");
        if (profile.EapType == EapType.Tls)
        {
            sb.AppendLine($"    client_cert=\"{Escape(profile.ClientCertPath8021X ?? "")}\"");
            sb.AppendLine($"    private_key=\"{Escape(profile.ClientKeyPath8021X ?? "")}\"");
            if (!string.IsNullOrEmpty(profile.Password8021X))
                sb.AppendLine($"    private_key_passwd=\"{Escape(profile.Password8021X)}\"");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
