namespace IndustrialIoT.Protocols.FileTransfer;

using FluentFTP;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using Microsoft.Extensions.Logging;

public partial class FtpTransferDriver : IVerificationFileTransfer
{
    public async Task<IVerificationFileLease> CreateVerificationFileAsync(
        string directory, string fileName, CancellationToken ct)
    {
        VerificationTransferPolicy.Validate(directory, fileName);
        EnsureConnected();
        ct.ThrowIfCancellationRequested();

        // RFC 959 MKD success (257) means a NEW directory was created. Do not use FluentFTP's
        // CreateDirectory helper: that helper can treat a pre-existing directory as success.
        // Derive the namespace from the run filename so retries encounter a collision, not reuse.
        var ownedDirectory = directory.TrimEnd('/') + "/.iott-" + fileName[..39];
        FtpReply reply;
        try { reply = await _client!.Execute("MKD " + ownedDirectory, ct); }
        catch (Exception ex)
        {
            // A lost MKD reply does not establish ownership. Do not delete a possibly pre-existing
            // directory; retain the candidate path for manual reconciliation of an uncertain create.
            _logger.LogWarning("FTP verification directory acquisition unconfirmed for {Path}: {ErrorType}; no unowned cleanup attempted",
                ownedDirectory, ex.GetType().Name);
            throw;
        }
        if (reply.Code != "257")
            throw new IOException($"Exclusive FTP test directory was not created (MKD {reply.Code}); no upload or cleanup was attempted.");
        // No cancellation check between successful acquisition and handing ownership to the caller.
        return new FtpVerificationLease(_client, ownedDirectory, fileName);
    }

    private sealed class FtpVerificationLease(AsyncFtpClient client, string directory, string fileName) : IVerificationFileLease
    {
        private bool _uploadAttempted;
        private bool _cleaned;
        private long _uploadedBytes;
        public string RemotePath { get; } = directory + "/" + fileName;

        public async Task<long> UploadAsync(Stream source, long length, CancellationToken ct)
        {
            if (_uploadAttempted || _cleaned) throw new InvalidOperationException("A verification lease allows only one upload.");
            ct.ThrowIfCancellationRequested();
            if (!source.CanSeek || source.Position != 0 || source.Length != length || length is <= 0 or > VerificationTransferPolicy.MaximumFileBytes)
                throw new ArgumentException("Verification source length or position is invalid.");
            _uploadAttempted = true;
            // STOR is confined to the namespace atomically acquired above. No recursive mkdir,
            // listing/check-then-overwrite, delete-and-retry, resume, or overwrite fallback.
            var status = await client.UploadStream(source, RemotePath, FtpRemoteExists.NoCheck,
                createRemoteDir: false, token: ct);
            ct.ThrowIfCancellationRequested();
            if (status != FtpStatus.Success || source.Position != length)
                throw new IOException("FTP verification upload did not consume the complete source.");
            _uploadedBytes = length;
            return length;
        }

        public async Task DownloadAsync(Stream destination, CancellationToken ct)
        {
            if (!_uploadAttempted || _cleaned) throw new InvalidOperationException("No owned verification upload is available.");
            // DownloadStream can turn a destination IOException into success on a 226 reply.
            // Read RETR to EOF without SIZE, then validate both bytes and the final control reply.
            long downloaded = 0;
            await using (var source = await client.OpenRead(RemotePath, FtpDataType.Binary,
                restart: 0, fileLen: -1, token: ct))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) != 0)
                {
                    var accepted = (int)Math.Min(read, Math.Max(0, _uploadedBytes - downloaded));
                    if (accepted > 0) await destination.WriteAsync(buffer.AsMemory(0, accepted), ct);
                    downloaded += read;
                }
            }
            var completion = await client.GetReply(ct);
            if (completion.Code is not ("226" or "250"))
                throw new IOException($"FTP verification RETR did not complete: {completion.Code}.");
            if (downloaded != _uploadedBytes)
                throw new IOException("FTP verification read-back length differs from the uploaded source.");
            ct.ThrowIfCancellationRequested();
        }

        public async Task CleanupAsync(CancellationToken ct)
        {
            if (_cleaned) return;
            ct.ThrowIfCancellationRequested();
            string? deleteCode = null;
            if (_uploadAttempted)
            {
                var deletion = await client.Execute("DELE " + RemotePath, ct);
                deleteCode = deletion.Code;
                // Cancellation before STOR may leave an empty owned directory and DELE returns 550.
                // A successful nonrecursive RMD still proves both owned objects are gone.
            }
            // Never use DeleteDirectory: recursive deletion could remove unrelated objects.
            var removal = await client.Execute("RMD " + directory, ct);
            if (!removal.Success)
                throw new IOException($"FTP exclusive test cleanup failed (DELE {deleteCode ?? "not needed"}, RMD {removal.Code}).");
            _cleaned = true;
        }
    }
}
