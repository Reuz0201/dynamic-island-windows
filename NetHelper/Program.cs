using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using DynamicIsland.Shared;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace DynamicIsland.NetHelper
{
    /// <summary>
    /// Помощник трафика. Работает с правами админа (их требует ETW), читает системные
    /// события сети по процессам и раз в секунду отправляет острову сводку через pipe:
    /// [{"n":"telegram","f":"Telegram Desktop","u":12345,"d":678}, ...]
    /// Закрывается сам, когда остров закрыт.
    /// </summary>
    internal static class Program
    {
        public const string PipeName = "DynamicIsland.Net";

        private static readonly object Lock = new();
        private static Dictionary<int, (long Up, long Down)> _bytes = new();
        private static readonly Dictionary<int, (string Name, string Friendly, DateTime At)> Procs = new();
        private static readonly int OwnPid = Environment.ProcessId;

        private static TraceEventSession? _session;
        private static volatile bool _everConnected;

        [STAThread]
        private static int Main()
        {
            Log.Init("nethelper");

            using var single = new Mutex(true, "DynamicIsland.NetHelper.Single", out bool isNew);
            if (!isNew)
            {
                Log.Info("Помощник уже запущен");
                return 0;
            }

            if (!IsAdmin())
            {
                Log.Warn("Помощник запущен без прав администратора — ETW недоступен");
                return 2;
            }

            var etw = new Thread(RunSession) { IsBackground = true, Name = "ETW" };
            etw.Start();

            var pipe = new Thread(ServePipe) { IsBackground = true, Name = "Pipe" };
            pipe.Start();

            // Живём, пока жив остров (с запасом на старт)
            var started = DateTime.UtcNow;
            while (true)
            {
                Thread.Sleep(1500);
                // до первого подключения даём острову время запуститься
                if (!_everConnected && DateTime.UtcNow - started < TimeSpan.FromSeconds(30)) continue;
                if (Process.GetProcessesByName("DynamicIsland").Length == 0)
                {
                    Log.Info("Остров закрыт — помощник выходит");
                    break;
                }
            }

            try { _session?.Dispose(); } catch { }
            return 0;
        }

        private static bool IsAdmin()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        // ------------------------------------------------------------ ETW

        private static void RunSession()
        {
            try
            {
                _session = new TraceEventSession("DynamicIsland-Network") { StopOnDispose = true };
                _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

                var k = _session.Source.Kernel;
                k.TcpIpSend += d => Add(d.ProcessID, d.size, true);
                k.TcpIpSendIPV6 += d => Add(d.ProcessID, d.size, true);
                k.UdpIpSend += d => Add(d.ProcessID, d.size, true);
                k.UdpIpSendIPV6 += d => Add(d.ProcessID, d.size, true);
                k.TcpIpRecv += d => Add(d.ProcessID, d.size, false);
                k.TcpIpRecvIPV6 += d => Add(d.ProcessID, d.size, false);
                k.UdpIpRecv += d => Add(d.ProcessID, d.size, false);
                k.UdpIpRecvIPV6 += d => Add(d.ProcessID, d.size, false);

                Log.Info("ETW-сессия запущена");
                _session.Source.Process();
            }
            catch (Exception ex)
            {
                Log.Error("ETW не запустился", ex);
            }
        }

        private static void Add(int pid, int size, bool upload)
        {
            if (pid <= 4 || pid == OwnPid || size <= 0) return;
            lock (Lock)
            {
                _bytes.TryGetValue(pid, out var cur);
                _bytes[pid] = upload ? (cur.Up + size, cur.Down) : (cur.Up, cur.Down + size);
            }
        }

        // ------------------------------------------------------------ pipe

        private static void ServePipe()
        {
            while (true)
            {
                try
                {
                    using var server = CreatePipe();
                    server.WaitForConnection();
                    Log.Info("Остров подключился к помощнику");
                    _everConnected = true;

                    // сбрасываем накопленное, чтобы первая секунда не была «всплеском»
                    lock (Lock) _bytes = new Dictionary<int, (long Up, long Down)>();

                    var writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true };
                    while (server.IsConnected)
                    {
                        Thread.Sleep(1000);
                        writer.WriteLine(Snapshot());
                    }
                }
                catch (IOException)
                {
                    // остров отключился — ждём следующего
                }
                catch (Exception ex)
                {
                    Log.Error("Ошибка pipe", ex);
                    Thread.Sleep(2000);
                }
            }
        }

        private static NamedPipeServerStream CreatePipe()
        {
            var security = new PipeSecurity();
            var user = WindowsIdentity.GetCurrent().User!;
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));

            return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                PipeOptions.None, 0, 0, security);
        }

        private sealed class Item
        {
            public string n { get; set; } = "";
            public string f { get; set; } = "";
            public long u { get; set; }
            public long d { get; set; }
        }

        private static string Snapshot()
        {
            Dictionary<int, (long Up, long Down)> snap;
            lock (Lock)
            {
                snap = _bytes;
                _bytes = new Dictionary<int, (long Up, long Down)>();
            }

            var perApp = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
            foreach (var (pid, b) in snap)
            {
                var (name, friendly) = Resolve(pid);
                if (name.Length == 0) continue;
                if (!perApp.TryGetValue(name, out var item))
                {
                    item = new Item { n = name, f = friendly };
                    perApp[name] = item;
                }
                item.u += b.Up;
                item.d += b.Down;
            }

            // забываем давно закрытые процессы
            if (Procs.Count > 500)
            {
                foreach (var pid in Procs.Keys.Where(p => !snap.ContainsKey(p)).ToList()) Procs.Remove(pid);
            }

            return JsonSerializer.Serialize(perApp.Values);
        }

        private static (string Name, string Friendly) Resolve(int pid)
        {
            if (Procs.TryGetValue(pid, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(2))
                return (c.Name, c.Friendly);

            try
            {
                using var p = Process.GetProcessById(pid);
                string name = p.ProcessName;
                string friendly = name;
                try
                {
                    string? desc = p.MainModule?.FileVersionInfo.FileDescription;
                    if (!string.IsNullOrWhiteSpace(desc) && desc.Length <= 40) friendly = desc.Trim();
                }
                catch { }

                Procs[pid] = (name, friendly, DateTime.UtcNow);
                return (name, friendly);
            }
            catch
            {
                return ("", "");
            }
        }
    }
}
