using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("This tool only supports Windows NTFS volumes.");
    return 1;
}

var options = CliOptions.Parse(args);
if (options is null)
{
    CliOptions.PrintUsage();
    return 1;
}

if (!IsAdministrator())
{
    throw new InvalidOperationException("Must run as Administrator.");
}

Directory.CreateDirectory(Path.GetDirectoryName(options.DestinationFile) ?? ".");

var sourceInfo = new FileInfo(options.SourceFile);
if (!sourceInfo.Exists)
{
    throw new FileNotFoundException("Source file was not found.", options.SourceFile);
}

var sourceFileSize = sourceInfo.Length;

using var volumeStream = new FileStream(options.VolumePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
var ntfsBoot = NtfsReader.ReadNtfsBoot(volumeStream);

Console.WriteLine($"Source Full Path : {options.SourceFile}");
Console.WriteLine($"Source File Size : {sourceFileSize} bytes");
Console.WriteLine($"Cluster size: {ntfsBoot.ClusterSize} bytes");

switch (options.Mode)
{
    case CopyMode.Mft:
        {
            var fileMeta = NtfsNative.GetNtfsFileInfo(options.SourceFile);
            var mftRecordNumber = (int)fileMeta.MftRecordNumber;
            Console.WriteLine($"MFT Record #{mftRecordNumber}");

            var record = NtfsReader.ReadMftRecord(volumeStream, ntfsBoot, mftRecordNumber);
            var fileRecordInfo = NtfsReader.GetFileInfoFromRecord(record);
            NtfsReader.CopyByMftDataRuns(volumeStream, record, fileRecordInfo, ntfsBoot, options.DestinationFile);
            break;
        }
    case CopyMode.Metadata:
        {
            var extents = FsutilParser.GetFileExtents(options.SourceFile);
            Console.WriteLine($"Found {extents.Count} extent(s)");
            foreach (var extent in extents)
            {
                Console.WriteLine($"LCN={extent.Lcn}  LengthClusters={extent.LengthClusters}");
            }

            NtfsReader.CopyFileByExtents(options.VolumePath, extents, ntfsBoot.ClusterSize, sourceFileSize, options.DestinationFile);
            break;
        }
}

Console.WriteLine($"File copied successfully to {options.DestinationFile}");
return 0;

static bool IsAdministrator()
{
    using var identity = WindowsIdentity.GetCurrent();
    var principal = new WindowsPrincipal(identity);
    return principal.IsInRole(WindowsBuiltInRole.Administrator);
}

internal enum CopyMode
{
    Mft,
    Metadata
}

internal sealed record CliOptions(CopyMode Mode, string SourceFile, string DestinationFile, string VolumePath)
{
    public static CliOptions? Parse(string[] args)
    {
        if (args.Length is 0)
        {
            return null;
        }

        string? modeRaw = null;
        string? source = null;
        string? destination = null;
        string volume = @"\\.\C:";

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--mode":
                    modeRaw = ReadValue(args, ref i);
                    break;
                case "--source":
                    source = ReadValue(args, ref i);
                    break;
                case "--destination":
                    destination = ReadValue(args, ref i);
                    break;
                case "--volume":
                    volume = ReadValue(args, ref i);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        if (string.IsNullOrWhiteSpace(modeRaw) ||
            string.IsNullOrWhiteSpace(source) ||
            string.IsNullOrWhiteSpace(destination))
        {
            return null;
        }

        if (!Enum.TryParse<CopyMode>(modeRaw, ignoreCase: true, out var mode))
        {
            throw new ArgumentException("Mode must be either MFT or Metadata.");
        }

        return new CliOptions(mode, source, destination, volume);
    }

    public static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  UnderlayCopy --mode <MFT|Metadata> --source <path> --destination <path> [--volume \\\\.\\C:]");
    }

    private static string ReadValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for argument {args[index]}.");
        }

        index++;
        return args[index];
    }
}

internal static class FsutilParser
{
    public static IReadOnlyList<FileExtent> GetFileExtents(string sourceFile)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "fsutil",
                Arguments = $"file queryextents \"{sourceFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException($"fsutil failed: {error}");
        }

        var extents = new List<FileExtent>();
        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!rawLine.StartsWith("VCN:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var segments = rawLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var clustersHex = segments.SkipWhile(s => !s.Equals("Clusters:", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
            var lcnHex = segments.SkipWhile(s => !s.Equals("LCN:", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();

            if (clustersHex is null || lcnHex is null)
            {
                continue;
            }

            var clusters = Convert.ToInt64(clustersHex.Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16);
            var lcn = Convert.ToInt64(lcnHex.Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16);
            extents.Add(new FileExtent(lcn, clusters));
        }

        if (extents.Count == 0)
        {
            throw new InvalidOperationException($"No extents parsed from fsutil output. Raw:\n{output}\n{error}");
        }

        return extents;
    }
}

