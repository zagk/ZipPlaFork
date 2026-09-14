using Microsoft.Win32;
using System;
using System.IO;
using System.Windows.Forms;

namespace ZipPla
{
    /// <summary>
    /// Registers ZipPla in the Windows Explorer context menu without requiring administrator rights.
    /// The registration is stored under HKCU\Software\Classes.
    /// </summary>
    public static class ContextMenuRegistrationManager
    {
        private const string BaseKey = @"Software\Classes";
        private const string VerbName = "ZipPla";

        private static readonly string[] ArchiveExtensions = { ".zip", ".rar" };

        public static bool IsRegistered
        {
            get
            {
                try
                {
                    if (!CommandKeyExists(@"Directory\shell\" + VerbName))
                        return false;

                    foreach (var extension in ArchiveExtensions)
                    {
                        if (!CommandKeyExists(@"SystemFileAssociations\" + extension + @"\shell\" + VerbName))
                            return false;
                    }

                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static bool Toggle()
        {
            if (IsRegistered)
            {
                Unregister();
                return false;
            }

            Register();
            return true;
        }

        public static void Register()
        {
            var executablePath = ApplicationExecutablePath();
            var command = Quote(executablePath) + " \"%1\"";

            // Folder context menu.
            using (var key = Registry.CurrentUser.CreateSubKey(
                BaseKey + @"\Directory\shell\" + VerbName))
            {
                key.SetValue("", "Open with ZipPla");
                key.SetValue("Icon", executablePath);
            }

            using (var key = Registry.CurrentUser.CreateSubKey(
                BaseKey + @"\Directory\shell\" + VerbName + @"\command"))
            {
                key.SetValue("", command);
            }

            // ZIP/RAR file context menus.
            foreach (var extension in ArchiveExtensions)
            {
                using (var key = Registry.CurrentUser.CreateSubKey(
                    BaseKey + @"\SystemFileAssociations\" + extension + @"\shell\" + VerbName))
                {
                    key.SetValue("", "Open with ZipPla");
                    key.SetValue("Icon", executablePath);
                }

                using (var key = Registry.CurrentUser.CreateSubKey(
                    BaseKey + @"\SystemFileAssociations\" + extension + @"\shell\" + VerbName + @"\command"))
                {
                    key.SetValue("", command);
                }
            }
        }

        public static void Unregister()
        {
            DeleteTree(@"Directory\shell\" + VerbName);

            foreach (var extension in ArchiveExtensions)
            {
                DeleteTree(@"SystemFileAssociations\" + extension + @"\shell\" + VerbName);
            }
        }

        private static bool CommandKeyExists(string relativePath)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(BaseKey + "\\" + relativePath + @"\command"))
            {
                return key != null && key.GetValue("") != null;
            }
        }

        private static void DeleteTree(string relativePath)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(BaseKey + "\\" + relativePath, false);
            }
            catch (ArgumentException)
            {
                // The key did not exist.
            }
        }

        private static string ApplicationExecutablePath()
        {
            return Path.GetFullPath(Application.ExecutablePath);
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
