using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using Microsoft.VisualBasic.FileIO;

namespace ZipFlow
{
    internal sealed class RecycleBinSourceRemover : ISourceRemover
    {
        public void Remove(string path)
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
    }

    internal sealed class ExplorerFolderLauncher : IFolderLauncher
    {
        public void Open(string path)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = path;
            startInfo.UseShellExecute = true;
            Process process = Process.Start(startInfo);
            if (process != null)
            {
                process.Dispose();
            }
        }
    }

    internal interface IZipFlowSetupFlow
    {
        void Run();
    }

    internal static class Program
    {
        internal static readonly string SetupInstructions =
            "One Windows choice remains"
            + Environment.NewLine
            + Environment.NewLine
            + "ZipFlow installed itself for this Windows account."
            + Environment.NewLine
            + Environment.NewLine
            + "Click OK to open Default Apps. Choose ZipFlow for .zip once."
            + Environment.NewLine
            + "Windows requires you to approve this choice."
            + Environment.NewLine
            + Environment.NewLine
            + "After that, double-click ZIP files normally.";

        [STAThread]
        private static void Main(string[] args)
        {
            string archivePathForError = ResolveArchivePathForError(args);
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                Run(
                    args,
                    desktop,
                    new RecycleBinSourceRemover(),
                    new ExplorerFolderLauncher(),
                    new InteractiveZipFlowSetupFlow());
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    FormatError(exception, archivePathForError),
                    "ZipFlow",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        internal static string Run(string[] args, string destinationRoot, ISourceRemover remover, IFolderLauncher launcher)
        {
            string archivePath = ValidateArguments(args);
            ZipProcessor processor = new ZipProcessor(ArchivePolicy.Default, remover, launcher);
            return processor.Process(archivePath, destinationRoot);
        }

        internal static string Run(
            string[] args,
            string destinationRoot,
            ISourceRemover remover,
            IFolderLauncher launcher,
            IZipFlowSetupFlow setupFlow)
        {
            if (args == null || args.Length == 0)
            {
                if (setupFlow == null)
                {
                    throw new ArgumentNullException("setupFlow");
                }

                setupFlow.Run();
                return null;
            }

            return Run(args, destinationRoot, remover, launcher);
        }

        internal static string FormatError(Exception exception, string archivePath)
        {
            if (exception == null)
            {
                throw new ArgumentNullException("exception");
            }

            string original = exception.Message;
            if (String.IsNullOrWhiteSpace(original))
            {
                original = "ZipFlow could not extract the selected archive.";
            }

            if (HasSourceState(original))
            {
                return original;
            }

            string message = ConciseMessage(original);
            if (String.IsNullOrWhiteSpace(archivePath))
            {
                return message;
            }

            string exactPath;
            try
            {
                exactPath = Path.GetFullPath(archivePath);
            }
            catch (Exception pathException)
            {
                if (pathException is ArgumentException || pathException is NotSupportedException || pathException is PathTooLongException)
                {
                    return message;
                }

                throw;
            }

            string sourceState = File.Exists(exactPath)
                ? "Source ZIP remains at: " + exactPath
                : "Source ZIP no longer exists at: " + exactPath;
            return message + Environment.NewLine + Environment.NewLine + sourceState;
        }

        private static bool HasSourceState(string message)
        {
            return message.IndexOf("Source ZIP", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("source archive remains", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("source archive is no longer", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("source archive was recycled", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ValidateArguments(string[] args)
        {
            if (args == null || args.Length != 1 || String.IsNullOrWhiteSpace(args[0]))
            {
                throw new ArgumentException("ZipFlow requires exactly one ZIP file.");
            }

            string archivePath = Path.GetFullPath(args[0]);
            if (!String.Equals(Path.GetExtension(archivePath), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The selected file must have a .zip extension.");
            }

            if (!File.Exists(archivePath))
            {
                throw new FileNotFoundException("The selected ZIP archive does not exist.", archivePath);
            }

            return archivePath;
        }

        private static string ResolveArchivePathForError(string[] args)
        {
            if (args == null || args.Length != 1 || String.IsNullOrWhiteSpace(args[0]))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(args[0]);
            }
            catch (Exception exception)
            {
                if (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
                {
                    return null;
                }

                throw;
            }
        }

        private static string ConciseMessage(string message)
        {
            const int MaximumLength = 700;
            if (message.Length > MaximumLength)
            {
                message = message.Substring(0, MaximumLength - 3) + "...";
            }

            return message;
        }
    }
}
