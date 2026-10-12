namespace IndustrialIoT.Protocols.FileTransfer;

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class NfsDirectReader
{
    // Overalign both the allocation and requests. Unsupported device alignment fails native I/O.
    private const int BlockSize = 1024 * 1024;

    internal static Task CopyToAsync(FileStream uploaded, Stream destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uploaded);
        ArgumentNullException.ThrowIfNull(destination);
        return Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();
            using var direct = OpenIndependent(uploaded);
            var allocation = Marshal.AllocHGlobal(BlockSize * 2 - 1);
            var aligned = (nint)(((nuint)allocation + BlockSize - 1) & ~(nuint)(BlockSize - 1));
            try
            {
                var buffer = new byte[BlockSize];
                long position = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    // Re-read an aligned final block after a short read; do not issue an unaligned EOF probe.
                    var offset = position - position % BlockSize;
                    var prefix = checked((int)(position - offset));
                    var count = OperatingSystem.IsWindows()
                        ? ReadWindows(direct, aligned, offset, ct) : ReadLinux(direct, aligned, offset, ct);
                    ct.ThrowIfCancellationRequested();
                    if (count < prefix) throw new IOException("The direct-read file shrank during verification.");
                    if (count == prefix) return;
                    Marshal.Copy(aligned + prefix, buffer, 0, count - prefix);
                    await destination.WriteAsync(buffer.AsMemory(0, count - prefix), ct).ConfigureAwait(false);
                    position = checked(offset + count);
                }
            }
            finally { Marshal.FreeHGlobal(allocation); }
        }, ct);
    }

    private static SafeFileHandle OpenIndependent(FileStream uploaded)
    {
        if (OperatingSystem.IsWindows())
        {
            // ReOpenFile: GENERIC_READ, SHARE_READ|WRITE|DELETE, NO_BUFFERING|OVERLAPPED.
            // The original upload handle must share Read; this handle shares its existing Write/Delete access.
            var handle = ReOpenFile(uploaded.SafeFileHandle, 0x80000000, 7, 0x20000000 | 0x40000000);
            if (!handle.IsInvalid) return handle;
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw NativeError("ReOpenFile(NO_BUFFERING)", error);
        }
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException("Direct NFS verification supports Windows and Linux x64/arm64 only.");
        var original = uploaded.SafeFileHandle;
        var retained = false;
        try
        {
            original.DangerousAddRef(ref retained);
            // Linux asm-generic/fcntl.h: O_RDONLY=0, O_DIRECT=1<<14, O_CLOEXEC=1<<19.
            var descriptor = OpenLinux($"/proc/self/fd/{original.DangerousGetHandle().ToInt64()}", (1 << 14) | (1 << 19));
            if (descriptor < 0) throw NativeError("open(O_DIRECT)", Marshal.GetLastPInvokeError());
            return new SafeFileHandle((nint)descriptor, ownsHandle: true);
        }
        finally { if (retained) original.DangerousRelease(); }
    }

    private static int ReadLinux(SafeFileHandle handle, nint buffer, long offset, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var count = Pread(handle.DangerousGetHandle().ToInt32(), buffer, BlockSize, offset);
            var error = Marshal.GetLastPInvokeError();
            ct.ThrowIfCancellationRequested();
            if (count >= 0) return checked((int)count);
            if (error != 4) throw NativeError("pread(O_DIRECT)", error); // Linux EINTR.
        }
    }

    private static int ReadWindows(SafeFileHandle handle, nint buffer, long offset, CancellationToken ct)
    {
        using var completed = new EventWaitHandle(false, EventResetMode.ManualReset);
        var overlap = Marshal.AllocHGlobal(Marshal.SizeOf<NativeIo>());
        try
        {
            Marshal.StructureToPtr(new NativeIo { Offset = (uint)offset, OffsetHigh = (uint)(offset >> 32),
                Event = completed.SafeWaitHandle.DangerousGetHandle() }, overlap, false);
            var started = ReadFile(handle, buffer, BlockSize, nint.Zero, overlap);
            var error = Marshal.GetLastPInvokeError();
            if (!started && error != 997) // ERROR_IO_PENDING.
            {
                if (error == 38) return 0; // ERROR_HANDLE_EOF.
                throw NativeError("ReadFile(NO_BUFFERING)", error);
            }
            // Register only after issuing I/O, then drain completion before releasing OVERLAPPED/buffer.
            using var cancellation = ct.Register(() => { CancelIoEx(handle, overlap); });
            var success = GetOverlappedResult(handle, overlap, out var count, true);
            error = Marshal.GetLastPInvokeError();
            ct.ThrowIfCancellationRequested();
            if (success) return checked((int)count);
            if (error == 38) return 0;
            throw NativeError("GetOverlappedResult(NO_BUFFERING)", error);
        }
        finally { Marshal.FreeHGlobal(overlap); }
    }

    private static IOException NativeError(string operation, int error) =>
        new($"{operation} failed ({error}); cached read-back is not permitted.", new Win32Exception(error));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeIo
    {
        public nuint Internal;
        public nuint InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public nint Event;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle ReOpenFile(SafeFileHandle original, uint access, uint share, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(SafeFileHandle file, nint buffer, uint size, nint bytesRead, nint overlap);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(SafeFileHandle file, nint overlap, out uint count, [MarshalAs(UnmanagedType.Bool)] bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafeFileHandle file, nint overlap);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenLinux([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "pread", SetLastError = true)]
    private static extern nint Pread(int descriptor, nint buffer, nuint size, long offset);
}
