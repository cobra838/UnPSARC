using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnPSARC.Helpers;

namespace UnPSARC
{
    internal class Program
    {
        private static string archiveExtension = ".psarc";
        public static bool oodleExist = false;

        private static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("Unpack: Unpsarc.exe <psarc path> [destination folder]");
            Console.WriteLine(" Pack : Unpsarc.exe <content folder> [archive filename to create]");
            Console.WriteLine("\nPress any key to exit");
            Console.ReadKey();
        }

        private static bool CheckForOodle()
        {
            string currentApplicationPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string currentApplicationDirectory = Path.GetDirectoryName(currentApplicationPath);
            string oodleLocation = Path.Combine(currentApplicationDirectory, "oo2core_9_win64.dll");

            return File.Exists(oodleLocation);
        }

        static void Main(string[] args)
        {
            Console.WriteLine("UnPSARC (Archive Tool For PlayStation Archive files 'PSARC')");
            Console.WriteLine("By NoobInCoding");
            Console.WriteLine("https://github.com/rm-NoobInCoding/UnPSARC");
            Console.WriteLine("");

            if (args.Length < 1)
            {
                PrintUsage();
                return;
            }

            oodleExist = CheckForOodle();

            bool isFile = File.Exists(args[0]);
            bool isDirectory = Directory.Exists(args[0]);
            string outputName = null;

            if (args.Length > 1)
            {
                if ((CommendHelper.IsFullPath(args[1]) && CommendHelper.IsValidPath(args[1])) || !CommendHelper.IsFullPath(args[1]))
                    outputName = args[1];
                else
                {
                    Console.WriteLine("Output directory is not a valid path! Make sure your path is in quotation marks.");
                    PrintUsage();
                    return;
                }
            }
            if (isFile && !isDirectory)
            {
                if (Path.GetExtension(args[0]) == archiveExtension)
                {
                    if (outputName == null)
                    {
                        string TempDir = Path.GetDirectoryName(args[0]);
                        if (TempDir == "") TempDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                        string customOutputDirectory = Path.GetFileNameWithoutExtension(args[0]) + "_Unpacked";
                        // string customOutputDirectory = Path.GetFileNameWithoutExtension(args[0]); // Extract to same folder instead
                        if (Directory.Exists(customOutputDirectory) == false)
                            Directory.CreateDirectory(customOutputDirectory);
                        UnpackArchiveFile(args[0], Path.Combine(TempDir, customOutputDirectory));
                    }
                    else
                    {
                        UnpackArchiveFile(args[0], outputName);
                    }
                }
            }
            else if (isDirectory && !isFile)
            {
                if (outputName == null)
                {
                    Console.WriteLine($"Packing {args[0]} to {Path.GetFileNameWithoutExtension(args[0]) + ".psarc"}");
                    PackArchiveFile(args[0], "../" + Path.GetFileNameWithoutExtension(args[0]) + ".psarc");
                }
                else
                {
                    if (!CommendHelper.IsFullPath(outputName))
                    {
                        if (outputName.StartsWith(Path.DirectorySeparatorChar.ToString())) outputName = outputName.Remove(0, 1);
                        outputName = Path.Combine(Environment.CurrentDirectory, outputName);
                    }
                    Console.WriteLine($"Packing {args[0]} to {outputName}");
                    PackArchiveFile(args[0], outputName);

                }

            }
            else
            {
                Console.WriteLine("Input path argument is not a valid file/directory, or does not exist! (Make sure your path is in quotation marks)");
                PrintUsage();
            }
        }

        private static void PackArchiveFile(string contentFolderPath, string outputFilename)
        {
            string fileListPath = Path.Combine(contentFolderPath, "Filenames.txt");
            string packerPath = Path.Combine(contentFolderPath, "r.exe");

            try
            {
                string packXmlPath = PSARCRepackMetadata.CreatePackXml(contentFolderPath, outputFilename);
                if (packXmlPath != null)
                {
                    try
                    {
                        File.WriteAllBytes(packerPath, Packer.psarc);
                        Console.WriteLine($"Packing with metadata {PSARCRepackMetadata.FileName}");
                        RunPacker(contentFolderPath, packerPath, $"--xml=\"{packXmlPath}\"");
                        return;
                    }
                    finally
                    {
                        if (File.Exists(packXmlPath))
                        {
                            File.Delete(packXmlPath);
                        }
                    }
                }

                File.WriteAllText(fileListPath, MakeFileNameTable(contentFolderPath));
                File.WriteAllBytes(packerPath, Packer.psarc);
                RunPacker(contentFolderPath, packerPath, $"create --skip-missing-files --inputfile=filenames.txt --output=\"{outputFilename}\" -y");
            }
            finally
            {
                if (File.Exists(packerPath))
                {
                    File.Delete(packerPath);
                }

                if (File.Exists(fileListPath))
                {
                    File.Delete(fileListPath);
                }
            }
        }

        private static string MakeFileNameTable(string contentFolderPath)
        {
            List<string> files = new List<string>();
            foreach (string fname in Directory.GetFiles(contentFolderPath, "*.*", SearchOption.AllDirectories))
            {
                if (ShouldSkipPackHelperFile(contentFolderPath, fname))
                    continue;
                string _ = fname.Replace(contentFolderPath + Path.DirectorySeparatorChar.ToString(), "").Replace(Path.DirectorySeparatorChar.ToString(), "/");
                files.Add(_);
            }
            return string.Join("\n", files);
        }

        private static bool ShouldSkipPackHelperFile(string contentFolderPath, string filePath)
        {
            string fileName = Path.GetFileName(filePath);
            if (fileName == "Filenames.txt" || fileName == PSARCRepackMetadata.FileName)
            {
                return true;
            }

            string fullOutputPath = Path.GetFullPath(filePath);
            string fullMetadataPath = Path.GetFullPath(PSARCRepackMetadata.GetPath(contentFolderPath));
            return string.Equals(fullOutputPath, fullMetadataPath, StringComparison.OrdinalIgnoreCase);
        }

        private static void RunPacker(string workingDirectory, string executablePath, string arguments)
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using (var process = new Process { StartInfo = psi })
            {
                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        Console.WriteLine(e.Data);
                    }
                };
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        Console.Error.WriteLine(e.Data);
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"psarc.exe exited with code {process.ExitCode}.");
                }
            }
        }

        private static void UnpackArchiveFile(string inputPath, string outputDirectory)
        {
            Console.WriteLine("Unpacking {0}...", Path.GetFileName(inputPath));

            Stream R = File.OpenRead(inputPath);
            Archive.Unpack(R, outputDirectory);
            R.Close();
        }
    }
}
