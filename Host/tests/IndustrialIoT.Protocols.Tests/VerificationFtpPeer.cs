using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace IndustrialIoT.Protocols.Tests;

// A deliberately small loopback-only FTP peer. It permits STOR overwrite like a normal FTP server,
// so the production driver's raw exclusive MKD (not the fixture) must prevent collisions.
internal sealed class VerificationFtpPeer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _sessions = [];
    private readonly ConcurrentDictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _directories = new(StringComparer.Ordinal);
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "iott-verification-ftp-" + Guid.NewGuid().ToString("N"));
    private readonly Task _accept;
    public ConcurrentQueue<string> Commands { get; } = new();
    public bool RejectMkdir { get; set; }
    public bool RejectCleanup { get; set; }
    public bool CorruptReadBack { get; set; }
    public bool AppendReadBackByte { get; set; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int RemainingFiles => _files.Count;
    public int RemainingDirectories => _directories.Count;

    public VerificationFtpPeer()
    {
        Directory.CreateDirectory(_temp);
        _listener.Start();
        _accept = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _sessions.Add(SessionAsync(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }

    private async Task SessionAsync(TcpClient control)
    {
        using (control)
        {
            var ct = _stop.Token;
            using var reader = new StreamReader(control.GetStream(), Encoding.UTF8, false, 4096, true);
            await using var writer = new StreamWriter(control.GetStream(), new UTF8Encoding(false), 4096, true) { AutoFlush = true, NewLine = "\r\n" };
            // No UTF-8 BOM on the control channel.
            TcpListener? dataListener = null;
            try
            {
                await writer.WriteLineAsync("220 Loopback verification fixture");
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    Commands.Enqueue(line.StartsWith("PASS ", StringComparison.Ordinal) ? "PASS [fixture]" : line);
                    var split = line.IndexOf(' ');
                    var command = (split < 0 ? line : line[..split]).ToUpperInvariant();
                    var argument = split < 0 ? "" : line[(split + 1)..];
                    switch (command)
                    {
                        case "USER": await writer.WriteLineAsync("331 Password required"); break;
                        case "PASS": await writer.WriteLineAsync("230 Logged in"); break;
                        case "SYST": await writer.WriteLineAsync("215 UNIX Type: L8"); break;
                        case "FEAT": await writer.WriteAsync("211-Features\r\n SIZE\r\n UTF8\r\n EPSV\r\n211 End\r\n"); break;
                        case "OPTS": case "TYPE": case "NOOP": await writer.WriteLineAsync("200 OK"); break;
                        case "PWD": await writer.WriteLineAsync("257 \"/\" is current directory"); break;
                        case "CWD": await writer.WriteLineAsync("250 Directory changed"); break;
                        case "EPSV": case "PASV":
                            dataListener?.Stop();
                            dataListener = new TcpListener(IPAddress.Loopback, 0);
                            dataListener.Start();
                            var port = ((IPEndPoint)dataListener.LocalEndpoint).Port;
                            await writer.WriteLineAsync(command == "EPSV" ? $"229 Entering Extended Passive Mode (|||{port}|)"
                                : $"227 Entering Passive Mode (127,0,0,1,{port / 256},{port % 256})");
                            break;
                        case "MKD":
                            await writer.WriteLineAsync(!RejectMkdir && _directories.TryAdd(argument, 0)
                                ? $"257 \"{argument}\" created" : "550 Directory exists or creation denied");
                            break;
                        case "SIZE":
                            await writer.WriteLineAsync(_files.TryGetValue(argument, out var sized) ? $"213 {new FileInfo(sized).Length}" : "550 No file");
                            break;
                        case "STOR":
                            if (dataListener is null) { await writer.WriteLineAsync("425 No data listener"); break; }
                            await writer.WriteLineAsync("150 Opening data connection");
                            using (var data = await dataListener.AcceptTcpClientAsync(ct))
                            {
                                var local = _files.GetOrAdd(argument, _ => Path.Combine(_temp, Guid.NewGuid().ToString("N")));
                                await using var output = new FileStream(local, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, true);
                                await data.GetStream().CopyToAsync(output, 81920, ct);
                            }
                            dataListener.Stop(); dataListener = null;
                            await writer.WriteLineAsync("226 Transfer complete");
                            break;
                        case "RETR":
                            if (dataListener is null || !_files.TryGetValue(argument, out var path))
                            { await writer.WriteLineAsync("550 No file"); break; }
                            await writer.WriteLineAsync("150 Opening data connection");
                            using (var data = await dataListener.AcceptTcpClientAsync(ct))
                            {
                                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                                if (CorruptReadBack)
                                {
                                    var first = input.ReadByte();
                                    if (first >= 0) await data.GetStream().WriteAsync(new[] { (byte)(first ^ 0xff) }, ct);
                                }
                                await input.CopyToAsync(data.GetStream(), 81920, ct);
                                if (AppendReadBackByte) await data.GetStream().WriteAsync(new byte[] { 1 }, ct);
                            }
                            dataListener.Stop(); dataListener = null;
                            await writer.WriteLineAsync("226 Transfer complete");
                            break;
                        case "DELE":
                            if (!RejectCleanup && _files.TryRemove(argument, out var removed))
                            { File.Delete(removed); await writer.WriteLineAsync("250 Deleted"); }
                            else await writer.WriteLineAsync("550 Delete failed");
                            break;
                        case "RMD":
                            var empty = !_files.Keys.Any(key => key.StartsWith(argument + "/", StringComparison.Ordinal));
                            await writer.WriteLineAsync(!RejectCleanup && empty && _directories.TryRemove(argument, out _)
                                ? "250 Removed" : "550 Not empty or unavailable");
                            break;
                        case "QUIT": await writer.WriteLineAsync("221 Goodbye"); return;
                        default: await writer.WriteLineAsync("502 Not implemented by loopback fixture"); break;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (IOException) { }
            catch (SocketException) { }
            finally { dataListener?.Stop(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _accept;
        await Task.WhenAll(_sessions).WaitAsync(TimeSpan.FromSeconds(5));
        _stop.Dispose();
        Directory.Delete(_temp, true);
    }
}
