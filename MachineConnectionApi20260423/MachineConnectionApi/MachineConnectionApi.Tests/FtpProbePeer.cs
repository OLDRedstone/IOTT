namespace MachineConnectionApi.Tests;

using System.Net;
using System.Net.Sockets;
using System.Text;

internal sealed class FtpProbePeer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _session;
    private readonly string _mode;
    private readonly Action? _onRead;
    private byte[] _stored = [];
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int Uploads { get; private set; }
    public int Reads { get; private set; }
    public int Deletes { get; private set; }
    public int RemovedDirectories { get; private set; }

    public FtpProbePeer(string mode, Action? onRead = null)
    {
        _mode = mode; _onRead = onRead;
        _listener.Start();
        _session = SessionAsync();
    }

    private async Task SessionAsync()
    {
        using var control = await _listener.AcceptTcpClientAsync(_stop.Token);
        using var reader = new StreamReader(control.GetStream(), Encoding.UTF8, false, 4096, true);
        await using var writer = new StreamWriter(control.GetStream(), new UTF8Encoding(false), 4096, true)
            { AutoFlush = true, NewLine = "\r\n" };
        TcpListener? dataListener = null;
        try
        {
            await writer.WriteLineAsync("220 Local regression FTP peer");
            while (await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                var command = line.Split(' ', 2)[0].ToUpperInvariant();
                switch (command)
                {
                    case "USER": await writer.WriteLineAsync("331 Password"); break;
                    case "PASS": await writer.WriteLineAsync("230 Logged in"); break;
                    case "SYST": await writer.WriteLineAsync("215 UNIX Type: L8"); break;
                    case "FEAT": await writer.WriteAsync("211-Features\r\n SIZE\r\n UTF8\r\n EPSV\r\n211 End\r\n"); break;
                    case "PWD": await writer.WriteLineAsync("257 \"/\" is current directory"); break;
                    case "CWD": await writer.WriteLineAsync("250 Directory changed"); break;
                    case "MKD": await writer.WriteLineAsync(_mode == "collision" ? "550 Exists" : "257 Created"); break;
                    case "EPSV": case "PASV":
                        dataListener?.Stop();
                        dataListener = new TcpListener(IPAddress.Loopback, 0);
                        dataListener.Start();
                        var port = ((IPEndPoint)dataListener.LocalEndpoint).Port;
                        await writer.WriteLineAsync(command == "EPSV" ? $"229 Passive (|||{port}|)"
                            : $"227 Passive (127,0,0,1,{port / 256},{port % 256})");
                        break;
                    case "LIST": case "NLST": case "MLSD":
                        await writer.WriteLineAsync("150 Listing");
                        using (await dataListener!.AcceptTcpClientAsync(_stop.Token)) { }
                        dataListener.Stop(); dataListener = null;
                        await writer.WriteLineAsync("226 Listing complete");
                        break;
                    case "SIZE":
                        await writer.WriteLineAsync(_stored.Length == 0 ? "550 Missing" : $"213 {_stored.Length}");
                        break;
                    case "STOR":
                        Uploads++;
                        if (_mode == "upload-failure")
                        {
                            dataListener?.Stop(); dataListener = null;
                            await writer.WriteLineAsync("550 Upload denied"); break;
                        }
                        await writer.WriteLineAsync("150 Upload");
                        using (var data = await dataListener!.AcceptTcpClientAsync(_stop.Token))
                        {
                            using var memory = new MemoryStream();
                            await data.GetStream().CopyToAsync(memory, _stop.Token);
                            _stored = memory.ToArray();
                        }
                        dataListener.Stop(); dataListener = null;
                        await writer.WriteLineAsync("226 Upload complete");
                        break;
                    case "RETR":
                        Reads++; _onRead?.Invoke();
                        await writer.WriteLineAsync("150 Download");
                        using (var data = await dataListener!.AcceptTcpClientAsync(_stop.Token))
                        {
                            var content = (byte[])_stored.Clone();
                            if (_mode.StartsWith("corrupt", StringComparison.Ordinal)) content[0] ^= 0xff;
                            if (_mode == "short") content = content[..^1];
                            await data.GetStream().WriteAsync(content, _stop.Token);
                        }
                        dataListener.Stop(); dataListener = null;
                        await writer.WriteLineAsync("226 Download complete");
                        break;
                    case "DELE":
                        Deletes++;
                        if (!_mode.Contains("cleanup-failure")) _stored = [];
                        await writer.WriteLineAsync(_stored.Length == 0 ? "250 Deleted" : "550 Delete denied");
                        break;
                    case "RMD":
                        RemovedDirectories++;
                        await writer.WriteLineAsync(_stored.Length == 0 ? "250 Removed" : "550 Not empty");
                        break;
                    case "QUIT": await writer.WriteLineAsync("221 Bye"); return;
                    default: await writer.WriteLineAsync("200 OK"); break;
                }
            }
        }
        finally { dataListener?.Stop(); }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop();
        try { await _session; }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
        _stop.Dispose();
    }
}
