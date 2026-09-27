using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ZipPla
{
    /// <summary>
    /// Optional single-instance mode. The setting is stored per-user and the
    /// second launch forwards its command line to the first instance.
    ///
    /// d1 changes:
    ///   - The named pipe is machine-wide by nature, but the mutex only used to be
    ///     session-scoped ("Local\"). Both are now scoped by session id + user SID so a
    ///     second Windows session (RDP / fast user switching) can never receive another
    ///     user's paths.
    ///   - The pipe server now carries an ACL that grants access to the current user (and
    ///     LocalSystem) only, instead of relying on the process default DACL.
    ///   - The payload is length-limited and read with a timeout; a client that connects and
    ///     never writes can no longer wedge the server loop.
    ///   - Arguments are converted to absolute paths before forwarding, because the receiving
    ///     process resolves relative paths against its own working directory.
    ///   - Stop() no longer calls Mutex.ReleaseMutex() from an arbitrary thread (mutex
    ///     ownership is thread-affine) and waits briefly for the server thread to finish.
    /// </summary>
    public static class SingleInstanceManager
    {
        private const string RegistryPath = @"Software\ZipPlaForkCustom";
        private const string RegistryValue = "SingleWindow";

        // 실제 이름에 ScopeSuffix 가 붙는다.
        private const string MutexNameBase = @"Local\ZipPlaForkCustom.SingleWindow";
        private const string PipeNameBase = "ZipPlaForkCustom.SingleWindow";

        private const int MaxPayloadBytes = 32 * 1024;
        private const int ConnectTimeoutMs = 4000;
        private const int ClientReadTimeoutMs = 3000;

        private static readonly string ScopeSuffix = CreateScopeSuffix();

        private static string MutexName { get { return MutexNameBase + "." + ScopeSuffix; } }
        private static string PipeName { get { return PipeNameBase + "." + ScopeSuffix; } }

        private static Mutex mutex;
        private static Thread serverThread;
        private static volatile bool stopping;

        public static bool Enabled
        {
            get
            {
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(RegistryPath))
                    {
                        return key != null && Convert.ToBoolean(key.GetValue(RegistryValue, false));
                    }
                }
                catch (Exception ex)
                {
                    Program.LogException(ex, "SingleInstanceManager.Enabled (get)");
                    return false;
                }
            }
            set
            {
                try
                {
                    using (var key = Registry.CurrentUser.CreateSubKey(RegistryPath))
                    {
                        if (key == null)
                        {
                            throw new IOException(@"HKCU\" + RegistryPath + " could not be created.");
                        }
                        key.SetValue(RegistryValue, value ? 1 : 0, RegistryValueKind.DWord);
                    }
                }
                catch (Exception ex)
                {
                    Program.LogException(ex, "SingleInstanceManager.Enabled (set)");
                    MessageBox.Show(ex.Message, "ZipPla", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        /// <summary>
        /// 이 프로세스가 단일 인스턴스의 수신자(primary)가 되면 true.
        /// 이미 다른 프로세스가 수신자이면 false.
        /// </summary>
        public static bool TryBecomePrimaryAndStartServer()
        {
            if (!Enabled) return true;
            if (mutex != null) return true; // 이미 이 프로세스가 수신자

            bool createdNew;
            try
            {
                mutex = new Mutex(true, MutexName, out createdNew);
            }
            catch (Exception ex)
            {
                Program.LogException(ex, "SingleInstanceManager.TryBecomePrimaryAndStartServer");
                return true; // 뮤텍스를 만들 수 없으면 단일 창 기능만 포기하고 정상 기동한다
            }

            if (!createdNew)
            {
                mutex.Dispose();
                mutex = null;
                return false;
            }

            stopping = false;
            serverThread = new Thread(ServerLoop) { IsBackground = true, Name = "ZipPla single instance pipe server" };
            serverThread.Start();
            return true;
        }

        /// <summary>
        /// 다른 프로세스의 명령줄을 수신자에게 전달한다.
        /// 인수는 절대 경로로 정규화한 뒤 NUL 로 구분해 보낸다(경로에 개행이 올 수 있으므로).
        /// </summary>
        public static bool SendToPrimary(string[] args)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0) Thread.Sleep(200 * attempt);

                try
                {
                    var bytes = Encoding.UTF8.GetBytes(JoinPayload(args));
                    if (bytes.Length > MaxPayloadBytes)
                    {
                        Program.LogMessage("Single instance command line is too long (" + bytes.Length + " bytes). Starting a new window.");
                        return false;
                    }

                    using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                    {
                        pipe.Connect(ConnectTimeoutMs);
                        pipe.Write(bytes, 0, bytes.Length);
                        pipe.Flush();
                        pipe.WaitForPipeDrain();
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    Program.LogException(ex, "SingleInstanceManager.SendToPrimary (attempt " + (attempt + 1) + ")");
                }
            }
            return false;
        }

        public static void Stop()
        {
            stopping = true;

            // Mutex 는 소유 스레드에서만 ReleaseMutex 할 수 있는데 이 메서드는 UI 스레드에서
            // 호출될 수 있다. 소유 핸들을 닫기만 하면 모든 핸들이 닫히는 순간 커널 오브젝트가
            // 사라져 이름이 해제되므로, 다음 실행은 정상적으로 createdNew == true 를 받는다.
            var m = Interlocked.Exchange(ref mutex, null);
            try { m?.Dispose(); } catch (Exception ex) { Program.LogException(ex, "SingleInstanceManager.Stop (mutex)"); }

            var t = Interlocked.Exchange(ref serverThread, null);
            try { t?.Join(500); } catch (Exception ex) { Program.LogException(ex, "SingleInstanceManager.Stop (thread)"); }
        }

        internal static string JoinPayload(string[] args)
        {
            var safe = (args ?? new string[0]).Select(NormalizeArgument);
            return string.Join("\0", safe.ToArray());
        }

        internal static string[] SplitPayload(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return new string[0];
            var parts = payload.Split(new[] { '\0' }, StringSplitOptions.None);
            // 구버전 클라이언트는 개행으로 구분해 보냈다.
            if (parts.Length == 1 && parts[0].IndexOf('\n') >= 0)
            {
                parts = parts[0].Split(new[] { '\n' }, StringSplitOptions.None);
            }
            return parts;
        }

        private static string NormalizeArgument(string argument)
        {
            var value = (argument ?? "").Replace("\0", "");
            if (value.Length == 0 || value[0] == '-') return value;

            // 수신자 쪽은 자기 작업 디렉터리 기준으로 상대 경로를 해석하므로 여기서 절대 경로로 바꾼다.
            // 경로에 개행 등 잘못된 문자가 있으면 Path.IsPathRooted/GetFullPath 가
            // ArgumentException 을 던진다. 자체 검사기에서 실제로 재현되었으므로 전체를 감싼다.
            try
            {
                if (Path.IsPathRooted(value)) return value;
                return Path.GetFullPath(value);
            }
            catch (Exception ex)
            {
                Program.LogException(ex, "SingleInstanceManager.NormalizeArgument");
                return value;
            }
        }

        private static void ServerLoop()
        {
            while (!stopping)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = CreateServerStream();

                    var wait = server.BeginWaitForConnection(null, null);
                    while (!wait.AsyncWaitHandle.WaitOne(250))
                    {
                        if (stopping) return;
                    }
                    server.EndWaitForConnection(wait);

                    var payload = ReadPayload(server);
                    if (!string.IsNullOrEmpty(payload))
                    {
                        var commands = SplitPayload(payload);
                        if (commands.Length > 0)
                        {
                            Program.ReceiveSingleInstanceCommand(commands);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!stopping)
                    {
                        Program.LogException(ex, "SingleInstanceManager.ServerLoop");
                        Thread.Sleep(100);
                    }
                }
                finally
                {
                    try { server?.Dispose(); } catch (Exception ex) { Program.LogException(ex, "SingleInstanceManager.ServerLoop (dispose)"); }
                }
            }
        }

        private static NamedPipeServerStream CreateServerStream()
        {
            var security = TryCreatePipeSecurity();
            if (security != null)
            {
                return new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, MaxPayloadBytes, MaxPayloadBytes, security);
            }

            return new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, MaxPayloadBytes, MaxPayloadBytes);
        }

        /// <summary>현재 사용자와 LocalSystem 에만 접근을 허용하는 ACL. 실패하면 null(기본 DACL 사용).</summary>
        private static PipeSecurity TryCreatePipeSecurity()
        {
            try
            {
                var security = new PipeSecurity();
                var currentUser = WindowsIdentity.GetCurrent()?.User;
                if (currentUser != null)
                {
                    security.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));
                }
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    PipeAccessRights.FullControl, AccessControlType.Allow));
                return security;
            }
            catch (Exception ex)
            {
                Program.LogException(ex, "SingleInstanceManager.TryCreatePipeSecurity");
                return null;
            }
        }

        /// <summary>
        /// 최대 MaxPayloadBytes 까지만 읽고, 클라이언트가 응답하지 않으면 포기한다.
        /// StreamReader.ReadToEnd() 는 악의적/정지된 클라이언트에서 서버 루프를 영구히 막을 수 있다.
        /// </summary>
        private static string ReadPayload(Stream stream)
        {
            var buffer = new byte[MaxPayloadBytes];
            var total = 0;

            while (total < buffer.Length)
            {
                int read;
                try
                {
                    var task = stream.ReadAsync(buffer, total, buffer.Length - total);
                    if (!task.Wait(ClientReadTimeoutMs))
                    {
                        task.ContinueWith(t => { var ignored = t.Exception; }); // 미관측 예외 방지
                        Program.LogMessage("Single instance client did not send a payload in time; dropping the connection.");
                        break;
                    }
                    read = task.Result;
                }
                catch (Exception ex)
                {
                    Program.LogException(ex, "SingleInstanceManager.ReadPayload");
                    break;
                }

                if (read <= 0) break;
                total += read;
            }

            return total == 0 ? null : Encoding.UTF8.GetString(buffer, 0, total);
        }

        /// <summary>
        /// 파이프 이름은 머신 전역 네임스페이스를 쓰므로 세션과 사용자를 붙여야 한다.
        /// 그렇지 않으면 RDP/빠른 사용자 전환 환경에서 다른 세션의 창으로 경로가 전달될 수 있다.
        /// </summary>
        private static string CreateScopeSuffix()
        {
            try
            {
                var sessionId = Process.GetCurrentProcess().SessionId;
                var sid = WindowsIdentity.GetCurrent()?.User?.Value ?? "unknown-user";
                return "s" + sessionId + "-" + Fnv1a(sid + "|" + sessionId);
            }
            catch (Exception ex)
            {
                Program.LogException(ex, "SingleInstanceManager.CreateScopeSuffix");
                try { return "s" + Process.GetCurrentProcess().SessionId; }
                catch { return "s0"; }
            }
        }

        private static string Fnv1a(string text)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (var c in text)
                {
                    hash ^= c;
                    hash *= 16777619;
                }
                return hash.ToString("x8");
            }
        }
    }
}
