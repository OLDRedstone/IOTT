namespace IndustrialIoT.Protocols.FileTransfer;

using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using SMBLibrary;
using SMBLibrary.Client;

public partial class SmbTransferDriver : IVerificationFileTransfer
{
    public Task<IVerificationFileLease> CreateVerificationFileAsync(
        string directory, string fileName, CancellationToken ct)
    {
        VerificationTransferPolicy.Validate(directory, fileName);
        EnsureConnected();
        var store = _fileStore!;
        var path = FileTransferRemotePath.CombineSmbDirectory(directory, fileName);
        var readSize = (int)Math.Min(BufferSize, _smbClient!.MaxReadSize == 0 ? BufferSize : _smbClient.MaxReadSize);
        var writeSize = (int)Math.Min(BufferSize, _smbClient.MaxWriteSize == 0 ? BufferSize : _smbClient.MaxWriteSize);
        return Task.Run<IVerificationFileLease>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var status = store.CreateFile(out var handle, out var created, path,
                AccessMask.GENERIC_READ | AccessMask.GENERIC_WRITE | AccessMask.DELETE | AccessMask.SYNCHRONIZE,
                SMBLibrary.FileAttributes.Normal, ShareAccess.None, CreateDisposition.FILE_CREATE,
                CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
            if (status != NTStatus.STATUS_SUCCESS)
                throw new IOException($"SMB exclusive verification create failed: {status}; no existing file was changed.");
            if (created != FileStatus.FILE_CREATED)
            {
                store.CloseFile(handle);
                throw new IOException("SMB server did not confirm a newly created file; no deletion attempted.");
            }
            // Hand ownership to the caller even if cancellation arrived during the synchronous request.
            return new SmbVerificationLease(store, handle, path, readSize, writeSize);
        }, ct);
    }

    private sealed class SmbVerificationLease(ISMBFileStore store, object handle, string path,
        int readSize, int writeSize) : IVerificationFileLease
    {
        private bool _attempted;
        private bool _uploaded;
        private bool _closed;
        private bool _deleted;
        public string RemotePath => path;

        public Task<long> UploadAsync(Stream source, long length, CancellationToken ct) => Task.Run(async () =>
        {
            if (_attempted || _closed) throw new InvalidOperationException("A verification lease allows one upload.");
            ct.ThrowIfCancellationRequested();
            if (!source.CanSeek || source.Position != 0 || source.Length != length ||
                length is <= 0 or > VerificationTransferPolicy.MaximumFileBytes)
                throw new ArgumentException("Verification source length or position is invalid.");
            _attempted = true;
            var buffer = new byte[writeSize];
            long transferred = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, ct)) > 0)
            {
                var pending = count == buffer.Length ? buffer : buffer[..count];
                var written = 0;
                while (written < count)
                {
                    ct.ThrowIfCancellationRequested();
                    var data = written == 0 ? pending : pending[written..];
                    var status = store.WriteFile(out var accepted, handle, transferred + written, data);
                    if (status != NTStatus.STATUS_SUCCESS || accepted <= 0 || accepted > data.Length)
                        throw new IOException($"SMB verification write failed: {status}, accepted={accepted}.");
                    written += accepted;
                }
                transferred += count;
                if (transferred > length) throw new IOException("SMB source exceeded its declared length.");
            }
            ct.ThrowIfCancellationRequested();
            if (transferred != length || store.FlushFileBuffers(handle) != NTStatus.STATUS_SUCCESS)
                throw new IOException("SMB verification upload was incomplete or could not be flushed.");
            _uploaded = true;
            return transferred;
        }, ct);

        public Task DownloadAsync(Stream destination, CancellationToken ct) => Task.Run(async () =>
        {
            if (!_uploaded || _closed) throw new InvalidOperationException("No completed verification upload is available.");
            long offset = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var status = store.ReadFile(out var data, handle, offset, readSize);
                ct.ThrowIfCancellationRequested();
                if (status == NTStatus.STATUS_END_OF_FILE) return;
                if (status != NTStatus.STATUS_SUCCESS || data is null || data.Length == 0)
                    throw new IOException($"SMB verification read-back failed at {offset}: {status}.");
                if (data.Length > VerificationTransferPolicy.MaximumFileBytes - offset)
                    throw new IOException("SMB verification read-back exceeded the maximum size.");
                await destination.WriteAsync(data, ct);
                offset += data.Length;
            }
        }, ct);

        public Task CleanupAsync(CancellationToken ct) => Task.Run(() =>
        {
            if (_closed)
            {
                if (!_deleted) throw new IOException("SMB verification deletion was not confirmed.");
                return;
            }
            ct.ThrowIfCancellationRequested();
            NTStatus deletion;
            try
            {
                // Delete by the owned, exclusively opened handle; never reopen or delete a path.
                deletion = store.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true });
            }
            finally
            {
                _closed = store.CloseFile(handle) == NTStatus.STATUS_SUCCESS;
            }
            _deleted = deletion == NTStatus.STATUS_SUCCESS && _closed;
            if (!_deleted) throw new IOException($"SMB owned file cleanup unconfirmed: {deletion}.");
        }, ct);
    }
}
