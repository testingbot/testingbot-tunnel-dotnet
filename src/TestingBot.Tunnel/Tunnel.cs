using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TestingBot.Internal;

namespace TestingBot
{
    /// <summary>
    /// Downloads and runs TestingBot Tunnel, so browsers and devices on TestingBot can reach websites
    /// on your machine or private network.
    /// </summary>
    /// <example>
    /// <code>
    /// await using var tunnel = new Tunnel(new TunnelOptions { TunnelIdentifier = "my-build" });
    /// await tunnel.StartAsync();
    /// // run your tests with tb:options tunnel-identifier = "my-build"
    /// await tunnel.StopAsync();
    /// </code>
    /// </example>
    public sealed class Tunnel : IDisposable, IAsyncDisposable
    {
        // Every tunnel that is still running, so none outlives the process that started it
        private static readonly HashSet<Tunnel> ActiveTunnels = new HashSet<Tunnel>();
        private static int _exitHandlerRegistered;

        private readonly TunnelOptions _options;
        private readonly TunnelEnvironment _environment;
        private readonly object _lock = new object();
        private readonly List<string> _recentOutput = new List<string>();

        private Process? _process;
        private TaskCompletionSource<bool>? _exited;
        private Task? _stopTask;
        private string? _readyDirectory;
        private TestingBotCredentials? _credentials;
        private string? _startupError;
        private bool _started;
        private volatile bool _ready;

        /// <summary>Create a tunnel. Nothing is downloaded or started until <see cref="StartAsync"/>.</summary>
        public Tunnel(TunnelOptions? options = null)
            : this(options ?? new TunnelOptions(), TunnelEnvironment.Default())
        {
        }

        internal Tunnel(TunnelOptions options, TunnelEnvironment environment)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _environment = environment;
        }

