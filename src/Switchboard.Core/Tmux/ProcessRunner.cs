using System.ComponentModel;
using System.Diagnostics;

namespace Switchboard.Core.Tmux;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

internal static class ProcessRunner
{
    /// <summary>
    /// Runs a program with an argument list. ArgumentList passes each argument to the program
    /// as-is, with no shell in between, so text from users or agents can never be run as a
    /// shell command.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(
        string fileName, IEnumerable<string> args, string? stdin = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new TmuxException($"Could not start '{fileName}'.");
        }
        catch (Win32Exception e)
        {
            throw new TmuxException($"Could not start '{fileName}': {e.Message}. Is it installed and on PATH?");
        }

        using (process)
        {
            // Read both streams while the process runs. Waiting first and reading after can
            // deadlock: the child blocks once the pipe buffer is full and never exits.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), ct);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(ct);
            return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
    }
}
