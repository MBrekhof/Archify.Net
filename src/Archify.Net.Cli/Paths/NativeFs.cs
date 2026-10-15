using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Archify.Net.Cli.Paths;

// Node's stat dev/ino/nlink on Windows: volume serial, 64-bit file index, link count (GetFileInformationByHandle).
internal readonly record struct FileIdentity(uint VolumeSerial, ulong FileIndex, uint LinkCount);

// The slice of fs.Stats path-semantics reads. Identity is null where it is unavailable (off Windows).
internal sealed record FsStat(bool IsDirectory, bool IsSymbolicLink, FileIdentity? Identity);

// A filesystem error carrying Node's error code; upstream copies error.code into reason.systemCode.
internal sealed class NativeFsException(string code, string message) : IOException(message)
{
    public string Code { get; } = code;
}

// The filesystem calls path-semantics makes (realpathSync.native, statSync, lstatSync, readlinkSync, openSync 'wx', unlinkSync).
// NativeFs.Ops is the real one; tests substitute fakes the way upstream mocks node:fs.
internal interface IPathFs
{
    string RealPath(string path);
    FsStat Stat(string path);
    FsStat LStat(string path);
    string ReadLink(string path);
    IDisposable CreateExclusive(string path);
    void Delete(string path);
}

// Q6: native path identity. Windows: CreateFileW(FILE_FLAG_BACKUP_SEMANTICS) + GetFinalPathNameByHandleW / GetFileInformationByHandle,
// which is what libuv does for realpathSync.native and stat. Off Windows: the ResolveLinkTarget chain and no identity (stated deviation:
// hard-link aliases are not detected there).
internal static partial class NativeFs
{
    public static readonly IPathFs Ops = new NativeOps();

    sealed class NativeOps : IPathFs
    {
        public string RealPath(string path) => NativeFs.RealPath(path);
        public FsStat Stat(string path) => NativeFs.Stat(path);
        public FsStat LStat(string path) => NativeFs.LStat(path);
        public string ReadLink(string path) => NativeFs.ReadLink(path);
        // ponytail: openSync(path, 'wx', 0o600) without the mode; Windows has no POSIX mode bits.
        public IDisposable CreateExclusive(string path) =>
            File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        public void Delete(string path) => File.Delete(path);
    }

    const uint FileReadAttributes = 0x80;
    const uint FileShareAll = 0x1 | 0x2 | 0x4;
    const uint OpenExisting = 3;
    const uint FileFlagBackupSemantics = 0x02000000;
    const uint FileFlagOpenReparsePoint = 0x00200000;
    const uint FileAttributeDirectory = 0x10;
    const uint FileAttributeReparsePoint = 0x400;
    const uint IoReparseTagSymlink = 0xA000000C;
    const uint IoReparseTagMountPoint = 0xA0000003;
    const int FileAttributeTagInfoClass = 9;
    const uint VolumeNameDos = 0;

