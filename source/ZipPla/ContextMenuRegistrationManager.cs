using Microsoft.Win32;
using System;
using System.IO;
using System.Windows.Forms;

namespace ZipPla
{
    /// <summary>
    /// Registers ZipPla in the Windows Explorer context menu without requiring administrator rights.
    /// The registration is stored under HKCU\Software\Classes.
    ///
    /// d1 changes:
    ///   - IsRegistered() now also verifies that the stored command still points at this
    ///     executable, so moving/renaming ZipPla is detected as "not registered" and Toggle()
    ///     repairs the stale entry instead of leaving a dead menu item behind.
    ///   - Registry writes are null-checked and failures are surfaced through the log instead of
    ///     throwing a NullReferenceException.
    ///   - Unregister() no longer only catches ArgumentException.
    ///   - The archive extension list is a single source of truth, shared with Program.cs.
    ///   - The menu/verb text comes from Message so it can be localized.
    /// </summary>
    public static class ContextMenuRegistrationManager
    {
        private const string BaseKey = @"Software\Classes";
        private const string VerbName = "ZipPla";

        /// <summary>
        /// 컨텍스트 메뉴에 등록할 아카이브 확장자.
        /// Program.cs 는 "아카이브는 카탈로그로 연다"를 판정할 때 이 목록을 그대로 사용한다.
        /// 확장자를 늘리려면 여기만 고치면 된다.
        /// </summary>
        public static readonly string[] ArchiveExtensions = { ".zip", ".rar" };

        public static bool IsArchiveExtension(string extension)
        {
            if (string.IsNullOrEmpty(extension)) return false;
            foreach (var candidate in ArchiveExtensions)
            {
                if (string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static bool IsRegistered
        {
            get
            {
                try
                {
                    var command = CommandFor(ApplicationExecutablePath());

                    if (!IsCommandKeyCurrent(@"Directory\shell\" + VerbName, command))
                        return false;

                    foreach (var extension in ArchiveExtensions)
                    {
                        if (!IsCommandKeyCurrent(@"SystemFileAssociations\" + extension + @"\shell\" + VerbName, command))
                            return false;
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    Program.LogException(ex, "ContextMenuRegistrationManager.IsRegistered");
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
            var command = CommandFor(executablePath);
            var verbText = Message.ContextMenuOpenWithZipPla ?? "Open with ZipPla";

            // 폴더 컨텍스트 메뉴.
            WriteVerbKey(@"Directory\shell\" + VerbName, verbText, executablePath, command);

            // 압축 파일 컨텍스트 메뉴.
            foreach (var extension in ArchiveExtensions)
            {
                WriteVerbKey(@"SystemFileAssociations\" + extension + @"\shell\" + VerbName, verbText, executablePath, command);
            }

            // 등록 결과를 확인한다. 권한 문제 등으로 조용히 실패하는 것을 막는다.
            if (!IsRegistered)
            {
                throw new IOException(Message.ContextMenuRegistrationFailed ?? "Could not change the Windows context menu registration.");
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

        private static void WriteVerbKey(string verbPath, string verbText, string executablePath, string command)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(BaseKey + @"\" + verbPath))
            {
                if (key == null) throw new IOException(@"HKCU\" + BaseKey + @"\" + verbPath + " could not be created.");
                key.SetValue("", verbText);
                key.SetValue("Icon", executablePath);
            }

            using (var key = Registry.CurrentUser.CreateSubKey(BaseKey + @"\" + verbPath + @"\command"))
            {
                if (key == null) throw new IOException(@"HKCU\" + BaseKey + @"\" + verbPath + @"\command could not be created.");
                key.SetValue("", command);
            }
        }

        /// <summary>등록된 command 값이 현재 실행 파일을 가리키는지 확인한다(이동/이름 변경 감지).</summary>
        private static bool IsCommandKeyCurrent(string relativePath, string expectedCommand)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(BaseKey + @"\" + relativePath + @"\command"))
                {
                    var value = key?.GetValue("") as string;
                    return value != null && string.Equals(value, expectedCommand, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                Program.LogException(ex, "ContextMenuRegistrationManager.IsCommandKeyCurrent");
                return false;
            }
        }

        private static void DeleteTree(string relativePath)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(BaseKey + @"\" + relativePath, false);
            }
            catch (ArgumentException)
            {
                // 키가 존재하지 않음.
            }
            catch (Exception ex)
            {
                Program.LogException(ex, "ContextMenuRegistrationManager.DeleteTree");
            }
        }

        private static string ApplicationExecutablePath()
        {
            return Path.GetFullPath(Application.ExecutablePath);
        }

        /// <summary>Explorer 가 실행할 명령줄. 실행 파일 경로는 항상 인용하고, 선택 항목은 하나만 넘긴다.</summary>
        private static string CommandFor(string executablePath)
        {
            return "\"" + executablePath + "\" \"%1\"";
        }
    }
}
