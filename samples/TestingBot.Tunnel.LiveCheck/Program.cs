using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Remote;
using TestingBot;

// The credentials the tunnel uses, for the remote browser as well. The secret is never printed.
var credentials = TestingBotCredentials.Resolve()
    ?? throw new InvalidOperationException("No TestingBot credentials: set TB_KEY/TB_SECRET or create ~/.testingbot");
Console.WriteLine($"Using credentials from {credentials.Source}");

var identifier = $"dotnet-live-{Guid.NewGuid():N}"[..24];
var title = $"TestingBot Tunnel .NET check {Guid.NewGuid():N}";

// A page that only exists on this machine
using var server = new TcpListener(IPAddress.Loopback, 0);
server.Start();
var port = ((IPEndPoint)server.LocalEndpoint).Port;
using var stopServer = new CancellationTokenSource();
var requests = 0;
_ = Task.Run(async () =>
{
    while (!stopServer.IsCancellationRequested)
    {
        using var client = await server.AcceptTcpClientAsync(stopServer.Token);
        await using var stream = client.GetStream();
        var buffer = new byte[8192];
        _ = await stream.ReadAsync(buffer, stopServer.Token);
        Interlocked.Increment(ref requests);
        var body = Encoding.UTF8.GetBytes($"<!doctype html><html><head><title>{title}</title></head><body><h1>{title}</h1></body></html>");
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, stopServer.Token);
        await stream.WriteAsync(body, stopServer.Token);
    }
});
Console.WriteLine($"Serving the page on 127.0.0.1:{port}");

// --bundled runs the tunnel with the Java included in its download instead of the installed Java
var tunnel = new Tunnel(new TunnelOptions
{
    TunnelIdentifier = identifier,
    JavaRuntime = args.Contains("--bundled") ? TunnelJavaRuntime.Bundled : TunnelJavaRuntime.Auto,
});
var stopwatch = Stopwatch.StartNew();
await tunnel.StartAsync();
Console.WriteLine($"Tunnel {tunnel.TunnelVersion} ready in {stopwatch.Elapsed.TotalSeconds:F1}s (pid {tunnel.ProcessId}, java {tunnel.JavaPath})");

var failed = false;
try
{
    var options = new ChromeOptions { BrowserVersion = "latest", PlatformName = "WIN11" };
    options.AddAdditionalOption("tb:options", new Dictionary<string, object>
    {
        ["key"] = credentials.Key,
        ["secret"] = credentials.Secret,
        ["tunnel-identifier"] = identifier,
        ["name"] = "TestingBot.Tunnel .NET live check",
    });

    using var driver = new RemoteWebDriver(new Uri("https://hub.testingbot.com/wd/hub"), options.ToCapabilities(), TimeSpan.FromMinutes(3));
    try
    {
        // local.testingbot.com resolves to 127.0.0.1, on this machine through the tunnel
        driver.Navigate().GoToUrl($"http://local.testingbot.com:{port}/");
        Console.WriteLine($"Remote browser sees the title \"{driver.Title}\" ({requests} request(s) reached the local server)");
        if (driver.Title != title)
        {
            Console.WriteLine($"FAIL: expected the title \"{title}\"");
            failed = true;
        }
    }
    finally
    {
        driver.Quit();
    }
}
finally
{
    var pid = tunnel.ProcessId!.Value;
    await tunnel.StopAsync();
    stopServer.Cancel();

    var gone = true;
    try
    {
        using var process = Process.GetProcessById(pid);
        gone = process.HasExited;
    }
    catch (ArgumentException)
    {
    }
    Console.WriteLine($"Tunnel stopped: IsRunning={tunnel.IsRunning}, exit code {tunnel.ExitCode}, process {pid} gone: {gone}");
    failed |= !gone || tunnel.IsRunning;
}

Console.WriteLine(failed ? "LIVE CHECK FAILED" : "LIVE CHECK PASSED");
return failed ? 1 : 0;
