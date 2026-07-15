using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ZipFlow
{
    public interface ISourceRemover
    {
        void Remove(string path);
    }

    public interface IFolderLauncher
    {
        void Open(string path);
    }

    public sealed class ArchivePolicy
    {
        public readonly int MaxEntries;
        public readonly long MaxEntryBytes;
        public readonly long MaxTotalBytes;
        public readonly long MaxCompressionRatio;
        public readonly int MaxDepth;
        public readonly int MaxFullPath;

        private static readonly ArchivePolicy DefaultPolicy = new ArchivePolicy(
            10000,
            1024L * 1024L * 1024L,
            4L * 1024L * 1024L * 1024L,
            1000,
            32,
            240);

        public ArchivePolicy(int maxEntries, long maxEntryBytes, long maxTotalBytes, long maxCompressionRatio, int maxDepth, int maxFullPath)
        {
            if (maxEntries <= 0)
            {
                throw new ArgumentOutOfRangeException("maxEntries");
            }

            if (maxEntryBytes < 0)
            {
                throw new ArgumentOutOfRangeException("maxEntryBytes");
            }

            if (maxTotalBytes < 0)
            {
                throw new ArgumentOutOfRangeException("maxTotalBytes");
            }

            if (maxCompressionRatio <= 0)
            {
                throw new ArgumentOutOfRangeException("maxCompressionRatio");
            }

            if (maxDepth <= 0)
            {
                throw new ArgumentOutOfRangeException("maxDepth");
            }

            if (maxFullPath <= 0)
            {
                throw new ArgumentOutOfRangeException("maxFullPath");
            }

            MaxEntries = maxEntries;
            MaxEntryBytes = maxEntryBytes;
            MaxTotalBytes = maxTotalBytes;
            MaxCompressionRatio = maxCompressionRatio;
            MaxDepth = maxDepth;
            MaxFullPath = maxFullPath;
        }

        public static ArchivePolicy Default
        {
            get { return DefaultPolicy; }
        }
    }

    public sealed class ZipProcessor
    {
        private const uint EndOfCentralDirectorySignature = 0x06054b50U;
        private const uint CentralDirectorySignature = 0x02014b50U;
        private const uint LocalHeaderSignature = 0x04034b50U;
        private const int CopyBufferSize = 81920;

        private readonly ArchivePolicy policy;
        private readonly ISourceRemover remover;
        private readonly IFolderLauncher launcher;
        private readonly Action<string> beforeExtraction;
        private readonly Action<string> beforePublish;
        private readonly Action<string> afterMoveBeforeVisibility;
        private readonly Action<string> afterPublish;
        private readonly Action<string> beforeCleanup;

        public ZipProcessor(ArchivePolicy policy, ISourceRemover remover, IFolderLauncher launcher)
            : this(policy, remover, launcher, null, null, null, null, null)
        {
        }

        internal ZipProcessor(ArchivePolicy policy, ISourceRemover remover, IFolderLauncher launcher, Action<string> beforeExtraction)
            : this(policy, remover, launcher, beforeExtraction, null, null, null, null)
        {
        }

        internal ZipProcessor(ArchivePolicy policy, ISourceRemover remover, IFolderLauncher launcher, Action<string> beforeExtraction, Action<string> afterPublish)
            : this(policy, remover, launcher, beforeExtraction, null, null, afterPublish, null)
        {
        }

        internal ZipProcessor(ArchivePolicy policy, ISourceRemover remover, IFolderLauncher launcher, Action<string> beforeExtraction, Action<string> beforePublish, Action<string> afterPublish)
            : this(policy, remover, launcher, beforeExtraction, beforePublish, null, afterPublish, null)
        {
        }

        internal ZipProcessor(ArchivePolicy policy, ISourceRemover remover, IFolderLauncher launcher, Action<string> beforeExtraction, Action<string> beforePublish, Action<string> afterMoveBeforeVisibility, Action<string> afterPublish)
            : this(policy, remover, launcher, beforeExtraction, beforePublish, afterMoveBeforeVisibility, afterPublish, null)
        {
        }

        internal ZipProcessor(ArchivePolicy policy, ISourceRemover remover, IFolderLauncher launcher, Action<string> beforeExtraction, Action<string> beforePublish, Action<string> afterMoveBeforeVisibility, Action<string> afterPublish, Action<string> beforeCleanup)
        {
            if (policy == null)
            {
                throw new ArgumentNullException("policy");
            }

            if (remover == null)
            {
                throw new ArgumentNullException("remover");
            }

            if (launcher == null)
            {
                throw new ArgumentNullException("launcher");
            }

            this.policy = policy;
            this.remover = remover;
            this.launcher = launcher;
            this.beforeExtraction = beforeExtraction;
            this.beforePublish = beforePublish;
            this.afterMoveBeforeVisibility = afterMoveBeforeVisibility;
            this.afterPublish = afterPublish;
            this.beforeCleanup = beforeCleanup;
        }

        public string Process(string archivePath, string destinationRoot)
        {
            string sourcePath = ValidateInputs(archivePath, destinationRoot);
            string destinationPath = NormalizeRoot(destinationRoot);
            string stem = Path.GetFileNameWithoutExtension(sourcePath);
            if (String.IsNullOrEmpty(stem))
            {
                throw new InvalidDataException("The archive name cannot produce an output folder name.");
            }

            SourceSnapshot originalSource;
            string stagingPath = Path.Combine(destinationPath, ".zipflow-" + Guid.NewGuid().ToString("N") + ".partial");
            string finalPath = null;
            bool published = false;

            try
            {
                using (FileStream source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    originalSource = CaptureSourceSnapshot(source);

                    ArchiveLayout layout = ReadCentralDirectory(source);
                    int finalSuffix;
                    string plannedFinalPath = FindAvailableFinalPath(destinationPath, stem, 1, out finalSuffix);
                    ExtractionPlan plan = BuildPlan(source, layout, stagingPath, plannedFinalPath);

                    Directory.CreateDirectory(stagingPath);
                    RejectReparsePoint(stagingPath);
                    File.SetAttributes(stagingPath, File.GetAttributes(stagingPath) | FileAttributes.Hidden);
                    if (beforeExtraction != null)
                    {
                        beforeExtraction(stagingPath);
                    }

                    Extract(source, plan, stagingPath);

                    Publish(stagingPath, destinationPath, stem, plan, plannedFinalPath, finalSuffix, ref finalPath, ref published);
                    stagingPath = null;
                }

                if (afterPublish != null)
                {
                    afterPublish(finalPath);
                }

                SourceSnapshot currentSource;
                try
                {
                    using (FileStream current = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        currentSource = CaptureSourceSnapshot(current);
                    }
                }
                catch (IOException exception)
                {
                    throw new IOException("The source archive changed during extraction. The completed output was preserved at: " + finalPath, exception);
                }

                if (!originalSource.SameFileAndMetadata(currentSource))
                {
                    throw new IOException("The source archive changed during extraction. The completed output was preserved at: " + finalPath);
                }

                try
                {
                    remover.Remove(sourcePath);
                }
                catch (Exception exception)
                {
                    string sourceState = File.Exists(sourcePath)
                        ? "The source archive remains at: " + sourcePath + "."
                        : "The source archive is no longer present at its original path.";
                    throw new IOException(
                        "The source archive could not be sent to the Recycle Bin. "
                            + sourceState
                            + " The completed output remains at: "
                            + finalPath,
                        exception);
                }

                if (File.Exists(sourcePath))
                {
                    throw new IOException(
                        "The Recycle Bin operation returned without removing the source archive. The source archive remains at: "
                            + sourcePath
                            + ". The completed output remains at: "
                            + finalPath);
                }

                try
                {
                    launcher.Open(finalPath);
                }
                catch (Exception exception)
                {
                    throw new IOException(
                        "The source archive was recycled, but the completed output could not be opened. It remains at: " + finalPath,
                        exception);
                }

                return finalPath;
            }
            catch (Exception primaryFailure)
            {
                if (!published && stagingPath != null)
                {
                    Exception cleanupFailure;
                    if (!TryDeleteDirectory(stagingPath, out cleanupFailure) && Directory.Exists(stagingPath))
                    {
                        string cleanupDetail = cleanupFailure == null ? "unknown cleanup failure" : cleanupFailure.Message;
                        throw new IOException(
                            "Processing failed and the staging folder could not be removed. It remains at: "
                                + stagingPath
                                + ". Cleanup error: "
                                + cleanupDetail,
                            primaryFailure);
                    }
                }

                throw;
            }
        }

        private string ValidateInputs(string archivePath, string destinationRoot)
        {
            if (String.IsNullOrWhiteSpace(archivePath))
            {
                throw new ArgumentException("An archive path is required.", "archivePath");
            }

            string sourcePath = Path.GetFullPath(archivePath);
            if (!String.Equals(Path.GetExtension(sourcePath), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The selected file must have a .zip extension.", "archivePath");
            }

            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("The selected ZIP archive does not exist.", sourcePath);
            }

            if (String.IsNullOrWhiteSpace(destinationRoot))
            {
                throw new ArgumentException("A destination folder is required.", "destinationRoot");
            }

            string destinationPath = Path.GetFullPath(destinationRoot);
            if (!Directory.Exists(destinationPath))
            {
                throw new DirectoryNotFoundException("The destination folder does not exist: " + destinationPath);
            }

            return sourcePath;
        }

        private static string NormalizeRoot(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string root = Path.GetPathRoot(fullPath);
            if (String.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.TrimEnd(Path.AltDirectorySeparatorChar);
            }

            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private ArchiveLayout ReadCentralDirectory(FileStream source)
        {
            long eocdOffset = FindEndOfCentralDirectory(source);
            source.Position = eocdOffset + 4;
            ushort diskNumber = ReadUInt16(source);
            ushort centralDisk = ReadUInt16(source);
            ushort entriesOnDisk = ReadUInt16(source);
            ushort entryCount = ReadUInt16(source);
            uint centralSizeValue = ReadUInt32(source);
            uint centralOffsetValue = ReadUInt32(source);
            ushort commentLength = ReadUInt16(source);

            if (diskNumber != 0 || centralDisk != 0 || entriesOnDisk != entryCount)
            {
                throw new InvalidDataException("Multidisk ZIP archives are not supported.");
            }

            if (entriesOnDisk == UInt16.MaxValue || entryCount == UInt16.MaxValue || centralSizeValue == UInt32.MaxValue || centralOffsetValue == UInt32.MaxValue)
            {
                throw new InvalidDataException("ZIP64 archives are not supported.");
            }

            if (entryCount > policy.MaxEntries)
            {
                throw new InvalidDataException("The archive contains too many entries.");
            }

            if (CheckedAdd(eocdOffset + 22, commentLength) != source.Length)
            {
                throw new InvalidDataException("The end-of-central-directory record is malformed.");
            }

            long centralOffset = centralOffsetValue;
            long centralSize = centralSizeValue;
            long centralEnd = CheckedAdd(centralOffset, centralSize);
            if (centralOffset < 0 || centralEnd != eocdOffset)
            {
                throw new InvalidDataException("The central directory bounds are malformed.");
            }

            source.Position = centralOffset;
            List<ArchiveEntry> entries = new List<ArchiveEntry>(entryCount);
            Encoding utf8 = new UTF8Encoding(false, true);
            Encoding legacy;
            try
            {
                legacy = Encoding.GetEncoding(437, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("The ZIP filename encoding is unavailable.", exception);
            }

            for (int index = 0; index < entryCount; index++)
            {
                EnsureAvailable(source, centralEnd, 46);
                if (ReadUInt32(source) != CentralDirectorySignature)
                {
                    throw new InvalidDataException("A central-directory entry is malformed.");
                }

                ReadUInt16(source);
                ReadUInt16(source);
                ushort flags = ReadUInt16(source);
                ushort method = ReadUInt16(source);
                ReadUInt16(source);
                ReadUInt16(source);
                uint crc = ReadUInt32(source);
                uint compressedSize = ReadUInt32(source);
                uint expandedSize = ReadUInt32(source);
                ushort nameLength = ReadUInt16(source);
                ushort extraLength = ReadUInt16(source);
                ushort entryCommentLength = ReadUInt16(source);
                ushort startDisk = ReadUInt16(source);
                ReadUInt16(source);
                uint externalAttributes = ReadUInt32(source);
                uint localOffset = ReadUInt32(source);

                if ((flags & 0x0041) != 0)
                {
                    throw new InvalidDataException("Encrypted ZIP entries are not supported.");
                }

                if (method != 0 && method != 8)
                {
                    throw new InvalidDataException("The archive uses an unsupported compression method.");
                }

                ValidateGeneralPurposeFlags(flags, method);

                if (compressedSize == UInt32.MaxValue || expandedSize == UInt32.MaxValue || localOffset == UInt32.MaxValue || startDisk == UInt16.MaxValue)
                {
                    throw new InvalidDataException("ZIP64 entries are not supported.");
                }

                if (startDisk != 0)
                {
                    throw new InvalidDataException("Multidisk ZIP entries are not supported.");
                }

                if (nameLength == 0)
                {
                    throw new InvalidDataException("ZIP entries must have names.");
                }

                long variableLength = CheckedAdd(CheckedAdd(nameLength, extraLength), entryCommentLength);
                EnsureAvailable(source, centralEnd, variableLength);
                byte[] nameBytes = ReadBytes(source, nameLength);
                byte[] extraBytes = ReadBytes(source, extraLength);
                ReadBytes(source, entryCommentLength);
                RejectZip64Extra(extraBytes);

                Encoding encoding = (flags & 0x0800) != 0 ? utf8 : legacy;
                string decodedName;
                try
                {
                    decodedName = encoding.GetString(nameBytes);
                }
                catch (DecoderFallbackException exception)
                {
                    throw new InvalidDataException("A ZIP entry name is not valid text.", exception);
                }

                ArchiveEntry entry = new ArchiveEntry();
                entry.Flags = flags;
                entry.Method = method;
                entry.Crc32 = crc;
                entry.CompressedSize = compressedSize;
                entry.ExpandedSize = expandedSize;
                entry.NameBytes = nameBytes;
                entry.DecodedName = decodedName;
                entry.LocalHeaderOffset = localOffset;
                entry.ExternalAttributes = externalAttributes;
                entries.Add(entry);
            }

            if (source.Position != centralEnd)
            {
                throw new InvalidDataException("The central-directory entry count or size is malformed.");
            }

            ArchiveLayout layout = new ArchiveLayout();
            layout.CentralOffset = centralOffset;
            layout.Entries = entries;
            return layout;
        }

        private long FindEndOfCentralDirectory(FileStream source)
        {
            if (source.Length < 22)
            {
                throw new InvalidDataException("The file is too short to be a ZIP archive.");
            }

            int searchLength = (int)Math.Min(source.Length, 22L + UInt16.MaxValue);
            byte[] tail = new byte[searchLength];
            source.Position = source.Length - searchLength;
            ReadExactly(source, tail, 0, tail.Length);

            for (int index = tail.Length - 22; index >= 0; index--)
            {
                if (ReadUInt32(tail, index) != EndOfCentralDirectorySignature)
                {
                    continue;
                }

                ushort commentLength = ReadUInt16(tail, index + 20);
                if (index + 22 + commentLength == tail.Length)
                {
                    return source.Length - searchLength + index;
                }
            }

            throw new InvalidDataException("The ZIP end-of-central-directory record was not found.");
        }

        private ExtractionPlan BuildPlan(FileStream source, ArchiveLayout layout, string stagingPath, string finalPath)
        {
            Dictionary<string, bool> nodes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> entryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<long> localOffsets = new HashSet<long>();
            List<DataRange> ranges = new List<DataRange>();
            List<string> directories = new List<string>();
            long declaredTotal = 0;
            string stagingRoot = NormalizeRoot(stagingPath);
            string stagingPrefix = stagingRoot + Path.DirectorySeparatorChar;

            foreach (ArchiveEntry entry in layout.Entries)
            {
                bool directory;
                string relativePath = ValidateEntryPath(entry.DecodedName, out directory);
                entry.IsDirectory = directory;
                entry.RelativePath = relativePath;

                string[] segments = relativePath.Split(Path.DirectorySeparatorChar);
                if (segments.Length > policy.MaxDepth)
                {
                    throw new InvalidDataException("A ZIP entry exceeds the path-depth limit.");
                }

                string current = String.Empty;
                for (int index = 0; index < segments.Length - 1; index++)
                {
                    current = current.Length == 0 ? segments[index] : Path.Combine(current, segments[index]);
                    bool existingDirectory;
                    if (nodes.TryGetValue(current, out existingDirectory))
                    {
                        if (!existingDirectory)
                        {
                            throw new InvalidDataException("A ZIP entry places a child beneath a file.");
                        }
                    }
                    else
                    {
                        nodes.Add(current, true);
                        directories.Add(current);
                    }
                }

                bool existingType;
                if (entryPaths.Contains(relativePath))
                {
                    throw new InvalidDataException("The ZIP contains duplicate destination paths.");
                }

                if (nodes.TryGetValue(relativePath, out existingType))
                {
                    if (!existingType || !directory)
                    {
                        throw new InvalidDataException("The ZIP contains a file-directory conflict.");
                    }
                }
                else
                {
                    nodes.Add(relativePath, directory);
                    if (directory)
                    {
                        directories.Add(relativePath);
                    }
                }

                entryPaths.Add(relativePath);

                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(Path.Combine(stagingRoot, relativePath));
                }
                catch (Exception exception)
                {
                    if (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
                    {
                        throw new InvalidDataException("A ZIP entry has an invalid destination path.", exception);
                    }

                    throw;
                }

                if (!fullPath.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("A ZIP entry escapes the staging folder.");
                }

                entry.FullPath = fullPath;

                if (directory)
                {
                    if (entry.CompressedSize != 0 || entry.ExpandedSize != 0 || entry.Crc32 != 0)
                    {
                        throw new InvalidDataException("A directory entry contains inconsistent file metadata.");
                    }
                }
                else
                {
                    if (entry.ExpandedSize > policy.MaxEntryBytes)
                    {
                        throw new InvalidDataException("A ZIP entry exceeds the expanded-size limit.");
                    }

                    try
                    {
                        declaredTotal = checked(declaredTotal + entry.ExpandedSize);
                    }
                    catch (OverflowException exception)
                    {
                        throw new InvalidDataException("The archive expanded-size total overflows.", exception);
                    }

                    if (declaredTotal > policy.MaxTotalBytes)
                    {
                        throw new InvalidDataException("The archive exceeds the total expanded-size limit.");
                    }

                    if (CompressionRatioExceeded(entry.ExpandedSize, entry.CompressedSize, policy.MaxCompressionRatio))
                    {
                        throw new InvalidDataException("A ZIP entry exceeds the compression-ratio limit.");
                    }

                    if (entry.Method == 0 && entry.CompressedSize != entry.ExpandedSize)
                    {
                        throw new InvalidDataException("A stored ZIP entry has inconsistent sizes.");
                    }
                }

                if (!localOffsets.Add(entry.LocalHeaderOffset))
                {
                    throw new InvalidDataException("Multiple entries reference the same local header.");
                }

                ReadLocalHeader(source, entry, layout.CentralOffset);
                DataRange range = new DataRange();
                range.Start = entry.LocalHeaderOffset;
                range.End = CheckedAdd(entry.DataOffset, entry.CompressedSize);
                ranges.Add(range);
            }

            ranges.Sort(delegate(DataRange left, DataRange right) { return left.Start.CompareTo(right.Start); });
            for (int index = 1; index < ranges.Count; index++)
            {
                if (ranges[index].Start < ranges[index - 1].End)
                {
                    throw new InvalidDataException("ZIP entry data ranges overlap.");
                }
            }

            directories.Sort(delegate(string left, string right)
            {
                int depthComparison = PathDepth(left).CompareTo(PathDepth(right));
                return depthComparison != 0 ? depthComparison : StringComparer.OrdinalIgnoreCase.Compare(left, right);
            });

            ExtractionPlan plan = new ExtractionPlan();
            plan.Entries = layout.Entries;
            plan.Directories = directories;
            ValidateFinalPaths(plan, finalPath);
            return plan;
        }

        private void ValidateFinalPaths(ExtractionPlan plan, string finalPath)
        {
            string finalRoot = NormalizeRoot(finalPath);
            string finalPrefix = finalRoot + Path.DirectorySeparatorChar;
            if (finalRoot.Length > policy.MaxFullPath)
            {
                throw new InvalidDataException("The output folder exceeds the full-path limit.");
            }

            foreach (ArchiveEntry entry in plan.Entries)
            {
                string resolved;
                try
                {
                    resolved = Path.GetFullPath(Path.Combine(finalRoot, entry.RelativePath));
                }
                catch (Exception exception)
                {
                    if (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
                    {
                        throw new InvalidDataException("A ZIP entry has an invalid final destination path.", exception);
                    }

                    throw;
                }

                if (!resolved.StartsWith(finalPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("A ZIP entry escapes the final output folder.");
                }

                if (resolved.Length > policy.MaxFullPath)
                {
                    throw new InvalidDataException("A ZIP entry exceeds the full-path limit.");
                }
            }
        }

        private void ReadLocalHeader(FileStream source, ArchiveEntry entry, long centralOffset)
        {
            long headerEnd = CheckedAdd(entry.LocalHeaderOffset, 30);
            if (entry.LocalHeaderOffset < 0 || headerEnd > centralOffset)
            {
                throw new InvalidDataException("A local ZIP header is outside the archive data area.");
            }

            source.Position = entry.LocalHeaderOffset;
            if (ReadUInt32(source) != LocalHeaderSignature)
            {
                throw new InvalidDataException("A local ZIP header is malformed.");
            }

            ReadUInt16(source);
            ushort flags = ReadUInt16(source);
            ushort method = ReadUInt16(source);
            ReadUInt16(source);
            ReadUInt16(source);
            uint localCrc = ReadUInt32(source);
            uint localCompressed = ReadUInt32(source);
            uint localExpanded = ReadUInt32(source);
            ushort nameLength = ReadUInt16(source);
            ushort extraLength = ReadUInt16(source);

            ValidateGeneralPurposeFlags(flags, method);
            if (flags != entry.Flags || method != entry.Method || (flags & 0x0041) != 0)
            {
                throw new InvalidDataException("Local and central ZIP metadata disagree.");
            }

            if ((flags & 0x0008) == 0)
            {
                if (localCrc != entry.Crc32 || localCompressed != entry.CompressedSize || localExpanded != entry.ExpandedSize)
                {
                    throw new InvalidDataException("Local and central ZIP sizes or CRC values disagree.");
                }
            }
            else
            {
                if ((localCrc != 0 && localCrc != entry.Crc32)
                    || (localCompressed != 0 && localCompressed != entry.CompressedSize)
                    || (localExpanded != 0 && localExpanded != entry.ExpandedSize))
                {
                    throw new InvalidDataException("Local and central ZIP descriptor metadata disagree.");
                }
            }

            long variableEnd = CheckedAdd(headerEnd, CheckedAdd(nameLength, extraLength));
            if (variableEnd > centralOffset)
            {
                throw new InvalidDataException("A local ZIP header exceeds the archive data area.");
            }

            byte[] localName = ReadBytes(source, nameLength);
            if (!ByteArraysEqual(localName, entry.NameBytes))
            {
                throw new InvalidDataException("Local and central ZIP entry names disagree.");
            }

            byte[] localExtra = ReadBytes(source, extraLength);
            RejectZip64Extra(localExtra);
            entry.DataOffset = variableEnd;
            if (CheckedAdd(entry.DataOffset, entry.CompressedSize) > centralOffset)
            {
                throw new InvalidDataException("Compressed ZIP data exceeds the archive data area.");
            }
        }

        private string ValidateEntryPath(string decodedName, out bool directory)
        {
            if (String.IsNullOrEmpty(decodedName))
            {
                throw new InvalidDataException("ZIP entries must have nonempty names.");
            }

            if (decodedName[0] == '/' || decodedName[0] == '\\' || Path.IsPathRooted(decodedName) || decodedName.IndexOf(':') >= 0)
            {
                throw new InvalidDataException("Rooted, device, drive, and alternate-data-stream paths are not allowed.");
            }

            string normalized = decodedName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            directory = normalized[normalized.Length - 1] == Path.DirectorySeparatorChar;
            if (directory)
            {
                normalized = normalized.Substring(0, normalized.Length - 1);
            }

            if (normalized.Length == 0)
            {
                throw new InvalidDataException("ZIP entry paths cannot be empty.");
            }

            string[] segments = normalized.Split(Path.DirectorySeparatorChar);
            foreach (string segment in segments)
            {
                ValidateSegment(segment);
            }

            return String.Join(Path.DirectorySeparatorChar.ToString(), segments);
        }

        private static void ValidateGeneralPurposeFlags(ushort flags, ushort method)
        {
            const int DataDescriptorAndUtf8 = 0x0808;
            const int DeflateOptions = 0x0006;
            int allowed = DataDescriptorAndUtf8;
            if (method == 8)
            {
                allowed |= DeflateOptions;
            }

            if ((flags & ~allowed) != 0)
            {
                throw new InvalidDataException("A ZIP entry uses unsupported general-purpose flags.");
            }
        }

        private static void ValidateSegment(string segment)
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                throw new InvalidDataException("ZIP entry paths cannot contain empty, dot, or dot-dot segments.");
            }

            if (segment[0] == ' ' || segment[segment.Length - 1] == ' ' || segment[segment.Length - 1] == '.')
            {
                throw new InvalidDataException("ZIP path segments cannot have leading or trailing spaces or periods.");
            }

            const string Invalid = "<>:\"/\\|?*";
            foreach (char value in segment)
            {
                if (value < 32 || Invalid.IndexOf(value) >= 0)
                {
                    throw new InvalidDataException("A ZIP path segment contains invalid Windows filename characters.");
                }
            }

            string basename = segment;
            int period = basename.IndexOf('.');
            if (period >= 0)
            {
                basename = basename.Substring(0, period);
            }

            string upper = basename.ToUpperInvariant();
            if (upper == "CON" || upper == "PRN" || upper == "AUX" || upper == "NUL" || upper == "CLOCK$")
            {
                throw new InvalidDataException("Reserved DOS device names are not allowed.");
            }

            if (upper.Length == 4 && (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal)))
            {
                char suffix = upper[3];
                if ((suffix >= '1' && suffix <= '9') || suffix == '\u00b9' || suffix == '\u00b2' || suffix == '\u00b3')
                {
                    throw new InvalidDataException("Reserved DOS device names are not allowed.");
                }
            }
        }

        private void Extract(FileStream source, ExtractionPlan plan, string stagingPath)
        {
            foreach (string relativeDirectory in plan.Directories)
            {
                string directoryPath = Path.Combine(stagingPath, relativeDirectory);
                Directory.CreateDirectory(directoryPath);
                EnsureDirectoryPathHasNoReparsePoints(stagingPath, directoryPath);
            }

            long actualTotal = 0;
            byte[] buffer = new byte[CopyBufferSize];
            foreach (ArchiveEntry entry in plan.Entries)
            {
                if (entry.IsDirectory)
                {
                    continue;
                }

                string parent = Path.GetDirectoryName(entry.FullPath);
                if (!String.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                    EnsureDirectoryPathHasNoReparsePoints(stagingPath, parent);
                }

                source.Position = entry.DataOffset;
                BoundedReadStream compressed = new BoundedReadStream(source, entry.CompressedSize);
                uint crc = UInt32.MaxValue;
                long actualEntry = 0;

                using (FileStream output = new FileStream(entry.FullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    Stream expanded = compressed;
                    DeflateStream deflate = null;
                    if (entry.Method == 8)
                    {
                        deflate = new DeflateStream(compressed, CompressionMode.Decompress, true);
                        expanded = deflate;
                    }

                    try
                    {
                        while (true)
                        {
                            int read = expanded.Read(buffer, 0, buffer.Length);
                            if (read == 0)
                            {
                                break;
                            }

                            try
                            {
                                actualEntry = checked(actualEntry + read);
                                actualTotal = checked(actualTotal + read);
                            }
                            catch (OverflowException exception)
                            {
                                throw new InvalidDataException("Expanded ZIP byte counts overflow.", exception);
                            }

                            if (actualEntry > entry.ExpandedSize || actualEntry > policy.MaxEntryBytes || actualTotal > policy.MaxTotalBytes)
                            {
                                throw new InvalidDataException("Expanded ZIP data exceeds its declared size or configured quota.");
                            }

                            crc = UpdateCrc32(crc, buffer, read);
                            output.Write(buffer, 0, read);
                        }
                    }
                    finally
                    {
                        if (deflate != null)
                        {
                            deflate.Dispose();
                        }
                    }

                    if (actualEntry != entry.ExpandedSize)
                    {
                        throw new InvalidDataException("An extracted file does not match its declared length.");
                    }

                    if ((crc ^ UInt32.MaxValue) != entry.Crc32)
                    {
                        throw new InvalidDataException("An extracted file failed CRC-32 verification.");
                    }

                    if (compressed.Remaining != 0)
                    {
                        throw new InvalidDataException("A compressed ZIP stream did not consume its declared bytes.");
                    }

                    output.Flush(true);
                }
            }
        }

        private void Publish(string stagingPath, string destinationPath, string stem, ExtractionPlan plan, string candidate, int suffix, ref string finalPath, ref bool published)
        {
            while (true)
            {
                ValidateFinalPaths(plan, candidate);
                if (Directory.Exists(candidate) || File.Exists(candidate))
                {
                    candidate = FindAvailableFinalPath(destinationPath, stem, suffix + 1, out suffix);
                    continue;
                }

                try
                {
                    EnsureTreeHasNoReparsePoints(stagingPath);
                    if (beforePublish != null)
                    {
                        beforePublish(stagingPath);
                    }

                    Directory.Move(stagingPath, candidate);
                }
                catch (IOException)
                {
                    if ((Directory.Exists(candidate) || File.Exists(candidate)) && Directory.Exists(stagingPath))
                    {
                        candidate = FindAvailableFinalPath(destinationPath, stem, suffix + 1, out suffix);
                        continue;
                    }

                    throw;
                }

                finalPath = candidate;
                published = true;
                try
                {
                    if (afterMoveBeforeVisibility != null)
                    {
                        afterMoveBeforeVisibility(candidate);
                    }

                    File.SetAttributes(candidate, File.GetAttributes(candidate) & ~FileAttributes.Hidden);
                }
                catch (Exception exception)
                {
                    throw new IOException("The completed output was published but could not be made visible. It was preserved at: " + candidate, exception);
                }

                return;
            }
        }

        private static void EnsureDirectoryPathHasNoReparsePoints(string stagingPath, string directoryPath)
        {
            string stagingRoot = NormalizeRoot(stagingPath);
            string target = NormalizeRoot(directoryPath);
            string prefix = stagingRoot + Path.DirectorySeparatorChar;
            if (!String.Equals(target, stagingRoot, StringComparison.OrdinalIgnoreCase)
                && !target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A staging directory path escaped its root.");
            }

            RejectReparsePoint(stagingRoot);
            if (String.Equals(target, stagingRoot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string relative = target.Substring(prefix.Length);
            string current = stagingRoot;
            string[] segments = relative.Split(Path.DirectorySeparatorChar);
            foreach (string segment in segments)
            {
                current = Path.Combine(current, segment);
                RejectReparsePoint(current);
            }
        }

        private static void EnsureTreeHasNoReparsePoints(string stagingPath)
        {
            Stack<string> pending = new Stack<string>();
            pending.Push(stagingPath);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                RejectReparsePoint(directory);
                string[] entries = Directory.GetFileSystemEntries(directory);
                foreach (string entry in entries)
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidDataException("A reparse point was found inside the staging folder.");
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                    }
                }
            }
        }

        private static void RejectReparsePoint(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("A reparse point was found inside the staging folder.");
            }
        }

        private static string FindAvailableFinalPath(string destinationPath, string stem, int startingSuffix, out int selectedSuffix)
        {
            int suffix = startingSuffix;
            while (true)
            {
                string name = suffix == 1 ? stem : stem + " (" + suffix + ")";
                string candidate = Path.Combine(destinationPath, name);
                if (!Directory.Exists(candidate) && !File.Exists(candidate))
                {
                    selectedSuffix = suffix;
                    return candidate;
                }

                suffix++;
            }
        }

        private static bool CompressionRatioExceeded(long expanded, long compressed, long maximumRatio)
        {
            if (expanded == 0)
            {
                return false;
            }

            if (compressed == 0)
            {
                return true;
            }

            if (compressed > Int64.MaxValue / maximumRatio)
            {
                return false;
            }

            return expanded > compressed * maximumRatio;
        }

        private static SourceSnapshot CaptureSourceSnapshot(FileStream source)
        {
            ByHandleFileInformation information;
            if (!GetFileInformationByHandle(source.SafeFileHandle, out information))
            {
                throw new IOException("Windows could not read the source archive identity.", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            SourceSnapshot snapshot = new SourceSnapshot();
            snapshot.VolumeSerialNumber = information.VolumeSerialNumber;
            snapshot.FileIndexHigh = information.FileIndexHigh;
            snapshot.FileIndexLow = information.FileIndexLow;
            snapshot.Length = ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
            snapshot.LastWriteHigh = information.LastWriteTime.High;
            snapshot.LastWriteLow = information.LastWriteTime.Low;
            return snapshot;
        }

        private static uint UpdateCrc32(uint crc, byte[] buffer, int count)
        {
            for (int index = 0; index < count; index++)
            {
                crc ^= buffer[index];
                for (int bit = 0; bit < 8; bit++)
                {
                    uint mask = (uint)-(int)(crc & 1U);
                    crc = (crc >> 1) ^ (0xedb88320U & mask);
                }
            }

            return crc;
        }

        private static void RejectZip64Extra(byte[] extra)
        {
            int offset = 0;
            while (offset < extra.Length)
            {
                if (extra.Length - offset < 4)
                {
                    throw new InvalidDataException("A ZIP extra field is malformed.");
                }

                ushort id = ReadUInt16(extra, offset);
                ushort length = ReadUInt16(extra, offset + 2);
                offset += 4;
                if (length > extra.Length - offset)
                {
                    throw new InvalidDataException("A ZIP extra field exceeds its record.");
                }

                if (id == 0x0001)
                {
                    throw new InvalidDataException("ZIP64 entries are not supported.");
                }

                offset += length;
            }
        }

        private static int PathDepth(string path)
        {
            int depth = 1;
            foreach (char value in path)
            {
                if (value == Path.DirectorySeparatorChar)
                {
                    depth++;
                }
            }

            return depth;
        }

        private static long CheckedAdd(long left, long right)
        {
            try
            {
                return checked(left + right);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException("ZIP bounds overflow.", exception);
            }
        }

        private static void EnsureAvailable(Stream source, long limit, long count)
        {
            if (count < 0 || source.Position < 0 || CheckedAdd(source.Position, count) > limit)
            {
                throw new InvalidDataException("A ZIP record exceeds its declared bounds.");
            }
        }

        private static ushort ReadUInt16(Stream source)
        {
            byte[] bytes = ReadBytes(source, 2);
            return ReadUInt16(bytes, 0);
        }

        private static uint ReadUInt32(Stream source)
        {
            byte[] bytes = ReadBytes(source, 4);
            return ReadUInt32(bytes, 0);
        }

        private static ushort ReadUInt16(byte[] bytes, int offset)
        {
            return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            return (uint)(bytes[offset]
                | (bytes[offset + 1] << 8)
                | (bytes[offset + 2] << 16)
                | (bytes[offset + 3] << 24));
        }

        private static byte[] ReadBytes(Stream source, int count)
        {
            byte[] bytes = new byte[count];
            ReadExactly(source, bytes, 0, count);
            return bytes;
        }

        private static void ReadExactly(Stream source, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = source.Read(buffer, offset, count);
                if (read == 0)
                {
                    throw new InvalidDataException("The ZIP archive ended unexpectedly.");
                }

                offset += read;
                count -= read;
            }
        }

        private static bool ByteArraysEqual(byte[] left, byte[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index])
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryDeleteDirectory(string path, out Exception failure)
        {
            failure = null;
            try
            {
                if (beforeCleanup != null)
                {
                    beforeCleanup(path);
                }

                if (Directory.Exists(path))
                {
                    DeleteStagingTreeWithoutFollowingReparsePoints(path);
                }

                return !Directory.Exists(path);
            }
            catch (Exception exception)
            {
                failure = exception;
                return !Directory.Exists(path);
            }
        }

        private static void DeleteStagingTreeWithoutFollowingReparsePoints(string path)
        {
            FileAttributes rootAttributes = File.GetAttributes(path);
            if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(path, false);
                return;
            }

            string[] entries = Directory.GetFileSystemEntries(path);
            foreach (string entry in entries)
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        Directory.Delete(entry, false);
                    }
                    else
                    {
                        File.Delete(entry);
                    }
                }
                else if ((attributes & FileAttributes.Directory) != 0)
                {
                    DeleteStagingTreeWithoutFollowingReparsePoints(entry);
                }
                else
                {
                    File.Delete(entry);
                }
            }

            Directory.Delete(path, false);
        }

        private sealed class ArchiveLayout
        {
            internal long CentralOffset;
            internal List<ArchiveEntry> Entries;
        }

        private sealed class ArchiveEntry
        {
            internal ushort Flags;
            internal ushort Method;
            internal uint Crc32;
            internal long CompressedSize;
            internal long ExpandedSize;
            internal byte[] NameBytes;
            internal string DecodedName;
            internal long LocalHeaderOffset;
            internal uint ExternalAttributes;
            internal bool IsDirectory;
            internal string RelativePath;
            internal string FullPath;
            internal long DataOffset;
        }

        private sealed class ExtractionPlan
        {
            internal List<ArchiveEntry> Entries;
            internal List<string> Directories;
        }

        private sealed class DataRange
        {
            internal long Start;
            internal long End;
        }

        private sealed class SourceSnapshot
        {
            internal uint VolumeSerialNumber;
            internal uint FileIndexHigh;
            internal uint FileIndexLow;
            internal long Length;
            internal uint LastWriteHigh;
            internal uint LastWriteLow;

            internal bool SameFileAndMetadata(SourceSnapshot other)
            {
                return other != null
                    && VolumeSerialNumber == other.VolumeSerialNumber
                    && FileIndexHigh == other.FileIndexHigh
                    && FileIndexLow == other.FileIndexLow
                    && Length == other.Length
                    && LastWriteHigh == other.LastWriteHigh
                    && LastWriteLow == other.LastWriteLow;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileTime
        {
            internal uint Low;
            internal uint High;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            internal uint FileAttributes;
            internal NativeFileTime CreationTime;
            internal NativeFileTime LastAccessTime;
            internal NativeFileTime LastWriteTime;
            internal uint VolumeSerialNumber;
            internal uint FileSizeHigh;
            internal uint FileSizeLow;
            internal uint NumberOfLinks;
            internal uint FileIndexHigh;
            internal uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

        private sealed class BoundedReadStream : Stream
        {
            private readonly Stream source;
            private long remaining;

            internal BoundedReadStream(Stream source, long length)
            {
                this.source = source;
                remaining = length;
            }

            internal long Remaining
            {
                get { return remaining; }
            }

            public override bool CanRead
            {
                get { return true; }
            }

            public override bool CanSeek
            {
                get { return false; }
            }

            public override bool CanWrite
            {
                get { return false; }
            }

            public override long Length
            {
                get { throw new NotSupportedException(); }
            }

            public override long Position
            {
                get { throw new NotSupportedException(); }
                set { throw new NotSupportedException(); }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (remaining == 0)
                {
                    return 0;
                }

                int allowed = (int)Math.Min((long)count, remaining);
                int read = source.Read(buffer, offset, allowed);
                if (read == 0)
                {
                    throw new InvalidDataException("Compressed ZIP data ended unexpectedly.");
                }

                remaining -= read;
                return read;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }
    }
}
