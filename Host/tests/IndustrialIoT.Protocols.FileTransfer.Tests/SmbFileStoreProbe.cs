namespace IndustrialIoT.Protocols.FileTransfer.Tests;

using System.Reflection;
using SMBLibrary;
using SMBLibrary.Client;

public class SmbFileStoreProbe : DispatchProxy
{
    public MemoryStream Contents { get; } = new();
    public int MaxWrite { get; set; } = int.MaxValue;
    public NTStatus ReadStatus { get; set; } = NTStatus.STATUS_SUCCESS;
    public long? ExpectedLength { get; set; }
    public bool EmptySuccessfulRead { get; set; }
    public int CloseCalls { get; private set; }
    public bool VerificationFileExists { get; set; }
    public bool CorruptReadBack { get; set; }
    public bool DeletePending { get; private set; }
    public int DeleteCalls { get; private set; }
    public Action? AfterWrite { get; set; }

    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(arguments);
        switch (method.Name)
        {
            case nameof(ISMBFileStore.CreateFile):
                if ((CreateDisposition)arguments[6]! == CreateDisposition.FILE_CREATE && VerificationFileExists)
                {
                    arguments[0] = null;
                    arguments[1] = FileStatus.FILE_EXISTS;
                    return NTStatus.STATUS_OBJECT_NAME_COLLISION;
                }
                if ((CreateDisposition)arguments[6]! == CreateDisposition.FILE_CREATE) VerificationFileExists = true;
                arguments[0] = new object();
                arguments[1] = FileStatus.FILE_CREATED;
                return NTStatus.STATUS_SUCCESS;
            case nameof(ISMBFileStore.CloseFile):
                CloseCalls++;
                if (DeletePending) VerificationFileExists = false;
                return NTStatus.STATUS_SUCCESS;
            case nameof(ISMBFileStore.FlushFileBuffers):
                return NTStatus.STATUS_SUCCESS;
            case nameof(ISMBFileStore.SetFileInformation):
                DeleteCalls++;
                DeletePending = ((FileDispositionInformation)arguments[1]!).DeletePending;
                return NTStatus.STATUS_SUCCESS;
            case nameof(ISMBFileStore.GetFileInformation):
                arguments[0] = new FileStandardInformation { EndOfFile = ExpectedLength ?? Contents.Length };
                return NTStatus.STATUS_SUCCESS;
            case nameof(ISMBFileStore.ReadFile):
                arguments[0] = Array.Empty<byte>();
                if (ReadStatus != NTStatus.STATUS_SUCCESS) return ReadStatus;
                if (EmptySuccessfulRead) return NTStatus.STATUS_SUCCESS;
                var offset = (long)arguments[2]!;
                if (offset >= Contents.Length) return NTStatus.STATUS_END_OF_FILE;
                var count = (int)Math.Min((int)arguments[3]!, Contents.Length - offset);
                arguments[0] = Contents.ToArray().AsSpan((int)offset, count).ToArray();
                if (CorruptReadBack) ((byte[])arguments[0]!)[0] ^= 0xff;
                return NTStatus.STATUS_SUCCESS;
            case nameof(ISMBFileStore.WriteFile):
                var data = (byte[])arguments[3]!;
                var accepted = Math.Min(MaxWrite, data.Length);
                arguments[0] = accepted;
                Contents.Position = (long)arguments[2]!;
                Contents.Write(data, 0, accepted);
                AfterWrite?.Invoke();
                return NTStatus.STATUS_SUCCESS;
            case nameof(ISMBFileStore.Disconnect):
                return NTStatus.STATUS_SUCCESS;
            default:
                throw new NotSupportedException(method.Name);
        }
    }
}
