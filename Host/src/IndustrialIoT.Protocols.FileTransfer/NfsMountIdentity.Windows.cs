namespace IndustrialIoT.Protocols.FileTransfer;

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

internal sealed partial record NfsMountIdentity
{
    // Values/layouts are from Microsoft's WinSDK wnnc.h, minwinbase.h, WinBase.h and fileapi.h.
    private const uint MicrosoftNfsProtocol = 0x00420000; // WNNC_NET_MS_NFS
    private const int FileRemoteProtocolInfo = 13;

    private static SafeFileHandle OpenWindowsIdentityHandle(string path)
    {
        var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw new IOException($"Windows NFS identity open failed (Win32 {error}).");
    }

    private static NfsMountIdentity ReadWindows(SafeFileHandle handle, string host)
    {
        var remote = new byte[512];
        if (!GetFileInformationByHandleEx(handle, FileRemoteProtocolInfo, remote, (uint)remote.Length))
            throw new IOException($"Windows does not expose reliable NFS remote protocol information (Win32 {Marshal.GetLastPInvokeError()}).");
        if (BitConverter.ToUInt16(remote, 0) == 0 || BitConverter.ToUInt16(remote, 2) < 20)
            throw new IOException("Windows returned incomplete NFS remote protocol information.");
        if (BitConverter.ToUInt32(remote, 4) != MicrosoftNfsProtocol)
            throw new IOException("The opened path is not reported as Microsoft NFS by Windows.");
        if ((BitConverter.ToUInt32(remote, 16) & 2) != 0)
            throw new IOException("Windows reports the NFS filesystem offline; cached files are not network evidence.");
        var name = new StringBuilder(32768);
        var count = GetFinalPathNameByHandleW(handle, name, (uint)name.Capacity, 0);
        if (count == 0 || count >= name.Capacity)
            throw new IOException($"Windows cannot resolve the NFS server/export path (Win32 {Marshal.GetLastPInvokeError()}).");
        var resolved = name.ToString();
        if (!resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Windows NFS provider did not expose a UNC server/export identity.");
        var parts = resolved[8..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new IOException("Windows NFS server/export identity is incomplete.");
        RequireHost(parts[0], host);
        var file = new byte[52]; // BY_HANDLE_FILE_INFORMATION: thirteen DWORDs.
        if (!GetFileInformationByHandle(handle, file))
            throw new IOException($"Windows cannot identify the opened NFS file (Win32 {Marshal.GetLastPInvokeError()}).");
        var volume = BitConverter.ToUInt32(file, 28);
        var fileIndex = ((ulong)BitConverter.ToUInt32(file, 44) << 32) | BitConverter.ToUInt32(file, 48);
        if (fileIndex == 0) throw new IOException("Windows NFS provider did not expose a usable file identity.");
        var source = $@"\\{parts[0]}\{parts[1]}";
        return new($"windows:{MicrosoftNfsProtocol}:{volume}:{source.ToUpperInvariant()}",
            fileIndex.ToString(), resolved.ToUpperInvariant(), source);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass,
        [Out] byte[] information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, [Out] byte[] information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);
}
