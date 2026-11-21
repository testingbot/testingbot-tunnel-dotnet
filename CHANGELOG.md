# Changelog

## 1.0.0 - unreleased

First release, a port of the [Node.js launcher](https://github.com/testingbot/testingbot-tunnel-launcher) 1.2.0.

- `Tunnel` with `StartAsync`/`StopAsync`, `IsRunning`, `IDisposable` and `IAsyncDisposable`
- Downloads the tunnel jar from testingbot.com, or the TestingBot Tunnel download with Java included (5.0 and up) when Java 17 is not installed, checked against its published SHA-256 checksum
- Every TestingBot Tunnel option as a typed property, refused with a clear message when the tunnel version does not know it
- Credentials from the options, `TB_KEY`/`TB_SECRET`, `TESTINGBOT_KEY`/`TESTINGBOT_SECRET` or `~/.testingbot`, handed to the tunnel through its environment
- Waits for the readyfile of the tunnel, and reports why it could not start in the words of TestingBot
- Stops the tunnel with Ctrl+C semantics and kills it when it does not stop within 10 seconds; tunnels do not outlive the process that started them