    // fs.realpathSync.native: every link, 8.3 alias and case spelling resolved; `\\?\` and `\\?\UNC\` stripped as libuv does.
    // ponytail: paths are passed to CreateFileW as given (no `\\?\` added), as libuv does; beyond MAX_PATH that needs LongPathsEnabled.
    public static string RealPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return PortableRealPath(path);
        using var handle = Open(path, followLinks: true, "realpath");
        var buffer = new char[512];
        while (true)
        {
            var length = GetFinalPathNameByHandleW(handle, buffer, VolumeNameDos);
            if (length == 0) throw Error(Marshal.GetLastPInvokeError(), "realpath", path);
            if (length < buffer.Length)
            {
                var final = new string(buffer, 0, (int)length);
                if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + final[8..];
                return final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
            }
            buffer = new char[length];   // too small: length is the size needed, terminator included
        }
    }

    // stat (follows links). Null off Windows.
    public static FileIdentity? Identity(string path) => Stat(path).Identity;

    public static FsStat Stat(string path) => OperatingSystem.IsWindows() ? StatHandle(path, followLinks: true) : PortableStat(path, followLinks: true);

    // lstat: libuv reports symbolic links and junctions (mount points) as links. ponytail: a volume mount point is a junction too and
    // counts as a link here (libuv treats it as a directory); readlink then yields `\\?\Volume{…}`, which path-semantics rejects.
    public static FsStat LStat(string path) => OperatingSystem.IsWindows() ? StatHandle(path, followLinks: false) : PortableStat(path, followLinks: false);

    // readlinkSync: the stored target, relative targets kept relative. ponytail: .NET's LinkTarget, which reads the print name
    // where libuv reads the substitute name; they agree for links made by mklink, CreateSymbolicLink and Node.
    public static string ReadLink(string path) =>
        new FileInfo(path).LinkTarget ?? throw new NativeFsException("EINVAL", $"EINVAL: invalid argument, readlink '{path}'");

    // Node's error.code for an exception from this class or from System.IO: a subset of libuv's uv_translate_sys_error; an unmapped
    // Win32 error reads UNKNOWN like libuv's, and a non-filesystem exception has no code.
    public static string? ErrorCode(Exception error) => error switch
    {
        NativeFsException native => native.Code,
        _ when (error.HResult & 0xFFFF0000) == 0x80070000 => CodeFor(error.HResult & 0xFFFF),
        _ => null,
    };

    static string CodeFor(int win32Error) => win32Error switch
    {
        1 => "EISDIR",
        2 or 3 or 15 or 123 or 126 or 4392 => "ENOENT",
        5 or 1314 => "EPERM",
        6 => "EBADF",
        17 => "EXDEV",
        19 => "EROFS",
        23 or 1117 => "EIO",
        32 or 33 or 231 => "EBUSY",
        39 or 112 => "ENOSPC",
        50 => "ENOTSUP",
        80 or 183 => "EEXIST",
        87 or 4390 or 1464 => "EINVAL",
        145 => "ENOTEMPTY",
        206 => "ENAMETOOLONG",
        267 => "ENOTDIR",
        998 or 1920 => "EACCES",
        1921 => "ELOOP",
        _ => "UNKNOWN",
    };

    static NativeFsException Error(int win32Error, string operation, string path)
    {
        var code = CodeFor(win32Error);
        return new NativeFsException(code, $"{code}: {operation} '{path}' (Win32 error {win32Error})");
    }

    static SafeFileHandle Open(string path, bool followLinks, string operation)
    {
        var flags = FileFlagBackupSemantics | (followLinks ? 0 : FileFlagOpenReparsePoint);
        var handle = CreateFileW(path, FileReadAttributes, FileShareAll, 0, OpenExisting, flags, 0);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw Error(error, operation, path);
    }

    static FsStat StatHandle(string path, bool followLinks)
    {
        var operation = followLinks ? "stat" : "lstat";
        using var handle = Open(path, followLinks, operation);
        if (!GetFileInformationByHandle(handle, out var info)) throw Error(Marshal.GetLastPInvokeError(), operation, path);
        var identity = new FileIdentity(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, info.NumberOfLinks);
        var isLink = false;
        if (!followLinks && (info.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, out var tag, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
                throw Error(Marshal.GetLastPInvokeError(), operation, path);
            isLink = tag.ReparseTag is IoReparseTagSymlink or IoReparseTagMountPoint;
        }
        return new FsStat(!isLink && (info.FileAttributes & FileAttributeDirectory) != 0, isLink, identity);
    }

    // ponytail: off Windows (stated deviation, Q6) — GetFullPath collapses `..` lexically before links are resolved, and each
    // component's link is resolved once with ResolveLinkTarget. libc realpath/stat via P/Invoke if Linux ever matters.
    internal static string PortableRealPath(string path)
    {
        var missing = new NativeFsException("ENOENT", $"ENOENT: no such file or directory, realpath '{path}'");
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full) ?? "";
            var current = root;
            foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Join(current, part);
                if (new FileInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target) current = target.FullName;
            }
            return Path.Exists(current) ? current : throw missing;
        }
        catch (IOException error) when (error is not NativeFsException) { throw Coded(error, missing); }
    }

    // The fallbacks' System.IO errors as Node codes: not-found is ENOENT; a path through a file reads ENOTDIR, as libuv maps it.
    static NativeFsException Coded(IOException error, NativeFsException missing) =>
        error is FileNotFoundException or DirectoryNotFoundException || ErrorCode(error) is not { } code || code == "ENOENT" ? missing
            : new NativeFsException(code, error.Message);

    internal static FsStat PortableStat(string path, bool followLinks)
    {
        var missing = new NativeFsException("ENOENT", $"ENOENT: no such file or directory, {(followLinks ? "stat" : "lstat")} '{path}'");
        try
        {
            if (!followLinks && new FileInfo(path).LinkTarget is not null) return new FsStat(false, true, null);
            if (Directory.Exists(path)) return new FsStat(true, false, null);
            if (File.Exists(path)) return new FsStat(false, false, null);
        }
        catch (IOException error) { throw Coded(error, missing); }
        throw missing;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow, CreationTimeHigh, LastAccessTimeLow, LastAccessTimeHigh, LastWriteTimeLow, LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh, FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandleW(SafeFileHandle file, char* filePath, uint filePathLength, uint flags);

    static unsafe uint GetFinalPathNameByHandleW(SafeFileHandle file, char[] filePath, uint flags)
    {
        fixed (char* buffer = filePath) return GetFinalPathNameByHandleW(file, buffer, (uint)filePath.Length, flags);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileAttributeTagInfo information,
        uint bufferSize);
}
