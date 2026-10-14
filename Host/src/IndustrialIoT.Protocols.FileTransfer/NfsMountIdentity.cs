using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("IndustrialIoT.Protocols.FileTransfer.Tests")]

namespace IndustrialIoT.Protocols.FileTransfer;

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal sealed partial record NfsMountIdentity(string MountKey, string FileKey, string ResolvedPath, string Source)
{
    public string Fingerprint => $"{MountKey}\n{FileKey}\n{ResolvedPath}";

    public static NfsMountIdentity Read(string path, string host)
    {
        var fullPath = Path.GetFullPath(path);
        RejectLinks(fullPath);
        using var handle = OpenIdentityHandle(fullPath);
        return ReadHandle(handle, fullPath, host);
    }

    public static NfsMountIdentity ReadHandle(SafeFileHandle handle, string path, string host)
    {
        if (OperatingSystem.IsWindows()) return ReadWindows(handle, host);
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("NFS verification requires Linux mountinfo or Windows remote protocol information.");
        var descriptor = handle.DangerousGetHandle().ToInt64();
        var fields = File.ReadAllLines($"/proc/self/fdinfo/{descriptor}")
            .Select(line => line.Split(':', 2)).Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1].Trim(), StringComparer.Ordinal);
        if (!fields.TryGetValue("mnt_id", out var mountId) || !fields.TryGetValue("ino", out var inode))
            throw new IOException("Linux fdinfo does not expose the NFS mount and inode identity.");
        var resolved = File.ResolveLinkTarget($"/proc/self/fd/{descriptor}", returnFinalTarget: true)?.FullName
            ?? throw new IOException("The opened NFS path could not be resolved.");
        if (resolved != Path.GetFullPath(path))
            throw new IOException("NFS path was redirected or replaced while opening its identity handle.");
        return ParseLinux(File.ReadAllLines("/proc/self/mountinfo"), mountId, inode, resolved, host);
    }

    internal static NfsMountIdentity ParseLinux(IEnumerable<string> lines, string mountId, string inode,
        string path, string host)
    {
        foreach (var line in lines)
        {
            var halves = line.Split(" - ", 2, StringSplitOptions.None);
            if (halves.Length != 2) continue;
            var mount = halves[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var fileSystem = halves[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (mount.Length < 6 || fileSystem.Length < 3 || mount[0] != mountId) continue;
            if (fileSystem[0] is not ("nfs" or "nfs4"))
                throw new IOException($"The opened path is on {fileSystem[0]}, not an identified NFS mount.");
            var source = Unescape(fileSystem[1]);
            var separator = source.IndexOf(":/", StringComparison.Ordinal);
            if (separator <= 0) throw new IOException("NFS mount source does not identify its server and export.");
            RequireHost(source[..separator], host);
            var mountPath = Unescape(mount[4]);
            if (path != mountPath && !path.StartsWith(mountPath.TrimEnd('/') + "/", StringComparison.Ordinal))
                throw new IOException("The opened path is outside the identified NFS mount.");
            var key = $"linux:{mountId}:{mount[2]}:{mount[3]}:{mountPath}:{fileSystem[0]}:{source}";
            return new(key, inode, path, source);
        }
        throw new IOException("The opened file's mount identity is absent from /proc/self/mountinfo.");
    }

    private static string Unescape(string value) => value.Replace("\\040", " ").Replace("\\011", "\t")
        .Replace("\\012", "\n").Replace("\\134", "\\");

    private static void RequireHost(string actual, string expected)
    {
        static string Normalize(string value) => value.Trim().Trim('[', ']').TrimEnd('.');
        if (string.IsNullOrWhiteSpace(expected) ||
            !string.Equals(Normalize(actual), Normalize(expected), StringComparison.OrdinalIgnoreCase))
            throw new IOException($"NFS mount server '{actual}' does not match configured host '{expected}'. Use the mounted server identity.");
    }

    private static void RejectLinks(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("NFS verification paths cannot traverse symbolic links or reparse points.");
        }
    }

    private static SafeFileHandle OpenIdentityHandle(string path)
    {
        if (OperatingSystem.IsWindows()) return OpenWindowsIdentityHandle(path);
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("NFS mount verification is unavailable on this operating system.");
        // Linux asm-generic/fcntl.h: O_RDONLY=0, O_CLOEXEC=(1<<19).
        var descriptor = OpenLinux(path, 1 << 19);
        if (descriptor < 0) throw new IOException($"NFS identity open failed (errno {Marshal.GetLastPInvokeError()}).");
        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenLinux([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
}
