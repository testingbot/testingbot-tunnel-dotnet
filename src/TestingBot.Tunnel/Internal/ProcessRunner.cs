using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TestingBot.Internal
{
    internal sealed class ProcessResult
    {
        public ProcessResult(int exitCode, string stdout, string stderr)
        {
            ExitCode = exitCode;
            Stdout = stdout;
            Stderr = stderr;
        }

        public int ExitCode { get; }
        public string Stdout { get; }
        public string Stderr { get; }
    }

    internal static class ProcessRunner
    {
        public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        public static ProcessStartInfo CreateStartInfo(string fileName, IEnumerable<string> arguments)
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
#if NET8_0_OR_GREATER
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
#else
            startInfo.Arguments = JoinArguments(arguments);
#endif
            return startInfo;
        }

        /// <summary>
        /// Join arguments into one command line, quoted the way both Windows and .NET on Unix split it again.
        /// </summary>
        public static string JoinArguments(IEnumerable<string> arguments)
        {
            var builder = new StringBuilder();
            foreach (var argument in arguments)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
                {
                    builder.Append(argument);
                    continue;
                }

                builder.Append('"');
                var backslashes = 0;
                foreach (var character in argument)
                {
                    if (character == '\\')
                    {
                        backslashes++;
                        continue;
                    }
                    if (character == '"')
                    {
                        builder.Append('\\', backslashes * 2 + 1);
                    }
                    else
                    {
                        builder.Append('\\', backslashes);
                    }
                    backslashes = 0;
                    builder.Append(character);
                }
                builder.Append('\\', backslashes * 2);
                builder.Append('"');
            }
            return builder.ToString();
        }

        /// <summary>Run a command to the end and collect what it writes.</summary>
        /// <exception cref="Win32Exception">The command could not be started at all.</exception>
        public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, IDictionary<string, string?>? environment = null)
        {
            var startInfo = CreateStartInfo(fileName, arguments);
            if (environment != null)
            {
                foreach (var variable in environment)
                {
                    startInfo.Environment[variable.Key] = variable.Value;
                }
            }

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            process.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (stdout) { stdout.AppendLine(e.Data); } } };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) { lock (stderr) { stderr.AppendLine(e.Data); } } };
            process.Exited += (_, __) => exited.TrySetResult(true);

            process.Start();
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timer.CancelAfter(timeout);
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (timer.Token.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(exited.Task, cancelled.Task).ConfigureAwait(false) != exited.Task)
                {
                    Kill(process);
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TunnelException($"{fileName} did not finish within {timeout.TotalSeconds} seconds.");
                }
            }

            // Wait for the output to be read to the end
            process.WaitForExit();
            lock (stdout)
            {
                lock (stderr)
                {
                    return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
                }
            }
        }

        /// <summary>Whether a command can be started at all.</summary>
        public static async Task<bool> CanStartAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
        {
            try
            {
                await RunAsync(fileName, arguments, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Win32Exception)
            {
                return false;
            }
            catch (TunnelException)
            {
                // It started, it just did not finish in time
                return true;
            }
        }

        /// <summary>
        /// Ask a process to stop the way Ctrl+C does, so the tunnel can close its connection
        /// to TestingBot. Windows has no such signal for a process without a console, it is killed there.
        /// </summary>
        public static void Interrupt(Process process)
        {
            if (HasExited(process))
            {
                return;
            }

            if (!IsWindows)
            {
                try
                {
                    using var kill = new Process { StartInfo = CreateStartInfo("kill", new[] { "-s", "INT", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }) };
                    kill.Start();
                    kill.StandardInput.Close();
                    if (kill.WaitForExit(5000) && kill.ExitCode == 0)
                    {
                        return;
                    }
                }
                catch (Win32Exception)
                {
                    // No kill command, the process is killed below
                }
                catch (InvalidOperationException)
                {
                    // The process is gone
                    return;
                }
            }

            Kill(process);
        }

        public static void Kill(Process process)
        {
            try
            {
#if NET8_0_OR_GREATER
                process.Kill(entireProcessTree: true);
#else
                process.Kill();
#endif
            }
            catch (InvalidOperationException)
            {
                // Already gone
            }
            catch (Win32Exception)
            {
                // Already exiting
            }
        }

        public static bool HasExited(Process process)
        {
            try
            {
                return process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }
}
