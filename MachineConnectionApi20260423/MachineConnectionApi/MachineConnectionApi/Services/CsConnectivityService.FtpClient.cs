using System.Diagnostics;
using System.Text;
using FluentFTP;
using MachineConnectionApi.Models;

namespace MachineConnectionApi.Services;

/// <summary>CsConnectivityService FTP 客户端部分：基于 FluentFTP 的真实 FTP 验证。</summary>
public sealed partial class CsConnectivityService
{
    private async Task ProbeFtpRoundTripAsync(AsyncFtpClient client, string directory, List<string> steps, CancellationToken ct)
    {
        var ownedDirectory = directory.TrimEnd('/') + $"/__cs_probe_{Guid.NewGuid():N}";
        var acquired = await client.Execute("MKD " + ownedDirectory, ct);
        if (acquired.Code != "257")
            throw new IOException($"测试目录创建未确认（MKD {acquired.Code}），未写入或清理：{ownedDirectory}");
        var name = ownedDirectory + "/probe.txt";
        var payload = Encoding.UTF8.GetBytes($"cs-ftp-probe {Now()}");
        Exception? transferError = null;
        try
        {
            var status = await client.UploadBytes(payload, name, FtpRemoteExists.NoCheck, false, token: ct);
            if (status != FtpStatus.Success) throw new IOException($"上传失败({status})");
            steps.Add("上传成功");
            var downloaded = await client.DownloadBytes(name, token: ct);
            ct.ThrowIfCancellationRequested();
            if (downloaded is null || !payload.AsSpan().SequenceEqual(downloaded))
                throw new IOException("下载校验不符：回读内容与上传内容不一致");
            steps.Add("下载内容逐字节校验一致");
        }
        catch (Exception ex) { transferError = ex; throw; }
        finally
        {
            try { await CleanupFtpProbeAsync(client, ownedDirectory, name); steps.Add("清理完成"); }
            catch (Exception ex) when (transferError is not null)
            { steps.Add($"清理未完成：{name}（{ex.Message}）"); }
        }
    }

    private static async Task CleanupFtpProbeAsync(AsyncFtpClient client, string directory, string name)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var deletion = await client.Execute("DELE " + name, cleanup.Token);
        // A failed upload may leave no file. Nonrecursive RMD confirms the owned directory is empty.
        var removal = await client.Execute("RMD " + directory, cleanup.Token);
        if (!removal.Success)
            throw new IOException($"测试文件清理未完成：{name}（DELE {deletion.Code}, RMD {removal.Code}）");
    }

    /// <summary>FTP 登录、列目录；fullRoundTrip 启用上传、实际回读内容校验及清理。</summary>
    public async Task<CsProbeResult> ProbeFtpClientAsync(CsGateway gw, bool fullRoundTrip, CancellationToken ct)
    {
        var port = gw.Port > 0 ? gw.Port : 21;
        var steps = new List<string>();
        var sw = Stopwatch.StartNew();
        var client = new AsyncFtpClient(gw.Ip, gw.Username ?? "", gw.Password ?? "", port);
        client.Config.ConnectTimeout = 5000;
        client.Config.ReadTimeout = 5000;
        client.Config.DataConnectionConnectTimeout = 5000;
        client.Config.DataConnectionReadTimeout = 5000;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(fullRoundTrip ? 20000 : 8000);
            var token = timeoutCts.Token;

            await client.Connect(token);
            steps.Add($"登录成功({port}端口可用)");

            var dir = string.IsNullOrWhiteSpace(gw.RemotePath) ? "/" : gw.RemotePath!.Trim();
            if (dir.Any(char.IsControl)) throw new ArgumentException("FTP 目录不能包含控制字符。");
            var listing = await client.GetListing(dir, token);
            steps.Add($"列目录{dir}={listing.Length}项");

            if (fullRoundTrip)
                await ProbeFtpRoundTripAsync(client, dir, steps, token);

            sw.Stop();
            try { await client.Disconnect(token); } catch { /* ignore */ }
            return new CsProbeResult(true, Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                "FTP " + string.Join("；", steps), Now());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            var done = steps.Count > 0 ? $"（已完成：{string.Join("；", steps)}）" : "";
            return new CsProbeResult(false, Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                $"FTP 失败：{ex.Message}{done}", Now());
        }
        finally
        {
            client.Dispose();
        }
    }
}
