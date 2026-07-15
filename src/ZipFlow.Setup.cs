using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ZipFlow
{
    internal interface IZipFlowSetupBackend
    {
        void EnsureDirectory(string path);
        void CopyFile(string source, string destination);
        void SetCurrentUserValue(string subKeyPath, string name, object value, RegistryValueKind kind);
        void NotifyAssociationsChanged();
        void OpenDefaultApps();
    }

    internal sealed class ZipFlowRegistrationValue
    {
        internal readonly string SubKeyPath;
        internal readonly string Name;
        internal readonly object Value;
        internal readonly RegistryValueKind Kind;

        internal ZipFlowRegistrationValue(string subKeyPath, string name, object value, RegistryValueKind kind)
        {
            SubKeyPath = subKeyPath;
            Name = name;
            Value = value;
            Kind = kind;
        }
    }

    internal sealed class ZipFlowSetup
    {
        internal const string DefaultAppsUri = "ms-settings:defaultapps?registeredAppUser=ZipFlow";
        internal const string FallbackDefaultAppsUri = "ms-settings:defaultapps";

        private readonly IZipFlowSetupBackend backend;

        internal ZipFlowSetup(IZipFlowSetupBackend backend)
        {
            if (backend == null)
            {
                throw new ArgumentNullException("backend");
            }

            this.backend = backend;
        }

        internal string Install(string currentExecutable, string localAppData)
        {
            if (String.IsNullOrWhiteSpace(currentExecutable))
            {
                throw new ArgumentException("The running ZipFlow executable path is unavailable.", "currentExecutable");
            }
            if (String.IsNullOrWhiteSpace(localAppData))
            {
                throw new InvalidOperationException("LOCALAPPDATA is not available for this Windows account.");
            }

            string source = Path.GetFullPath(currentExecutable);
            string installDirectory = Path.Combine(Path.GetFullPath(localAppData), "ZipFlow");
            string installedExecutable = Path.Combine(installDirectory, "ZipFlow.exe");

            if (!String.Equals(source, installedExecutable, StringComparison.OrdinalIgnoreCase))
            {
                backend.EnsureDirectory(installDirectory);
                backend.CopyFile(source, installedExecutable);
            }

            foreach (ZipFlowRegistrationValue value in GetRegistrationPlan(installedExecutable))
            {
                backend.SetCurrentUserValue(value.SubKeyPath, value.Name, value.Value, value.Kind);
            }

            backend.NotifyAssociationsChanged();
            return installedExecutable;
        }

        internal void OpenDefaultApps()
        {
            backend.OpenDefaultApps();
        }

        internal static IList<ZipFlowRegistrationValue> GetRegistrationPlan(string installedExecutable)
        {
            if (String.IsNullOrWhiteSpace(installedExecutable))
            {
                throw new ArgumentException("The installed ZipFlow executable path is unavailable.", "installedExecutable");
            }

            string command = "\"" + Path.GetFullPath(installedExecutable) + "\" \"%1\"";
            return new List<ZipFlowRegistrationValue>
            {
                new ZipFlowRegistrationValue("Software\\Classes\\ZipFlow.Archive", "", "ZipFlow ZIP Archive", RegistryValueKind.String),
                new ZipFlowRegistrationValue("Software\\Classes\\ZipFlow.Archive\\shell\\open\\command", "", command, RegistryValueKind.String),
                new ZipFlowRegistrationValue("Software\\ZipFlow\\Capabilities", "ApplicationName", "ZipFlow", RegistryValueKind.String),
                new ZipFlowRegistrationValue("Software\\ZipFlow\\Capabilities", "ApplicationDescription", "Safely extracts ZIP archives to the Desktop.", RegistryValueKind.String),
                new ZipFlowRegistrationValue("Software\\ZipFlow\\Capabilities\\FileAssociations", ".zip", "ZipFlow.Archive", RegistryValueKind.String),
                new ZipFlowRegistrationValue("Software\\RegisteredApplications", "ZipFlow", "Software\\ZipFlow\\Capabilities", RegistryValueKind.String),
                new ZipFlowRegistrationValue("Software\\Classes\\.zip\\OpenWithProgids", "ZipFlow.Archive", new byte[0], RegistryValueKind.None),
                new ZipFlowRegistrationValue("Software\\Classes\\Applications\\ZipFlow.exe\\SupportedTypes", ".zip", new byte[0], RegistryValueKind.None),
                new ZipFlowRegistrationValue("Software\\Classes\\Applications\\ZipFlow.exe\\shell\\open\\command", "", command, RegistryValueKind.String)
            }.AsReadOnly();
        }
    }

    internal sealed class WindowsZipFlowSetupBackend : IZipFlowSetupBackend
    {
        private const uint AssociationChanged = 0x08000000;

        public void EnsureDirectory(string path)
        {
            Directory.CreateDirectory(path);
        }

        public void CopyFile(string source, string destination)
        {
            File.Copy(source, destination, true);
        }

        public void SetCurrentUserValue(string subKeyPath, string name, object value, RegistryValueKind kind)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(subKeyPath))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("Unable to create or open HKCU registry key: " + subKeyPath);
                }

                key.SetValue(name, value, kind);
            }
        }

        public void NotifyAssociationsChanged()
        {
            SHChangeNotify(AssociationChanged, 0, IntPtr.Zero, IntPtr.Zero);
        }

        public void OpenDefaultApps()
        {
            try
            {
                StartUri(ZipFlowSetup.DefaultAppsUri);
            }
            catch (Win32Exception)
            {
                StartUri(ZipFlowSetup.FallbackDefaultAppsUri);
            }
        }

        private static void StartUri(string uri)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = uri;
            startInfo.UseShellExecute = true;
            using (Process process = Process.Start(startInfo))
            {
            }
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
    }

    internal sealed class InteractiveZipFlowSetupFlow : IZipFlowSetupFlow
    {
        public void Run()
        {
            ZipFlowSetup setup = new ZipFlowSetup(new WindowsZipFlowSetupBackend());
            setup.Install(
                Application.ExecutablePath,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

            MessageBox.Show(
                Program.SetupInstructions,
                "ZipFlow setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            setup.OpenDefaultApps();
        }
    }
}