        /// <summary>Create and start a tunnel in one go.</summary>
        public static async Task<Tunnel> StartNewAsync(TunnelOptions? options = null, CancellationToken cancellationToken = default)
        {
            var tunnel = new Tunnel(options);
            try
            {
                await tunnel.StartAsync(cancellationToken).ConfigureAwait(false);
                return tunnel;
            }
            catch
            {
                await tunnel.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>The options this tunnel was created with.</summary>
        public TunnelOptions Options => _options;

        /// <summary>Whether the tunnel is ready and its process is still running.</summary>
        public bool IsRunning
        {
            get
            {
                var process = _process;
                return _ready && process != null && !ProcessRunner.HasExited(process);
            }
        }

        /// <summary>The process id of the tunnel, null before it is started.</summary>
        public int? ProcessId { get; private set; }

        /// <summary>The exit code of the tunnel process, null while it runs.</summary>
        public int? ExitCode
        {
            get
            {
                var process = _process;
                if (process == null || !ProcessRunner.HasExited(process))
                {
                    return null;
                }
                try
                {
                    return process.ExitCode;
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }
        }

        /// <summary>The version of the tunnel that runs, e.g. "5.2". Null when it could not be determined.</summary>
        public string? TunnelVersion { get; private set; }

        /// <summary>The java the tunnel runs with.</summary>
        public string? JavaPath { get; private set; }

        /// <summary>The tunnel jar that runs.</summary>
        public string? JarPath { get; private set; }

        /// <summary>
        /// Download the tunnel when needed and start it. Completes once the tunnel is ready to be used.
        /// </summary>
        /// <exception cref="ArgumentException">An option is not valid.</exception>
        /// <exception cref="TunnelException">The tunnel could not be downloaded or started, the message says why.</exception>
        /// <exception cref="OperationCanceledException">Cancelled; a tunnel that was starting is stopped.</exception>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                if (_started)
                {
                    throw new InvalidOperationException("This tunnel has already been started. Create a new Tunnel to start another one.");
                }
                _started = true;
            }

            TunnelVersions.ValidateOptions(_options);

            _credentials = TestingBotCredentials.Resolve(_options.ApiKey, _options.ApiSecret, _environment.GetEnvironmentVariable, _environment.HomeDirectory);
            if (_credentials == null && string.IsNullOrWhiteSpace(_options.Config))
            {
                throw new TunnelException("No TestingBot credentials found. Pass ApiKey and ApiSecret, set TB_KEY/TB_SECRET or TESTINGBOT_KEY/TESTINGBOT_SECRET, or create ~/.testingbot holding key:secret. Your credentials are at https://testingbot.com/members");
            }

            var runtime = await new RuntimeResolver(_environment, Log).PrepareAsync(_options, cancellationToken).ConfigureAwait(false);
            TunnelVersions.ValidateOptionsForVersion(_options, runtime.Version);

            TunnelVersion = runtime.Version;
            JavaPath = runtime.Java;
            JarPath = runtime.Jar;

            await RunAsync(runtime, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>The command line of the tunnel, without the readyfile. The credentials are not on it.</summary>
        internal static List<string> CreateArguments(TunnelOptions options, string jar)
        {
            var arguments = new List<string> { "-jar", jar };
            foreach (var argument in options.TunnelArguments())
            {
                arguments.Add("--" + argument.Name);
                arguments.AddRange(argument.Values);
            }
            if (options.AdditionalArguments != null)
            {
                arguments.AddRange(options.AdditionalArguments.Where(argument => argument != null));
            }
            return arguments;
        }

        private async Task RunAsync(TunnelRuntime runtime, CancellationToken cancellationToken)
        {
            // Every tunnel gets its own private directory, so tunnels running next to each
            // other can not see (or remove) each other's readyfile
            _readyDirectory = FileSystem.CreateTempDirectory("testingbot-tunnel-");
            var readyFile = Path.Combine(_readyDirectory, "testingbot.ready");

            var arguments = CreateArguments(_options, runtime.Jar);
            arguments.Add("-f");
            arguments.Add(readyFile);

            if (_options.Verbose)
            {
                Log("Starting tunnel with options " + Redact(ProcessRunner.JoinArguments(new[] { runtime.Java }.Concat(arguments))));
            }

            var startInfo = ProcessRunner.CreateStartInfo(runtime.Java, arguments);
            // The tunnel reads its credentials from the environment, which keeps them out of the process list
            if (_credentials != null)
            {
                startInfo.Environment["TESTINGBOT_KEY"] = _credentials.Key;
                startInfo.Environment["TESTINGBOT_SECRET"] = _credentials.Secret;
            }

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.OutputDataReceived += (_, e) => OnOutput(e.Data, stderr: false);
            process.ErrorDataReceived += (_, e) => OnOutput(e.Data, stderr: true);
            process.Exited += (_, __) => OnExited(exited);

            try
            {
                process.Start();
            }
            catch (System.ComponentModel.Win32Exception error)
            {
                process.Dispose();
                FileSystem.TryDeleteDirectory(_readyDirectory);
                throw new TunnelException($"Could not start TestingBot Tunnel: {error.Message}", error);
            }

            _process = process;
            _exited = exited;
            ProcessId = process.Id;
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            Register(this);

            var deadline = Stopwatch.StartNew();
            while (true)
            {
                if (File.Exists(readyFile))
                {
                    _ready = true;
                    Log("Tunnel is ready");
                    return;
                }

                if (exited.Task.IsCompleted)
                {
                    // Wait for the last output, it holds the reason
                    process.WaitForExit();
                    FileSystem.TryDeleteDirectory(_readyDirectory);
                    Unregister(this);
                    string[] recent;
                    lock (_recentOutput)
                    {
                        recent = _recentOutput.ToArray();
                    }
                    throw new TunnelException(TunnelOutput.DescribeStartupFailure(_startupError, ExitCode, recent));
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    // A tunnel that is still starting would otherwise keep using one of the tunnels of the account
                    await StopAsync(CancellationToken.None).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (deadline.Elapsed > _options.StartTimeout)
                {
                    var message = $"Tunnel failed to launch in {_options.StartTimeout.TotalSeconds} seconds.";
                    Log(message);
                    await StopAsync(CancellationToken.None).ConfigureAwait(false);
                    throw new TunnelException(message);
                }

                try
                {
                    await Task.WhenAny(exited.Task, Task.Delay(_environment.ReadyPollInterval, cancellationToken)).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Handled at the top of the loop
                }
            }
        }

        private void OnOutput(string? data, bool stderr)
        {
            if (data == null)
            {
                return;
            }

            foreach (var entry in TunnelOutput.ParseLogLine(data.Trim()))
            {
                var line = Redact(entry);
                if (line.Length == 0)
                {
                    continue;
                }

                if (_options.Verbose)
                {
                    Log(line);
                }

                if (!stderr)
                {
                    continue;
                }

                lock (_recentOutput)
                {
                    _recentOutput.Add(line);
                    if (_recentOutput.Count > TunnelOutput.MaxRememberedLines)
                    {
                        _recentOutput.RemoveAt(0);
                    }
                }

                if (!_options.Verbose && line.IndexOf("is available for download", StringComparison.Ordinal) > -1)
                {
                    Log(line);
                }

                // Once the tunnel is up, a failed API call (for example while it reconnects)
                // is something the tunnel recovers from, not a reason to close it
                var error = _ready ? null : TunnelOutput.ClassifyTunnelError(line);
                if (error != null)
                {
                    _startupError = error;
                    var process = _process;
                    if (process != null)
                    {
                        ProcessRunner.Interrupt(process);
                    }
                }
            }
        }

        private void OnExited(TaskCompletionSource<bool> exited)
        {
            if (_options.Verbose)
            {
                Log("Closing TestingBot Tunnel");
            }
            Unregister(this);
            exited.TrySetResult(true);
        }

        /// <summary>
        /// Stop the tunnel and wait until its process has exited. It is asked to stop the way Ctrl+C does,
        /// so it can close its connection to TestingBot, and killed when it has not stopped after 10 seconds.
        /// Calling it again, or on a tunnel that is not running, does nothing.
        /// </summary>
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Task stop;
            lock (_lock)
            {
                stop = _stopTask ??= StopCoreAsync();
            }
            return cancellationToken.CanBeCanceled ? WaitAsync(stop, cancellationToken) : stop;
        }

        private static async Task WaitAsync(Task task, CancellationToken cancellationToken)
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancelled.TrySetCanceled(cancellationToken)))
            {
                await (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false)).ConfigureAwait(false);
            }
        }

        private async Task StopCoreAsync()
        {
            await Task.Yield();
            var process = _process;
            var exited = _exited;
            try
            {
                if (process == null || exited == null || ProcessRunner.HasExited(process))
                {
                    return;
                }

                ProcessRunner.Interrupt(process);
                if (await Task.WhenAny(exited.Task, Task.Delay(_environment.KillGracePeriod)).ConfigureAwait(false) != exited.Task)
                {
                    ProcessRunner.Kill(process);
                    await exited.Task.ConfigureAwait(false);
                }
                process.WaitForExit();
            }
            finally
            {
                _ready = false;
                Unregister(this);
                FileSystem.TryDeleteDirectory(_readyDirectory);
            }
        }

        /// <summary>Stop the tunnel, see <see cref="StopAsync"/>.</summary>
        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            _process?.Dispose();
        }

        /// <summary>Stop the tunnel, see <see cref="StopAsync"/>. Blocks until it has exited.</summary>
        public void Dispose()
        {
            StopAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            _process?.Dispose();
        }

        private string Redact(string value) => TunnelOutput.Redact(value, _credentials);

        private void Log(string message)
        {
            try
            {
                _options.Log?.Invoke(message);
            }
            catch (InvalidOperationException)
            {
                // A test output helper that is used after its test finished
            }
        }

        private static void Register(Tunnel tunnel)
        {
            lock (ActiveTunnels)
            {
                ActiveTunnels.Add(tunnel);
            }

            if (Interlocked.Exchange(ref _exitHandlerRegistered, 1) == 0)
            {
                AppDomain.CurrentDomain.ProcessExit += (_, __) => StopAllWhileExiting();
            }
        }

        private static void Unregister(Tunnel tunnel)
        {
            lock (ActiveTunnels)
            {
                ActiveTunnels.Remove(tunnel);
            }
        }

        /// <summary>
        /// Without this a tunnel keeps running when the process that started it exits without stopping it,
        /// for example when a test run is aborted.
        /// </summary>
        internal static void StopAllWhileExiting()
        {
            Tunnel[] tunnels;
            lock (ActiveTunnels)
            {
                tunnels = ActiveTunnels.ToArray();
            }

            foreach (var tunnel in tunnels)
            {
                var process = tunnel._process;
                if (process != null)
                {
                    ProcessRunner.Interrupt(process);
                }
                FileSystem.TryDeleteDirectory(tunnel._readyDirectory);
            }
        }

        internal static int ActiveTunnelCount
        {
            get
            {
                lock (ActiveTunnels)
                {
                    return ActiveTunnels.Count;
                }
            }
        }
    }
}