internal sealed record FileExtent(long Lcn, long LengthClusters);

internal static class NtfsReader
{
    public static NtfsBootInfo ReadNtfsBoot(FileStream stream)
    {
        var buffer = new byte[512];
        stream.Seek(0, SeekOrigin.Begin);
        _ = stream.Read(buffer, 0, buffer.Length);

        var bytesPerSector = BitConverter.ToUInt16(buffer, 11);
        var sectorsPerCluster = buffer[13];
        var clusterSize = bytesPerSector * sectorsPerCluster;
        var mftCluster = BitConverter.ToInt64(buffer, 48);
        return new NtfsBootInfo(bytesPerSector, sectorsPerCluster, clusterSize, mftCluster);
    }

    public static byte[] ReadMftRecord(FileStream stream, NtfsBootInfo boot, int recordNumber)
    {
        const int mftRecordSize = 1024;
        var mftOffset = boot.MftCluster * boot.ClusterSize;
        var recordOffset = mftOffset + (recordNumber * mftRecordSize);

        stream.Seek(recordOffset, SeekOrigin.Begin);
        var record = new byte[mftRecordSize];
        _ = stream.Read(record, 0, record.Length);
        return record;
    }

    public static ParsedMftFileInfo GetFileInfoFromRecord(byte[] record)
    {
        var attrOffset = BitConverter.ToUInt16(record, 20);
        var info = new ParsedMftFileInfo("<unknown>", 5, 0, null);

        while (attrOffset < record.Length)
        {
            var type = BitConverter.ToInt32(record, attrOffset);
            if (type == -1)
            {
                break;
            }

            var len = BitConverter.ToInt32(record, attrOffset + 4);
            var nonResident = record[attrOffset + 8];

            if (type == 0x30)
            {
                var parentRef = BitConverter.ToInt64(record, attrOffset + 24);
                var nameLen = record[attrOffset + 88];
                var nameBytes = record[(attrOffset + 90)..(attrOffset + 90 + (nameLen * 2))];
                var fileName = Encoding.Unicode.GetString(nameBytes);
                info = info with { FileName = fileName, ParentRef = parentRef & 0xFFFFFFFFFFFF };
            }

            if (type == 0x80)
            {
                if (nonResident == 0)
                {
                    info = info with { FileSize = BitConverter.ToInt64(record, attrOffset + 16) };
                }
                else
                {
                    var fileSize = BitConverter.ToInt64(record, attrOffset + 48);
                    var dataOffset = BitConverter.ToUInt16(record, attrOffset + 32);
                    var dataRuns = record[(attrOffset + dataOffset)..(attrOffset + len)];
                    info = info with { FileSize = fileSize, Runs = ParseDataRuns(dataRuns) };
                }
            }

            attrOffset += len;
        }

        return info;
    }

    public static void CopyByMftDataRuns(FileStream volumeStream, byte[] record, ParsedMftFileInfo info, NtfsBootInfo boot, string destinationFile)
    {
        using var output = File.Create(destinationFile);
        long bytesWritten = 0;

        if (info.Runs is { Count: > 0 })
        {
            foreach (var run in info.Runs)
            {
                var toRead = Math.Min(run.Length * boot.ClusterSize, info.FileSize - bytesWritten);
                if (toRead <= 0)
                {
                    break;
                }

                if (run.Lcn == 0)
                {
                    output.Write(new byte[(int)toRead], 0, (int)toRead);
                    bytesWritten += toRead;
                    continue;
                }

                var diskOffset = run.Lcn * boot.ClusterSize;
                if (diskOffset < 0)
                {
                    Console.Error.WriteLine($"Skipping invalid LCN: {run.Lcn}");
                    continue;
                }

                volumeStream.Seek(diskOffset, SeekOrigin.Begin);
                var buffer = new byte[(int)toRead];
                _ = volumeStream.Read(buffer, 0, (int)toRead);
                output.Write(buffer, 0, (int)toRead);
                bytesWritten += toRead;

                if (bytesWritten >= info.FileSize)
                {
                    break;
                }
            }
        }
        else if (info.FileSize > 0)
        {
            var attrOffset = BitConverter.ToUInt16(record, 20);
            while (attrOffset < record.Length)
            {
                var type = BitConverter.ToInt32(record, attrOffset);
                if (type == 0x80)
                {
                    var valueLength = (int)BitConverter.ToInt64(record, attrOffset + 16);
                    var valueOffset = BitConverter.ToUInt16(record, attrOffset + 20);
                    var data = record[valueOffset..(valueOffset + valueLength)];
                    output.Write(data, 0, data.Length);
                    break;
                }

                var len = BitConverter.ToInt32(record, attrOffset + 4);
                attrOffset += len;
            }
        }
    }

