# TestingBot.Tunnel

[![NuGet](https://img.shields.io/nuget/v/TestingBot.Tunnel.svg)](https://www.nuget.org/packages/TestingBot.Tunnel)
[![CI](https://github.com/testingbot/testingbot-tunnel-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/testingbot/testingbot-tunnel-dotnet/actions/workflows/ci.yml)

Download and launch [TestingBot Tunnel](https://testingbot.com/support/tunnel) from your .NET tests, so browsers and devices on TestingBot can reach websites on your machine, in CI or on your private network.

The package downloads the tunnel the first time, starts it, waits until it is ready and stops it again when your tests are done. When Java 17 or newer is not installed, it uses the TestingBot Tunnel download with Java included, so nothing else has to be installed.

Targets .NET Standard 2.0 (.NET Framework 4.6.1+, .NET Core 2.0+) and .NET 8.

## Installation

```sh
dotnet add package TestingBot.Tunnel
```

## Usage

```csharp
using TestingBot;

await using var tunnel = new Tunnel(new TunnelOptions
{
    TunnelIdentifier = "my-build-42",
});

await tunnel.StartAsync();   // completes once the tunnel is ready

// ... run your tests, with tb:options { "tunnel-identifier": "my-build-42" }

await tunnel.StopAsync();    // waits until the tunnel process has exited
```

`Tunnel` implements `IDisposable` and `IAsyncDisposable`: disposing it stops the tunnel. `Tunnel.StartNewAsync(options)` creates and starts one in a single call.

| Member | |
| --- | --- |
| `Task StartAsync(CancellationToken)` | Download the tunnel when needed, start it and wait until it is ready. Cancelling stops a tunnel that is still starting. |
| `Task StopAsync(CancellationToken)` | Stop the tunnel and wait until its process has exited. Safe to call more than once. |
| `bool IsRunning` | Whether the tunnel is ready and its process still runs. |
| `int? ProcessId`, `int? ExitCode` | The tunnel process. |
| `string? TunnelVersion`, `JavaPath`, `JarPath` | What is running, e.g. `5.2` with `/usr/bin/java`. |
| `TestingBotCredentials.Resolve()` | The key and secret the tunnel uses, to build your WebDriver capabilities with. |

`StartAsync` throws an `ArgumentException` for options that are not valid and a `TunnelException` when the tunnel can not be downloaded or started. The message says why, in the words of TestingBot where it gave a reason, for example:

- `Invalid credentials. Please supply the correct key/secret obtained from TestingBot.com`
- `You already have 2 tunnels active - please close another tunnel first`
- `Tunnel failed to launch in 90 seconds.`
- `Could not start TestingBot Tunnel. Exit code 1` followed by the last lines the tunnel wrote

### NUnit and Selenium

Start one tunnel for the whole test run in a `SetUpFixture`, and point every remote browser at it with `tunnel-identifier`:

```csharp
using NUnit.Framework;
using TestingBot;

[SetUpFixture]
public class TestingBotTunnel
{
    public static readonly string Identifier = $"nunit-{Guid.NewGuid():N}";
    private static Tunnel? _tunnel;

    [OneTimeSetUp]
    public async Task StartTunnel()
    {
        _tunnel = new Tunnel(new TunnelOptions { TunnelIdentifier = Identifier });
        await _tunnel.StartAsync();
    }

    [OneTimeTearDown]
    public async Task StopTunnel()
    {
        if (_tunnel != null)
        {
            await _tunnel.DisposeAsync();
        }
    }
}
```

```csharp
using NUnit.Framework;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Remote;
using TestingBot;

public class HomePageTests
{
    private RemoteWebDriver _driver = null!;

    [SetUp]
    public void StartBrowser()
    {
        var credentials = TestingBotCredentials.Resolve()!;
        var options = new ChromeOptions { BrowserVersion = "latest", PlatformName = "WIN11" };
        options.AddAdditionalOption("tb:options", new Dictionary<string, object>
        {
            ["key"] = credentials.Key,
            ["secret"] = credentials.Secret,
            ["tunnel-identifier"] = TestingBotTunnel.Identifier,
            ["name"] = TestContext.CurrentContext.Test.FullName,
        });

        _driver = new RemoteWebDriver(new Uri("https://hub.testingbot.com/wd/hub"), options);
    }

    [Test]
    public void ShowsTheTitle()
    {
        // local.testingbot.com resolves to 127.0.0.1: your machine, through the tunnel
        _driver.Navigate().GoToUrl("http://local.testingbot.com:3000/");
        Assert.That(_driver.Title, Is.EqualTo("My App"));
    }

    [TearDown]
    public void StopBrowser() => _driver.Quit();
}
```

Use `http://local.testingbot.com:<port>` rather than `localhost`: browsers refuse to send `localhost` through a proxy, see [Localhost](https://testingbot.com/support/tunnel/troubleshooting#localhost).

### xUnit

Share one tunnel between the tests of a collection with a fixture:

```csharp
public sealed class TunnelFixture : IAsyncLifetime
{
    public Tunnel Tunnel { get; } = new Tunnel(new TunnelOptions { TunnelIdentifier = $"xunit-{Guid.NewGuid():N}" });

    public Task InitializeAsync() => Tunnel.StartAsync();

    public Task DisposeAsync() => Tunnel.StopAsync();
}
```

## Credentials

The [TestingBot key and secret](https://testingbot.com/members) are looked up in this order:

1. `ApiKey` and `ApiSecret` in the options
2. the `TB_KEY` and `TB_SECRET` environment variables
3. the `TESTINGBOT_KEY` and `TESTINGBOT_SECRET` environment variables
4. `~/.testingbot`, holding `key:secret`

They are handed to the tunnel through the `TESTINGBOT_KEY`/`TESTINGBOT_SECRET` environment variables of its process, never on its command line, so they do not show up in the process list. They are redacted from everything the launcher logs.

## Options

```csharp
var tunnel = new Tunnel(new TunnelOptions
{
    // Launcher options
    ApiKey = "key",
    ApiSecret = "secret",
    TunnelVersion = "5.2",                     // the latest when not set
    StartTimeout = TimeSpan.FromMinutes(2),    // default 90 seconds
    JavaRuntime = TunnelJavaRuntime.Auto,      // Auto, System or Bundled, see below
    Verbose = true,                            // log the tunnel output
    Log = message => TestContext.Progress.WriteLine(message), // default Console.WriteLine, null for silence

    // Tunnel options, see https://testingbot.com/support/tunnel/tunnelcommand
    TunnelIdentifier = "my-build",
    Shared = true,
    SePort = 4445,
    Proxy = "proxy.example.com:3128",
    ProxyUserpwd = "user:pwd",
    FastFailRegexps = ".*\\.google-analytics\\.com",
    NoBump = true,
    NoCache = true,
    Logfile = "tunnel.log",
});
```

Every option of TestingBot Tunnel has a property, named after the command line option: `Dns`, `Auth`, `Pac`, `HubPort`, `LocalProxy`, `NoProxy`, `Web`, `ExtraHeaders`, `Debug`, `MetricsPort` and `MetricsAuth`.

Tunnel 5.0 added `AllowHosts`, `BindAddress`, `CacertFiles`, `Config`, `ConnectTo`, `DnsRoundRobin`, `DnsTimeout`, `Headers`, `ResponseHeaders`, `HttpDialTimeout`, `HttpIdleTimeout`, `Krb5Hosts`, `Krb5Keytab`, `Krb5Principal`, `LocalhostPolicy`, `LogFormat`, `LogHttp`, `LogLevel`, `RequestIdHeader`, `NoBumpDomains`, `PacLocal`, `PacLocalSha256`, `ProxyAuthScheme`, `ProxySpn`, `ProxyTestingbot`, `ProxyTestingbotUserpwd`, `SshHostKeys`, `SshHostKeyPolicy` and `WsProxyMode`; tunnel 5.1 added `SshConnections` (1 to 8). The lists (`CacertFiles`, `Headers`, `ResponseHeaders`, `SshHostKeys`) pass the option once for every entry. Using one of them with an older `TunnelVersion` fails before the tunnel is started, with a message that says which version it needs.

`AdditionalArguments` passes arguments to the tunnel as they are, for options added to the tunnel after this package was released.

## Java

TestingBot Tunnel is a Java program: 5.0 and up need Java 17 or newer, older versions Java 11.

TestingBot also publishes the tunnel with Java included, for Linux and macOS (x64 and arm64) and Windows (x64). `JavaRuntime` picks the Java:

- `Auto` (default): the `java` on the `PATH` when it is recent enough for the tunnel, the download with Java included otherwise
- `System`: always the `java` on the `PATH`
- `Bundled`: always the download with Java included

The download with Java included (about 30 MB, once per version) is checked against the SHA-256 checksum TestingBot publishes next to it, and is not used when it does not match. Set `TESTINGBOT_TUNNEL_RELEASES_URL` to download it from a mirror of the [TestingBot Tunnel releases](https://github.com/testingbot/Testingbot-Tunnel/releases), laid out the same way.

All downloads are made over https; a redirect to plain http is refused.

## Where the tunnel is stored

In the cache directory of the user: `~/Library/Caches/testingbot-tunnel-launcher` on macOS, `%LOCALAPPDATA%\testingbot-tunnel-launcher` on Windows and `~/.cache/testingbot-tunnel-launcher` (or `$XDG_CACHE_HOME`) elsewhere. This is the same directory the [Node.js launcher](https://github.com/testingbot/testingbot-tunnel-launcher) uses, so they share their downloads. Set `TESTINGBOT_TUNNEL_CACHE_DIR` to store it somewhere else, for example in a directory your CI caches.

## Stopping

`StopAsync` asks the tunnel to stop the way Ctrl+C does, so it can close its connection to TestingBot, and kills it when it has not stopped after 10 seconds. On Windows the tunnel process is ended straight away, as there is no Ctrl+C for a process without a console. A tunnel that is still running when your test process exits is stopped as well.

Several tunnels can run next to each other: give each its own `TunnelIdentifier`.

## Development

```sh
dotnet test
# against the netstandard2.0 build of the library
dotnet test -p:TestNetStandard=true
```

The tests use a stand-in for java and for the TestingBot downloads, they need no network or account.

`samples/TestingBot.Tunnel.LiveCheck` is a real end-to-end check: it starts a tunnel with your credentials, serves a page on this machine, opens it in a remote Chrome on TestingBot through the tunnel and checks its title.

```sh
dotnet run -f net8.0 --project samples/TestingBot.Tunnel.LiveCheck            # the installed Java
dotnet run -f net8.0 --project samples/TestingBot.Tunnel.LiveCheck -- --bundled
```

## License

MIT, see [LICENSE](LICENSE). Copyright (c) TestingBot &lt;info@testingbot.com&gt;
