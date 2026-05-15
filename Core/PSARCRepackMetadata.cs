using Gibbed.IO;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace UnPSARC
{
    internal static class PSARCRepackMetadata
    {
        public const string FileName = "__unpsarc_repack.xml";

        private sealed class ArchiveFileMetadata
        {
            public string ArchivePath;
            public string SourcePath;
            public bool IsCompressed;
        }

        public static string GetPath(string directoryPath)
        {
            return Path.Combine(directoryPath, FileName);
        }

        public static void Write(string outputDirectory, string sourceArchivePath, PSARC psarc)
        {
            Directory.CreateDirectory(outputDirectory);

            List<ArchiveFileMetadata> files = BuildFileList(psarc);
            bool hasCompressedFiles = files.Any(file => file.IsCompressed);
            bool compressionSupported = IsCompressionSupported(psarc.CompressionType);
            string compressionType = GetXmlCompressionType(psarc.CompressionType);

            XElement createElement = new XElement("create",
                new XAttribute("archive", Path.GetFileName(sourceArchivePath)),
                new XAttribute("absolute", ToXmlBool(psarc.HasAbsolutePaths)),
                new XAttribute("ignorecase", ToXmlBool(psarc.HasIgnoreCasePaths)),
                new XAttribute("mergedups", "false"),
                new XAttribute("stripall", "false"),
                new XAttribute("blocksize", psarc.BlockSize.ToString()),
                new XAttribute("skipmissingfiles", "false"),
                new XAttribute("format", "psarc"),
                new XAttribute("overwrite", "false"));

            createElement.Add(new XElement("compression",
                new XAttribute("type", compressionType),
                new XAttribute("level", "9"),
                new XAttribute("enabled", ToXmlBool(hasCompressedFiles && compressionSupported))));

            foreach (ArchiveFileMetadata file in files)
            {
                XElement fileElement = new XElement("file",
                    new XAttribute("path", file.SourcePath),
                    new XAttribute("archivepath", file.ArchivePath));

                if (file.IsCompressed == false)
                {
                    fileElement.Add(new XAttribute("compressed", "false"));
                }

                createElement.Add(fileElement);
            }

            XDocument metadata = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("psarc", createElement));

            if (!compressionSupported)
            {
                metadata.Root.AddFirst(new XComment($" Original compression was '{psarc.CompressionType}'. The bundled psarc.exe can only rebuild zlib/lzma archives, so compression is disabled for compatibility. "));
            }

            SaveXml(metadata, GetPath(outputDirectory));
        }

        public static string CreatePackXml(string contentFolderPath, string outputFilename)
        {
            string metadataPath = GetPath(contentFolderPath);
            if (File.Exists(metadataPath) == false)
            {
                return null;
            }

            XDocument metadata = XDocument.Load(metadataPath, LoadOptions.PreserveWhitespace);
            XElement createElement = metadata.Root?.Element("create");
            if (createElement == null)
            {
                throw new InvalidDataException($"Metadata file '{FileName}' does not contain a <create> element.");
            }

            createElement.SetAttributeValue("archive", outputFilename);
            createElement.SetAttributeValue("overwrite", "true");
            AddMissingFiles(contentFolderPath, createElement);

            string tempXmlPath = Path.Combine(Path.GetTempPath(), $"unpsarc-pack-{Guid.NewGuid():N}.xml");
            SaveXml(metadata, tempXmlPath);
            return tempXmlPath;
        }

        private static List<ArchiveFileMetadata> BuildFileList(PSARC psarc)
        {
            List<ArchiveFileMetadata> files = new List<ArchiveFileMetadata>();
            foreach (TEntry entry in psarc.Entries)
            {
                if (Archive.ShouldSkipEntry(entry))
                {
                    continue;
                }

                string archiveFileName = Archive.GetArchiveFileName(psarc, entry);
                string normalizedArchivePath = NormalizeArchivePath(archiveFileName);
                string sourcePath = normalizedArchivePath.Replace('/', Path.DirectorySeparatorChar);

                files.Add(new ArchiveFileMetadata
                {
                    ArchivePath = normalizedArchivePath,
                    SourcePath = sourcePath,
                    IsCompressed = IsFileCompressed(psarc, entry),
                });
            }

            return files;
        }

        private static bool IsFileCompressed(PSARC psarc, TEntry entry)
        {
            long remainingSize = entry.UncompressedSize;
            int zSizeIndex = entry.ZSizeIndex;
            long blockOffset = entry.Offset;

            while (remainingSize > 0)
            {
                long blockSize = Math.Min(psarc.BlockSize, remainingSize);
                int storedSize = psarc.ZSizes[zSizeIndex++].ZSize;
                if (storedSize == 0)
                {
                    storedSize = psarc.BlockSize;
                }

                bool blockLooksCompressed = storedSize < blockSize ||
                                            (storedSize > 0 && IsCompressedBlock(psarc.Reader, blockOffset, psarc.CompressionType));

                if (blockLooksCompressed)
                {
                    return true;
                }

                blockOffset += storedSize;
                remainingSize -= blockSize;
            }

            return false;
        }

        private static string NormalizeArchivePath(string archiveFileName)
        {
            return archiveFileName.TrimStart('/', '\\').Replace('\\', '/');
        }

        private static bool IsCompressedBlock(Stream reader, long offset, string compressionType)
        {
            long position = reader.Position;
            reader.Seek(offset, SeekOrigin.Begin);
            int magic = reader.ReadValueU16();
            reader.Seek(position, SeekOrigin.Begin);

            if (compressionType == "oodl")
            {
                return magic == 0x68C;
            }

            if (compressionType == "zlib")
            {
                return magic == 0xDA78;
            }

            return false;
        }

        private static void AddMissingFiles(string contentFolderPath, XElement createElement)
        {
            HashSet<string> existingArchivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (XElement fileElement in createElement.Elements("file"))
            {
                XAttribute archivePathAttribute = fileElement.Attribute("archivepath");
                if (archivePathAttribute != null && string.IsNullOrWhiteSpace(archivePathAttribute.Value) == false)
                {
                    existingArchivePaths.Add(NormalizeArchivePath(archivePathAttribute.Value));
                    continue;
                }

                XAttribute pathAttribute = fileElement.Attribute("path");
                if (pathAttribute != null && string.IsNullOrWhiteSpace(pathAttribute.Value) == false)
                {
                    existingArchivePaths.Add(NormalizeArchivePath(pathAttribute.Value));
                }
            }

            foreach (string filePath in Directory.GetFiles(contentFolderPath, "*.*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (ShouldSkipPackHelperFile(contentFolderPath, filePath))
                {
                    continue;
                }

                string relativeArchivePath = NormalizeArchivePath(GetRelativePath(contentFolderPath, filePath));
                if (existingArchivePaths.Contains(relativeArchivePath))
                {
                    continue;
                }

                createElement.Add(new XElement("file",
                    new XAttribute("path", relativeArchivePath.Replace('/', Path.DirectorySeparatorChar)),
                    new XAttribute("archivepath", relativeArchivePath)));

                existingArchivePaths.Add(relativeArchivePath);
            }
        }

        private static bool ShouldSkipPackHelperFile(string contentFolderPath, string filePath)
        {
            string fileName = Path.GetFileName(filePath);
            if (fileName == "Filenames.txt" || fileName == FileName)
            {
                return true;
            }

            string fullOutputPath = Path.GetFullPath(filePath);
            string fullMetadataPath = Path.GetFullPath(GetPath(contentFolderPath));
            return string.Equals(fullOutputPath, fullMetadataPath, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetRelativePath(string basePath, string targetPath)
        {
            string normalizedBasePath = EnsureTrailingDirectorySeparator(Path.GetFullPath(basePath));
            string normalizedTargetPath = Path.GetFullPath(targetPath);

            Uri baseUri = new Uri(normalizedBasePath, UriKind.Absolute);
            Uri targetUri = new Uri(normalizedTargetPath, UriKind.Absolute);
            string relativePath = Uri.UnescapeDataString(baseUri.MakeRelativeUri(targetUri).ToString());
            return relativePath.Replace('/', Path.DirectorySeparatorChar);
        }

        private static string EnsureTrailingDirectorySeparator(string path)
        {
            if (path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
                path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                return path;
            }

            return path + Path.DirectorySeparatorChar;
        }

        private static string GetXmlCompressionType(string compressionType)
        {
            if (compressionType == "lzma")
            {
                return "lzma";
            }

            return "zlib";
        }

        private static bool IsCompressionSupported(string compressionType)
        {
            return compressionType == "zlib" || compressionType == "lzma";
        }

        private static string ToXmlBool(bool value)
        {
            return value ? "true" : "false";
        }

        private static void SaveXml(XDocument metadata, string path)
        {
            XmlWriterSettings settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = true,
                OmitXmlDeclaration = true,
                NewLineChars = Environment.NewLine,
                NewLineHandling = NewLineHandling.Replace,
            };

            using (XmlWriter writer = XmlWriter.Create(path, settings))
            {
                metadata.Save(writer);
            }
        }
    }
}