    public static void CopyFileByExtents(string volumePath, IReadOnlyList<FileExtent> extents, long clusterSize, long totalFileSize, string destinationFile, int chunkSize = 4 * 1024 * 1024)
    {
        using var device = new FileStream(volumePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var output = File.Open(destinationFile, FileMode.Create, FileAccess.Write, FileShare.None);

        var bytesRemaining = totalFileSize;
        var buffer = new byte[chunkSize];

        foreach (var extent in extents)
        {
            var extentBytes = extent.LengthClusters * clusterSize;
            var toCopy = Math.Min(extentBytes, bytesRemaining);
            if (toCopy <= 0)
            {
                break;
            }

            var startOffset = extent.Lcn * clusterSize;
            device.Seek(startOffset, SeekOrigin.Begin);

            long copied = 0;
            while (copied < toCopy)
            {
                var readSize = (int)Math.Min(buffer.Length, toCopy - copied);
                var read = 0;

                while (read < readSize)
                {
                    var chunk = device.Read(buffer, read, readSize - read);
                    if (chunk <= 0)
                    {
                        throw new EndOfStreamException("Unexpected end of device read");
                    }

                    read += chunk;
                }

                output.Write(buffer, 0, read);
                copied += read;
            }

            bytesRemaining -= copied;
            if (bytesRemaining <= 0)
            {
                break;
            }
        }
    }

    private static List<DataRun> ParseDataRuns(byte[] attr)
    {
        var runs = new List<DataRun>();
        var pos = 0;
        long currentLcn = 0;

        while (pos < attr.Length && attr[pos] != 0x00)
        {
            var header = attr[pos++];
            var lenSize = header & 0x0F;
            var offSize = (header >> 4) & 0x0F;

            long runLength = 0;
            for (var i = 0; i < lenSize; i++)
            {
                runLength |= (long)attr[pos++] << (8 * i);
            }

            long runOffset = 0;
            if (offSize > 0)
            {
                for (var i = 0; i < offSize; i++)
                {
                    runOffset |= (long)attr[pos++] << (8 * i);
                }

                if ((attr[pos - 1] & 0x80) != 0)
                {
                    runOffset -= 1L << (8 * offSize);
                }
            }

            currentLcn += runOffset;
            runs.Add(new DataRun(runLength, currentLcn));
        }

        return runs;
    }
}

internal sealed record NtfsBootInfo(ushort BytesPerSector, byte SectorsPerCluster, int ClusterSize, long MftCluster);
internal sealed record DataRun(long Length, long Lcn);
internal sealed record ParsedMftFileInfo(string FileName, long ParentRef, long FileSize, List<DataRun>? Runs);

internal static class NtfsNative
{
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    public static NtfsFileInfo GetNtfsFileInfo(string path)
    {
        var normalizedPath = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + path;

        var handle = CreateFileW(
            normalizedPath,
            FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateFileW failed for '{path}'");
        }

        using (handle)
        {
            if (!GetFileInformationByHandle(handle, out var info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"GetFileInformationByHandle failed for '{path}'");
            }

            ulong frn = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
            ulong mftRecord = frn & 0x0000FFFFFFFFFFFF;
            ulong sequenceNum = (frn >> 48) & 0xFFFF;
            ulong size = ((ulong)info.FileSizeHigh << 32) | info.FileSizeLow;

            return new NtfsFileInfo(
                path,
                $"0x{info.VolumeSerialNumber:X8}",
                frn,
                $"0x{frn:X16}",
                mftRecord,
                $"0x{mftRecord:X12}",
                sequenceNum,
                (info.FileAttributes & (uint)FileAttributes.Directory) != 0,
                info.NumberOfLinks,
                size);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint DwLowDateTime;
        public uint DwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}

internal sealed record NtfsFileInfo(
    string Path,
    string VolumeSerialNumber,
    ulong FileIdFrn64,
    string FileIdFrnHex,
    ulong MftRecordNumber,
    string MftRecordHex,
    ulong SequenceNumber,
    bool IsDirectory,
    uint Links,
    ulong Size);
