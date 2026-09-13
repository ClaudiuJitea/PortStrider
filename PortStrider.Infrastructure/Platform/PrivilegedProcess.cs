using System.Runtime.InteropServices;
using CliWrap;
using CliWrap.Buffered;

namespace PortStrider.Infrastructure.Platform;

/// <summary>
/// Runs network-admin commands (ip, ethtool, wpa_supplicant, dhclient) with the privileges available to the process.
/// On Linux the PortStrider binary normally carries cap_net_raw,cap_net_admin via setcap; those file capabilities are
/// not inherited by child processes unless raised into the inheritable and ambient sets on the launching thread. When that is not
/// possible (or the command still fails with a permission error) it retries through pkexec/sudo.
/// </summary>
public static class PrivilegedProcess
{
    private const int PR_CAP_AMBIENT = 47;
    private const int PR_CAP_AMBIENT_RAISE = 2;
    private const int CAP_NET_ADMIN = 12;
    private const int CAP_NET_RAW = 13;

    internal const uint NetworkCapabilityMask = (1u << CAP_NET_ADMIN) | (1u << CAP_NET_RAW);

    [StructLayout(LayoutKind.Sequential)]
    private struct CapabilityHeader
    {
        public uint Version;
        public int Pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CapabilityData
    {
        public uint Effective, Permitted, Inheritable;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int capget(ref CapabilityHeader header, [Out] CapabilityData[] data);

    [DllImport("libc", SetLastError = true)]
    private static extern int capset(ref CapabilityHeader header, [In] CapabilityData[] data);

    [DllImport("libc", SetLastError = true)]
    private static extern int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

    public static bool IsRoot
    {
        get
        {
            if (!HostOs.IsLinux) return false;
            try { return Mono.Unix.Native.Syscall.geteuid() == 0; }
            catch { return false; }
        }
    }

    /// <summary>True when child processes will inherit CAP_NET_ADMIN/CAP_NET_RAW, or we are root.</summary>
    public static bool HasAmbientNetCapabilities => IsRoot || TryRaiseAmbientCapabilities();

    public static bool HasEffectiveNetCapabilities
    {
        get
        {
            if (!HostOs.IsLinux) return false;
            try
            {
                var header = new CapabilityHeader { Version = 0x20080522 }; // Linux capability ABI v3, current thread
                var data = new CapabilityData[2];
                return capget(ref header, data) == 0 && (data[0].Effective & NetworkCapabilityMask) == NetworkCapabilityMask;
            }
            catch { return false; }
        }
    }

    internal static CapabilityData AddNetworkInheritance(CapabilityData data)
    {
        // Never add capabilities that this thread has not already been granted.
        data.Inheritable |= data.Permitted & NetworkCapabilityMask;
        return data;
    }

    public static bool TryRaiseAmbientCapabilities()
    {
        if (!HostOs.IsLinux) return false;
        try
        {
            var header = new CapabilityHeader { Version = 0x20080522 };
            var data = new CapabilityData[2];
            if (capget(ref header, data) != 0 || (data[0].Permitted & NetworkCapabilityMask) != NetworkCapabilityMask)
                return false;
            if ((data[0].Inheritable & NetworkCapabilityMask) != NetworkCapabilityMask)
            {
                data[0] = AddNetworkInheritance(data[0]);
                if (capset(ref header, data) != 0) return false;
            }
            // Capabilities are per thread: do not cache this result across async continuations.
            var admin = prctl(PR_CAP_AMBIENT, PR_CAP_AMBIENT_RAISE, CAP_NET_ADMIN, 0, 0);
            var raw = prctl(PR_CAP_AMBIENT, PR_CAP_AMBIENT_RAISE, CAP_NET_RAW, 0, 0);
            return admin == 0 && raw == 0;
        }
        catch
        {
            return false;
        }
    }

    public sealed record Result(int ExitCode, string StandardOutput, string StandardError, string CommandLine)
    {
        public bool Success => ExitCode == 0;
        public string Combined => (StandardOutput + "\n" + StandardError).Trim();
    }

    /// <summary>Runs a command, escalating through pkexec/sudo only if the direct attempt is refused for lack of privilege.</summary>
    public static async Task<Result> RunAsync(string file, IReadOnlyList<string> args, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        var resolved = ProcessUtil.FindCommand(file);
        if (resolved is null)
            return new Result(127, "", $"{file}: command not found", file);

        _ = HasAmbientNetCapabilities;
        var direct = await ExecuteAsync(resolved, args, cancellationToken, timeout);
        if (direct.Success || !HostOs.IsLinux || IsRoot || !LooksLikePermissionError(direct))
            return direct;

        var elevated = await ElevatedAsync(resolved, args, cancellationToken, timeout);
        return elevated ?? direct;
    }

    /// <summary>Builds a CliWrap command with elevation applied when needed (used for long-running daemons like wpa_supplicant).</summary>
    public static Command Build(string file, IReadOnlyList<string> args)
    {
        var resolved = ProcessUtil.FindCommand(file) ?? file;
        _ = HasAmbientNetCapabilities;
        if (!HostOs.IsLinux || IsRoot || HasAmbientNetCapabilities)
            return Cli.Wrap(resolved).WithArguments(args).WithValidation(CommandResultValidation.None);

        var sudo = ProcessUtil.FindCommand("sudo");
        if (sudo is not null && PasswordlessSudo.Value)
            return Cli.Wrap(sudo).WithArguments(["-n", resolved, .. args]).WithValidation(CommandResultValidation.None);

        var pkexec = ProcessUtil.FindCommand("pkexec");
        if (pkexec is not null)
            return Cli.Wrap(pkexec).WithArguments([resolved, .. args]).WithValidation(CommandResultValidation.None);

        return Cli.Wrap(resolved).WithArguments(args).WithValidation(CommandResultValidation.None);
    }

    private static readonly Lazy<bool> PasswordlessSudo = new(() =>
    {
        try
        {
            var sudo = ProcessUtil.FindCommand("sudo");
            if (sudo is null) return false;
            var result = Cli.Wrap(sudo).WithArguments(["-n", "true"]).WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync().GetAwaiter().GetResult();
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    });

    private static async Task<Result?> ElevatedAsync(string resolved, IReadOnlyList<string> args, CancellationToken cancellationToken, TimeSpan? timeout)
    {
        var sudo = ProcessUtil.FindCommand("sudo");
        if (sudo is not null && PasswordlessSudo.Value)
            return await ExecuteAsync(sudo, ["-n", resolved, .. args], cancellationToken, timeout);

        var pkexec = ProcessUtil.FindCommand("pkexec");
        if (pkexec is not null)
            return await ExecuteAsync(pkexec, [resolved, .. args], cancellationToken, timeout);

        return null;
    }

    private static async Task<Result> ExecuteAsync(string file, IReadOnlyList<string> args, CancellationToken cancellationToken, TimeSpan? timeout)
    {
        var commandLine = file + " " + string.Join(' ', args);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
            _ = HasAmbientNetCapabilities;
            var result = await Cli.Wrap(file)
                .WithArguments(args)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cts.Token);
            return new Result(result.ExitCode, result.StandardOutput, result.StandardError, commandLine);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Result(-1, "", "timed out", commandLine);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Result(-1, "", ex.Message, commandLine);
        }
    }

    private static bool LooksLikePermissionError(Result result)
    {
        var text = result.StandardError + result.StandardOutput;
        return text.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
               || text.Contains("must be root", StringComparison.OrdinalIgnoreCase)
               || text.Contains("not permitted", StringComparison.OrdinalIgnoreCase)
               || text.Contains("RTNETLINK answers: Operation not permitted", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Cannot set", StringComparison.OrdinalIgnoreCase) && text.Contains("permitted", StringComparison.OrdinalIgnoreCase);
    }
}
