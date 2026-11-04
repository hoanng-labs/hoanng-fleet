// Hoanng Fleet — manager for a small fleet of Linux machines/VMs: list (add / edit / delete / notes / search),
// one-click SSH, one-click Claude Code, optional "agent box" provisioning (Claude Code as root with
// --dangerously-skip-permissions) on Ubuntu, key repair after a VM rebuild, import of existing keys, zip backup.
// WinForms, .NET Framework 4.x, C# 5 — built by data\src\build.cmd with the csc.exe shipped in Windows.
// Storage next to the exe: data\machines.json (never passwords) + data\keys\<id>\{id_ed25519,.pub,known_hosts,config}.
// A password is used once, held in memory, and handed to ssh through a temp SSH_ASKPASS helper reading an env var.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Hoanng Fleet")]
[assembly: System.Reflection.AssemblyDescription("Quản lý đội máy Linux: SSH một chạm, Claude Code chạy root")]
[assembly: System.Reflection.AssemblyCompany("Hoanng Labs")]
[assembly: System.Reflection.AssemblyProduct("Hoanng Fleet")]
[assembly: System.Reflection.AssemblyCopyright("© 2026 Hoanng Labs")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace HoanngFleet
{
    enum St { Unknown = 0, Online = 1, Checking = 2, Error = 3 }

    [DataContract]
    class Machine
    {
        [DataMember(Order = 1)] public string Id;
        [DataMember(Order = 2)] public string Name;
        [DataMember(Order = 3)] public string Host;
        [DataMember(Order = 4)] public int Port;
        [DataMember(Order = 5)] public string User;
        [DataMember(Order = 6)] public string Dir;
        [DataMember(Order = 7)] public string Note;
        [DataMember(Order = 8)] public bool Agent;          // Claude Code verified runnable with --dangerously-skip-permissions
        [DataMember(Order = 9)] public string KeyText;      // base64 part of our public key (used to remove it remotely)
        [DataMember(Order = 10)] public bool Shared;        // imported key also used elsewhere: never remove it remotely by default
        [DataMember(Order = 11)] public string Created;
        [DataMember(Order = 12)] public string Status;
        [DataMember(Order = 13)] public int State;
        [DataMember(Order = 14)] public string ClaudeVer;
        [DataMember(Order = 15)] public string SyncRemote;  // remote folder to back up
        [DataMember(Order = 16)] public string SyncLocal;   // local destination folder
        [DataMember(Order = 17)] public string SyncExclude; // comma-separated names/globs pruned at any depth
        [DataMember(Order = 18)] public bool SyncMirror;    // move local files deleted remotely into .hf-trash
        [DataMember(Order = 19)] public int SyncEvery;      // minutes between scheduled runs (0 = off)
        [DataMember(Order = 20)] public string LastLocalDir; // last Windows folder shown in the file manager

        public Machine() { Defaults(); }
        [OnDeserializing] void OnDeser(StreamingContext c) { Defaults(); }
        void Defaults()
        {
            Id = Name = Host = Dir = Note = KeyText = Created = Status = ClaudeVer = SyncRemote = SyncLocal = "";
            SyncExclude = "node_modules,.cache,__pycache__,.venv";
            User = "root"; Port = 22; State = 0; SyncMirror = false; SyncEvery = 0; LastLocalDir = "";
        }
        public string Target { get { return User + "@" + Host + (Port != 22 ? ":" + Port : ""); } }
    }

    static class Ops
    {
        public const string AgentTools = "Read,Edit,Write,Bash,Grep,Glob";
        public const string ScriptPin = "github.com/hoanng-dev/ubuntu-agent-vm @ fe5b44c";
        static readonly string SshDir = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", @"System32\OpenSSH");
        public static readonly string Ssh = Path.Combine(SshDir, "ssh.exe");
        public static readonly string Keygen = Path.Combine(SshDir, "ssh-keygen.exe");
        public static readonly string AppDir = Path.GetDirectoryName(Application.ExecutablePath);
        public static readonly string DataDir = Path.Combine(AppDir, "data");
        static readonly string DbFile = Path.Combine(DataDir, "machines.json");
        static readonly object SaveLock = new object();

        public static string KeyDir(Machine m) { return Path.Combine(DataDir, "keys", m.Id); }
        public static string Key(Machine m) { return Path.Combine(KeyDir(m), "id_ed25519"); }
        public static string KnownHosts(Machine m) { return Path.Combine(KeyDir(m), "known_hosts"); }
        static string Cfg(Machine m) { return Path.Combine(KeyDir(m), "config"); }
        public static string Q(string s) { return "\"" + s + "\""; }

        // bash single-quote literal: anything inside is taken verbatim by the remote shell
        public static string SQ(string s) { return "'" + s.Replace("'", "'\\''") + "'"; }

        // One Windows command-line argument, escaped for CommandLineToArgvW (quotes and the backslashes before them).
        public static string QA(string s)
        {
            var sb = new StringBuilder("\"");
            int bs = 0;
            foreach (char c in s)
            {
                if (c == '\\') { bs++; continue; }
                if (c == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); }
                else { sb.Append('\\', bs); sb.Append(c); }
                bs = 0;
            }
            sb.Append('\\', bs * 2); sb.Append('"');
            return sb.ToString();
        }

        public static readonly string Sftp = Path.Combine(SshDir, "sftp.exe");

        // ---------------------------------------------------------------- remote files
        public class RItem { public string Name; public bool Dir; public bool Link; public long Size; public DateTime Mtime; public string Mode; }

        static DateTime FromEpoch(string s)
        {
            double d;
            double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d);
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Math.Floor(d));
        }

        static string Cd(string path) { return path == "" || path == "~" ? "cd" : path.StartsWith("~/") ? "cd ~/" + SQ(path.Substring(2)) : "cd -- " + SQ(path); }

        // Lists one remote directory (GNU find). Returns the canonical path through 'pwd'.
        public static List<RItem> ListDir(Machine m, string path, out string pwd)
        {
            var r = Exec(m, Cd(path) + " 2>/dev/null || { echo HF_NO_DIR; exit 0; }; echo \"HF_PWD:$(pwd)\"; "
                          + "find . -mindepth 1 -maxdepth 1 -printf '%y\\t%Y\\t%s\\t%T@\\t%m\\t%P\\n' 2>/dev/null", null, 60000, null);
            if (r.Output.Contains("HF_NO_DIR")) throw new Exception("Không mở được thư mục " + path + " (không có hoặc không đủ quyền).");
            var pm = Regex.Match(r.Output, "HF_PWD:(.*)");
            if (!pm.Success) throw new Exception(Explain(r.Output, false));
            pwd = pm.Groups[1].Value.TrimEnd('\r');
            var list = new List<RItem>();
            foreach (var raw in r.Output.Split('\n'))
            {
                var p = raw.TrimEnd('\r').Split('\t');
                if (p.Length < 6) continue;
                long size; long.TryParse(p[2], out size);
                list.Add(new RItem { Name = p[5], Link = p[0] == "l", Dir = p[0] == "d" || (p[0] == "l" && p[1] == "d"), Size = size, Mtime = FromEpoch(p[3]), Mode = p[4] });
            }
            return list;
        }

        // sftp batch: one quoted path, backslashes and quotes escaped (local paths are passed with forward slashes)
        public static string SE(string s) { return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }

        public static Result SftpBatch(Machine m, List<string> lines, int timeoutMs, Action<string> onLine)
        {
            string bf = Path.Combine(Path.GetTempPath(), "hf-sftp-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                File.WriteAllText(bf, string.Join("\n", lines.ToArray()) + "\n", new UTF8Encoding(false));
                return Run(Sftp, "-F " + Q(WriteCfgPublic(m)) + " -P " + m.Port + " -o StrictHostKeyChecking=yes -o BatchMode=yes -b " + Q(bf) + " " + m.User + "@" + m.Host,
                           null, null, timeoutMs, onLine);
            }
            finally { try { File.Delete(bf); } catch { } }
        }

        public static string L(string localPath) { return localPath.Replace('\\', '/'); }

        // ---------------------------------------------------------------- folder sync (remote → local backup)
        public static string SyncStateFile(Machine m) { return Path.Combine(DataDir, "sync", m.Id + ".txt"); }
        public static string SyncLogFile(Machine m) { return Path.Combine(DataDir, "sync", m.Id + ".log"); }

        public static string LastSync(Machine m)
        {
            try { return File.Exists(SyncStateFile(m)) ? File.ReadAllText(SyncStateFile(m)).Trim() : ""; } catch { return ""; }
        }

        static string[] Excludes(Machine m)
        {
            var l = new List<string>();
            foreach (var x in (m.SyncExclude ?? "").Split(',')) { string t = x.Trim(); if (t != "" && !t.Contains("'") && !t.Contains("/")) l.Add(t); }
            return l.ToArray();
        }

        static bool WildMatch(string name, string pat)
        {
            return Regex.IsMatch(name, "^" + Regex.Escape(pat).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);
        }

        // Downloads new/changed files (size or mtime differs) from m.SyncRemote into m.SyncLocal, keeping remote mtimes.
        // Mirror mode moves local files that vanished remotely into <local>\.hf-trash\<time>\ (never deletes).
        public static string Sync(Machine m, Action<string> log)
        {
            if (m.SyncRemote == "" || m.SyncLocal == "") throw new Exception("Chưa đặt thư mục nguồn / đích.");
            var ex = Excludes(m);
            string prune = "";
            if (ex.Length > 0)
            {
                var parts = new List<string>();
                foreach (var e in ex) parts.Add("-name " + SQ(e));
                prune = "\\( " + string.Join(" -o ", parts.ToArray()) + " \\) -prune -o ";
            }
            log("Đọc danh sách tệp trên máy: " + m.SyncRemote);
            var r = Exec(m, Cd(m.SyncRemote) + " 2>/dev/null || { echo HF_NO_DIR; exit 0; }; echo \"HF_PWD:$(pwd)\"; find . " + prune + "-type f -printf '%s\\t%T@\\t%P\\n'",
                         null, 600000, null);
            if (r.Output.Contains("HF_NO_DIR")) throw new Exception("Không có thư mục " + m.SyncRemote + " trên máy.");
            var pm = Regex.Match(r.Output, "HF_PWD:(.*)");
            if (!pm.Success) throw new Exception(Explain(r.Output, false));
            string root = pm.Groups[1].Value.TrimEnd('\r');
            var remote = new Dictionary<string, KeyValuePair<long, DateTime>>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in r.Output.Split('\n'))
            {
                var p = raw.TrimEnd('\r').Split(new[] { '\t' }, 3);
                if (p.Length < 3 || p[2] == "") continue;
                long size; if (!long.TryParse(p[0], out size)) continue;
                remote[p[2]] = new KeyValuePair<long, DateTime>(size, FromEpoch(p[1]));
            }
            log("Trên máy có " + remote.Count + " tệp (đã bỏ qua: " + string.Join(", ", ex) + ")");

            Directory.CreateDirectory(m.SyncLocal);
            var todo = new List<string>(); long bytes = 0;
            foreach (var kv in remote)
            {
                if (kv.Key.IndexOfAny(new[] { ':', '*', '?', '"', '<', '>', '|', '\\' }) >= 0) { log("  bỏ qua (tên không hợp lệ trên Windows): " + kv.Key); continue; }
                string lp = Path.Combine(m.SyncLocal, kv.Key.Replace('/', '\\'));
                var fi = new FileInfo(lp);
                if (fi.Exists && fi.Length == kv.Value.Key && Math.Abs((fi.LastWriteTimeUtc - kv.Value.Value).TotalSeconds) < 2) continue;
                todo.Add(kv.Key); bytes += kv.Value.Key;
            }
            log("Cần tải: " + todo.Count + " tệp, " + (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB");

            int failed = 0;
            for (int i = 0; i < todo.Count; i += 1000)
            {
                var lines = new List<string>();
                foreach (var rel in todo.GetRange(i, Math.Min(1000, todo.Count - i)))
                {
                    string lp = Path.Combine(m.SyncLocal, rel.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(lp));
                    lines.Add("-get -p " + SE(root.TrimEnd('/') + "/" + rel) + " " + SE(L(lp)));
                }
                var sr = SftpBatch(m, lines, 3600000, null);
                if (sr.Code != 0 && !sr.Output.Contains("Fetching")) throw new Exception("sftp lỗi: " + Explain(sr.Output, false));
                log("  đã xử lý " + Math.Min(i + 1000, todo.Count) + "/" + todo.Count);
            }
            foreach (var rel in todo)
            {
                string lp = Path.Combine(m.SyncLocal, rel.Replace('/', '\\'));
                var fi = new FileInfo(lp);
                if (!fi.Exists || fi.Length != remote[rel].Key) { failed++; if (failed <= 10) log("  LỖI tải: " + rel); continue; }
                try { File.SetLastWriteTimeUtc(lp, remote[rel].Value); } catch { }
            }

            int moved = 0;
            if (m.SyncMirror)
            {
                string trash = Path.Combine(m.SyncLocal, ".hf-trash", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                foreach (var f in Directory.GetFiles(m.SyncLocal, "*", SearchOption.AllDirectories))
                {
                    string rel = f.Substring(m.SyncLocal.TrimEnd('\\').Length + 1).Replace('\\', '/');
                    if (rel.StartsWith(".hf-trash/")) continue;
                    bool skip = false;
                    foreach (var seg in rel.Split('/')) foreach (var e in ex) if (WildMatch(seg, e)) skip = true;
                    if (skip || remote.ContainsKey(rel)) continue;
                    string dst = Path.Combine(trash, rel.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Move(f, dst); moved++;
                }
                if (moved > 0) log("Gương: chuyển " + moved + " tệp đã xoá trên máy vào " + trash);
            }

            string summary = DateTime.Now.ToString("dd/MM/yyyy HH:mm") + " · " + (failed == 0 ? "OK" : failed + " tệp lỗi") + " · tải " + (todo.Count - failed)
                           + " tệp (" + (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB), tổng " + remote.Count + (moved > 0 ? ", gương " + moved : "");
            Directory.CreateDirectory(Path.GetDirectoryName(SyncStateFile(m)));
            File.WriteAllText(SyncStateFile(m), summary, new UTF8Encoding(false));
            log("XONG: " + summary);
            if (failed > 0) throw new Exception(failed + " tệp tải lỗi (xem nhật ký).");
            return summary;
        }

        // Windows Task Scheduler entry that runs "HoanngFleet.exe --sync <id>" every N minutes (only while this user is logged on).
        public static string TaskName(Machine m) { return "HoanngFleet\\Sync " + m.Id; }

        public static string Schedule(Machine m)
        {
            if (m.SyncEvery <= 0)
            {
                Run("schtasks.exe", "/Delete /F /TN " + Q(TaskName(m)), null, null, 30000, null);
                return "đã tắt sao lưu định kỳ";
            }
            var r = Run("schtasks.exe", "/Create /F /SC MINUTE /MO " + m.SyncEvery + " /TN " + Q(TaskName(m))
                        + " /TR " + QA(Q(Application.ExecutablePath) + " --sync " + m.Id), null, null, 30000, null);
            if (r.Code != 0) throw new Exception("schtasks: " + r.Output.Trim());
            return "sao lưu tự động mỗi " + m.SyncEvery + " phút (Task Scheduler: " + TaskName(m) + ")";
        }

        public static string WriteCfgPublic(Machine m) { return WriteCfg(m); }

        // ---------------------------------------------------------------- storage
        public static List<Machine> Load()
        {
            if (!File.Exists(DbFile)) return new List<Machine>();
            using (var s = File.OpenRead(DbFile))
            {
                var l = (List<Machine>)new DataContractJsonSerializer(typeof(List<Machine>)).ReadObject(s);
                return l ?? new List<Machine>();
            }
        }

        public static void BackupCorrupt()
        {
            try { File.Copy(DbFile, DbFile + ".hong-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); } catch { }
        }

        public static void Save(List<Machine> list)
        {
            lock (SaveLock)
            {
                Directory.CreateDirectory(DataDir);
                string tmp = DbFile + ".tmp";
                using (var s = File.Create(tmp))
                using (var w = JsonReaderWriterFactory.CreateJsonWriter(s, new UTF8Encoding(false), false, true))
                    new DataContractJsonSerializer(typeof(List<Machine>)).WriteObject(w, list);
                if (File.Exists(DbFile)) File.Replace(tmp, DbFile, null); else File.Move(tmp, DbFile);
            }
        }

        public static bool DataWritable()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                string p = Path.Combine(DataDir, ".write-test");
                File.WriteAllText(p, "ok"); File.Delete(p); return true;
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- processes
        public class Result { public int Code; public string Output; }

        public static Result Run(string exe, string args, Dictionary<string, string> env, byte[] stdin, int timeoutMs, Action<string> onLine)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            if (env != null) foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
            var sb = new StringBuilder();
            DataReceivedEventHandler h = (s, e) =>
            {
                if (e.Data == null) return;
                lock (sb) sb.AppendLine(e.Data);
                if (onLine != null) onLine(e.Data);
            };
            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += h; p.ErrorDataReceived += h;
                p.Start();
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                var sIn = p.StandardInput.BaseStream;
                // stdin on its own thread: a large payload must not block the timeout, and a dead ssh must not throw here
                var wt = new Thread(delegate ()
                {
                    try { if (stdin != null) { sIn.Write(stdin, 0, stdin.Length); sIn.Flush(); } sIn.Close(); } catch (IOException) { } catch (ObjectDisposedException) { }
                });
                wt.IsBackground = true; wt.Start();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } lock (sb) sb.AppendLine("(quá thời gian, đã dừng)"); }
                p.WaitForExit(5000);
                string outp; lock (sb) outp = sb.ToString();
                return new Result { Code = p.HasExited ? p.ExitCode : -1, Output = outp };
            }
        }

        // Per-machine ssh config passed with -F: paths with spaces work, and the user's ~/.ssh/config cannot interfere.
        static string WriteCfg(Machine m)
        {
            Directory.CreateDirectory(KeyDir(m));
            string c = "Host *\n  IdentityFile \"" + Key(m).Replace('\\', '/') + "\"\n  UserKnownHostsFile \"" + KnownHosts(m).Replace('\\', '/')
                     + "\"\n  IdentitiesOnly yes\n  ServerAliveInterval 30\n";
            if (!File.Exists(Cfg(m)) || File.ReadAllText(Cfg(m)) != c) File.WriteAllText(Cfg(m), c, new UTF8Encoding(false));
            return Cfg(m);
        }

        static string Base(Machine m) { return "-F " + Q(WriteCfg(m)) + " -p " + m.Port; }

        public static Result Exec(Machine m, string remote, byte[] stdin, int timeoutMs, Action<string> onLine)
        {
            return Run(Ssh, Base(m) + " -o StrictHostKeyChecking=yes -o BatchMode=yes -o ConnectTimeout=10 " + m.User + "@" + m.Host + " " + QA(remote),
                       null, stdin, timeoutMs, onLine);
        }

        // ---------------------------------------------------------------- keys
        public static void FixAcl(string key)
        {
            string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
            var a = Run("icacls.exe", Q(key) + " /inheritance:r /grant:r *" + sid + ":F /remove:g *S-1-5-32-544 *S-1-5-18", null, null, 30000, null);
            if (a.Code != 0) throw new Exception("icacls không đặt được quyền khoá: " + a.Output.Trim());
        }

        public static void ReadKeyText(Machine m)
        {
            string pub = File.ReadAllText(Key(m) + ".pub").Trim();
            var parts = pub.Split(' ');
            m.KeyText = parts.Length > 1 ? parts[1] : "";
        }

        public static void CreateKey(Machine m)
        {
            Directory.CreateDirectory(KeyDir(m));
            foreach (var p in new[] { Key(m), Key(m) + ".pub" }) if (File.Exists(p)) File.Delete(p);
            string comment = "hoanng-fleet " + m.Id + " " + Regex.Replace(Environment.MachineName, @"[^A-Za-z0-9._\-]", "_");
            var r = Run(Keygen, "-q -t ed25519 -N \"\" -C " + Q(comment) + " -f " + Q(Key(m)), null, null, 30000, null);
            if (!File.Exists(Key(m))) throw new Exception("ssh-keygen không tạo được khoá: " + r.Output);
            FixAcl(Key(m));
            ReadKeyText(m);
        }

        public static string PubKey(Machine m) { return File.ReadAllText(Key(m) + ".pub").Trim(); }

        static string HostPattern(Machine m) { return m.Port == 22 ? m.Host : "[" + m.Host + "]:" + m.Port; }

        public static void ForgetHostKey(Machine m)
        {
            if (File.Exists(KnownHosts(m))) Run(Keygen, "-R " + Q(HostPattern(m)) + " -f " + Q(KnownHosts(m)), null, null, 30000, null);
            try { File.Delete(KnownHosts(m) + ".old"); } catch { }
        }

        public static string HostFingerprint(Machine m)
        {
            var r = Run(Keygen, "-l -F " + Q(HostPattern(m)) + " -f " + Q(KnownHosts(m)), null, null, 30000, null);
            var mm = Regex.Match(r.Output, @"SHA256:[A-Za-z0-9+/=]+");
            return mm.Success ? mm.Value : "(chưa có)";
        }

        public static void InstallKey(Machine m, string password)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "hf-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tmp);
                string ap = Path.Combine(tmp, "askpass.cmd");
                // raw UTF-8 bytes: [Console]::Out would use the OEM code page and break accented passwords
                File.WriteAllText(ap, "@powershell -NoProfile -NonInteractive -Command \"$b=[Text.Encoding]::UTF8.GetBytes($env:HF_PW);$o=[Console]::OpenStandardOutput();$o.Write($b,0,$b.Length)\"\r\n", Encoding.ASCII);
                var env = new Dictionary<string, string>();
                env["HF_PW"] = password; env["SSH_ASKPASS"] = ap; env["SSH_ASKPASS_REQUIRE"] = "force"; env["DISPLAY"] = ":0";
                string pub = PubKey(m);
                string remote = "umask 077; mkdir -p ~/.ssh && chmod 700 ~/.ssh && touch ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys && "
                              + "(grep -qF '" + m.KeyText + "' ~/.ssh/authorized_keys || echo '" + pub + "' >> ~/.ssh/authorized_keys) && echo KEY_INSTALLED";
                var r = Run(Ssh, Base(m) + " -o StrictHostKeyChecking=accept-new -o PubkeyAuthentication=no -o PreferredAuthentications=password,keyboard-interactive"
                               + " -o NumberOfPasswordPrompts=1 -o ConnectTimeout=10 " + m.User + "@" + m.Host + " " + Q(remote), env, null, 60000, null);
                if (!r.Output.Contains("KEY_INSTALLED")) throw new Exception(Explain(r.Output, true));
            }
            finally { try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { } }
        }

        public static void TestKey(Machine m)
        {
            var r = Exec(m, "echo KEY_LOGIN_OK", null, 30000, null);
            if (!r.Output.Contains("KEY_LOGIN_OK")) throw new Exception(Explain(r.Output, false));
        }

        // Turns raw ssh output into a short Vietnamese reason.
        public static string Explain(string o, bool passwordStep)
        {
            if (Regex.IsMatch(o, "REMOTE HOST IDENTIFICATION HAS CHANGED|Host key verification failed"))
                return "Khoá máy đã đổi (máy dựng lại?) — dùng \"Cài lại khoá\", tích \"Máy đã dựng lại\"";
            if (o.Contains("UNPROTECTED PRIVATE KEY")) return "Quyền tệp khoá trên máy này quá rộng — dùng \"Cài lại khoá\"";
            if (Regex.IsMatch(o, "Permission denied"))
                return passwordStep ? "Sai mật khẩu, hoặc máy cấm đăng nhập bằng mật khẩu cho user này" : "Máy từ chối khoá (khoá bị gỡ?) — dùng \"Cài lại khoá\"";
            if (Regex.IsMatch(o, "timed out|Connection refused|No route to host|Network is unreachable", RegexOptions.IgnoreCase)) return "Không kết nối được (máy tắt / sai IP / cổng SSH đóng)";
            if (Regex.IsMatch(o, "Could not resolve hostname", RegexOptions.IgnoreCase)) return "Không phân giải được tên máy";
            if (o.Contains("quá thời gian")) return "Quá thời gian chờ";
            foreach (var l in o.Split('\n'))
            {
                string t = l.Trim();
                if (t != "" && !t.StartsWith("@") && !t.StartsWith("**") && !t.StartsWith("Warning: Permanently added")) return t;
            }
            return "Lỗi không rõ";
        }

        // ---------------------------------------------------------------- status / claude
        public static void Check(Machine m)
        {
            var r = Exec(m, "echo ONLINE; command -v claude >/dev/null 2>&1 && claude --version 2>/dev/null | head -1; "
                          + "test -s ~/.claude/.credentials.json && echo CLAUDE_LOGGED_IN", null, 30000, null);
            if (!r.Output.Contains("ONLINE")) { m.State = (int)St.Error; m.Status = Explain(r.Output, false); return; }
            var mm = Regex.Match(r.Output, @"(\d+\.\d+\.\d+) \(Claude Code\)");
            m.ClaudeVer = mm.Success ? mm.Groups[1].Value : "";
            m.State = (int)St.Online;
            m.Status = "Online" + (m.ClaudeVer == "" ? "" : r.Output.Contains("CLAUDE_LOGGED_IN") ? " · Claude đã đăng nhập" : " · Claude chưa đăng nhập");
        }

        public static bool ClaudeLoggedIn(Machine m)
        {
            return Exec(m, "test -s ~/.claude/.credentials.json && echo CLAUDE_LOGGED_IN", null, 20000, null).Output.Contains("CLAUDE_LOGGED_IN");
        }

        public static byte[] AgentScript()
        {
            using (var s = typeof(Ops).Assembly.GetManifestResourceStream("setup-ubuntu-agent-vm.sh"))
            using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); }
        }

        public static bool RunAgentSetup(Machine m, string internalIp, Action<string> log)
        {
            string args = internalIp == "" ? "--no-internal" : "--internal-ip " + internalIp;
            var r = Exec(m, "bash -s -- " + args + " --ssh-key '" + PubKey(m) + "'", AgentScript(), 900000, log);
            if (r.Code == 255 && !r.Output.Contains("==>")) throw new Exception(Explain(r.Output, false));
            return r.Output.Contains("ALL CHECKS PASSED");
        }

        public static bool TestClaude(Machine m, out string msg)
        {
            var r = Exec(m, "cd /tmp && timeout 120 claude --dangerously-skip-permissions --allowedTools " + AgentTools
                          + " -p 'Reply with exactly: AGENT_OK'", null, 150000, null);
            string o = r.Output.Trim();
            if (o.Contains("AGENT_OK")) { msg = "claude --dangerously-skip-permissions chạy được dưới " + m.User; return true; }
            if (Regex.IsMatch(o, "root/sudo|cannot be used with root", RegexOptions.IgnoreCase)) msg = "bị chặn khi chạy root (thiếu IS_SANDBOX=1)";
            else if (Regex.IsMatch(o, "not found", RegexOptions.IgnoreCase)) msg = "máy chưa có claude";
            else if (Regex.IsMatch(o, "log ?in|/login|auth|credential|API key", RegexOptions.IgnoreCase)) msg = "Claude chưa đăng nhập — bấm \"Mở Claude\" và đăng nhập một lần";
            else msg = "chưa chạy được: " + (o.Length > 300 ? o.Substring(0, 300) : o);
            return false;
        }

        public static string UpdateClaude(Machine m, Action<string> log)
        {
            var r = Exec(m, "curl -fsSL https://claude.ai/install.sh | bash -s -- latest 2>&1 | tail -4; "
                          + "[ \"$(id -u)\" -eq 0 ] && [ -x /root/.local/bin/claude ] && ln -sf /root/.local/bin/claude /usr/local/bin/claude; "
                          + "PATH=$HOME/.local/bin:$PATH claude --version", null, 600000, log);
            var mm = Regex.Match(r.Output, @"(\d+\.\d+\.\d+) \(Claude Code\)");
            return mm.Success ? mm.Groups[1].Value : "";
        }

        // Removes our public key line from the remote authorized_keys: backup first, never leave the file empty.
        public static string RemoveRemoteKey(Machine m)
        {
            if (m.KeyText == "" || !Regex.IsMatch(m.KeyText, "^[A-Za-z0-9+/=]+$")) return "không có dấu nhận khoá, bỏ qua";
            string ts = DateTime.Now.ToString("yyyyMMddHHmmss");
            var r = Exec(m, "f=~/.ssh/authorized_keys; [ -f $f ] || { echo NOFILE; exit 0; }; "
                          + "n=$(grep -cv -F '" + m.KeyText + "' $f); if [ \"$n\" -eq 0 ]; then echo ONLY_KEY; exit 0; fi; "
                          + "cp -p $f $f.hf-bak-" + ts + " && grep -vF '" + m.KeyText + "' $f > $f.hf; [ $? -le 1 ] && cat $f.hf > $f && rm -f $f.hf && echo REMOVED",
                          null, 30000, null);
            if (r.Output.Contains("REMOVED")) return "đã gỡ khoá trên máy (bản cũ: ~/.ssh/authorized_keys.hf-bak-" + ts + ")";
            if (r.Output.Contains("ONLY_KEY")) return "giữ khoá trên máy vì đó là khoá duy nhất (tránh mất đường vào)";
            if (r.Output.Contains("NOFILE")) return "máy không có authorized_keys";
            return "không gỡ được khoá trên máy: " + Explain(r.Output, false);
        }

        public static void Connect(Machine m, bool claude) { Connect(m, claude, null); }

        // dir == null → the machine's default folder; otherwise open the session (or Claude) in that remote folder.
        public static void Connect(Machine m, bool claude, string dir)
        {
            string target = dir ?? m.Dir;
            if (target.IndexOfAny(new[] { ';', '"', '%', '\n', '\r' }) >= 0) throw new Exception("Đường dẫn có ký tự ; \" % — không mở được bằng cửa sổ dòng lệnh.");
            string cd = target != "" ? Cd(target) + " && " : "";
            string remote = claude ? cd + "exec claude --dangerously-skip-permissions --allowedTools " + AgentTools : cd + "exec $SHELL -l";
            string sshArgs = Base(m) + " -o StrictHostKeyChecking=yes -t " + m.User + "@" + m.Host + " " + QA(remote);
            string wt = WindowsTerminal();
            if (wt != null)
            {
                // A tab in one "HoanngFleet" window, titled after the machine (+ folder when not the default);
                // --suppressApplicationTitle stops bash's PS1 and Claude Code from renaming the tab.
                // ';' is wt's command separator, so keep it out of the title.
                string folder = dir == null || dir == m.Dir ? "" : " · " + (dir.TrimEnd('/') == "" ? "/" : dir.TrimEnd('/').Substring(dir.TrimEnd('/').LastIndexOf('/') + 1));
                string tabTitle = Regex.Replace(m.Name + (claude ? " · Claude" : "") + folder, "[;\"]", "_");
                Process.Start(new ProcessStartInfo(wt, "-w HoanngFleet new-tab --title " + Q(tabTitle) + " --suppressApplicationTitle"
                                                      + " --tabColor " + (claude ? "#d97757" : "#0d6efd") + " " + Q(Ssh) + " " + sshArgs) { UseShellExecute = false });
                return;
            }
            string title = Regex.Replace(m.Name + (claude ? " - Claude" : "") + " (Hoanng Fleet)", "[&|<>^%\"]", "_");
            string inner = "chcp 65001 >nul & title " + title + " & " + Q(Ssh) + " " + sshArgs + " || pause";
            Process.Start(new ProcessStartInfo("cmd.exe", "/s /c \"" + inner + "\"") { UseShellExecute = true });
        }

        // Windows Terminal app-execution alias, if installed for this user.
        static string WindowsTerminal()
        {
            string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\wt.exe");
            return File.Exists(p) ? p : null;
        }

        public static Version SshVersion()
        {
            try
            {
                var r = Run(Ssh, "-V", null, null, 10000, null);
                var mm = Regex.Match(r.Output, @"OpenSSH_for_Windows_(\d+)\.(\d+)|OpenSSH_(\d+)\.(\d+)");
                if (mm.Success) return mm.Groups[1].Success ? new Version(int.Parse(mm.Groups[1].Value), int.Parse(mm.Groups[2].Value)) : new Version(int.Parse(mm.Groups[3].Value), int.Parse(mm.Groups[4].Value));
            }
            catch { }
            return null;
        }
    }

    class MainForm : Form
    {
        static readonly Color Night = ColorTranslator.FromHtml("#061f47");
        static readonly Color Azure = ColorTranslator.FromHtml("#0d6efd");
        static readonly Color AzureText = ColorTranslator.FromHtml("#0b5cd5");
        static readonly Color Danger = ColorTranslator.FromHtml("#d13438");
        static readonly Color Line = ColorTranslator.FromHtml("#e1e4e8");
        static readonly Color Muted = ColorTranslator.FromHtml("#605e5c");
        static readonly Color Soft = ColorTranslator.FromHtml("#faf9f8");
        static readonly string Version = "1.0.0";

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, string l);

        List<Machine> machines = new List<Machine>();
        bool loadFailed;
        Panel header, bar, listHost, empty, details, toast;
        ListView list;
        TextBox search, dNote;
        Label status, emptyTitle, emptySub, toastText;
        Button emptyBtn;
        LinkLabel dKey;
        Label[] dVal = new Label[8];
        List<Control> needSel = new List<Control>();
        SplitContainer split;
        PictureBox overlay;
        System.Windows.Forms.Timer toastTimer;
        Panel blade, bladeBody;
        Label bladeTitle;
        Button bladePrimary, bladeCancel;
        TextBox bladeLog;
        ProgressBar bladeBusy;
        int by;
        bool busy;
        Action primaryAction;

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--sync") return HeadlessSync(args[1]);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => MessageBox.Show("Lỗi: " + e.Exception.Message, "Hoanng Fleet", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.Run(new MainForm());
            return 0;
        }

        // Scheduled backup (Task Scheduler): no window; appends to data\sync\<id>.log. Exit 0 = OK.
        static int HeadlessSync(string id)
        {
            Machine m = null;
            try { foreach (var x in Ops.Load()) if (x.Id == id) m = x; } catch { }
            if (m == null) return 2;
            string logFile = Ops.SyncLogFile(m);
            Directory.CreateDirectory(Path.GetDirectoryName(logFile));
            Action<string> log = delegate (string s)
            {
                try { File.AppendAllText(logFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + s + "\r\n", new UTF8Encoding(false)); } catch { }
            };
            using (var mx = new Mutex(false, "HoanngFleetSync-" + id))
            {
                if (!mx.WaitOne(0)) { log("bỏ qua: lượt đồng bộ trước còn đang chạy"); return 3; }
                try { log("=== bắt đầu (Task Scheduler) " + m.SyncRemote + " -> " + m.SyncLocal); Ops.Sync(m, log); return 0; }
                catch (Exception ex)
                {
                    log("LỖI: " + ex.Message);
                    try { File.WriteAllText(Ops.SyncStateFile(m), DateTime.Now.ToString("dd/MM/yyyy HH:mm") + " · LỖI: " + ex.Message, new UTF8Encoding(false)); } catch { }
                    return 1;
                }
                finally { mx.ReleaseMutex(); }
            }
        }

        MainForm()
        {
            Text = "Hoanng Fleet — Quản lý máy agent";
            ClientSize = new Size(1200, 680);
            MinimumSize = new Size(1080, 560);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 10);
            BackColor = Color.White;
            KeyPreview = false;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildHeader();
            BuildBar();
            BuildToast();
            BuildBody();
            status = new Label { Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(12, 4, 0, 0), ForeColor = Muted, BackColor = ColorTranslator.FromHtml("#f6f8fa"), Font = new Font("Segoe UI", 9) };
            BuildBlade();

            Controls.Add(split); Controls.Add(toast); Controls.Add(bar); Controls.Add(header); Controls.Add(status);
            overlay = new PictureBox { Visible = false, SizeMode = PictureBoxSizeMode.Normal, Cursor = Cursors.Hand };
            overlay.Click += delegate { CloseBlade(); };
            Controls.Add(overlay); Controls.Add(blade);
            Resize += delegate { if (blade.Visible) { PlaceBlade(); RenderOverlay(); } };
            Shown += delegate
            {
                try { split.SplitterDistance = Math.Max(150, split.Height - 190); } catch { }
                StartupChecks();
            };

            try { machines = Ops.Load(); }
            catch (Exception ex)
            {
                loadFailed = true; Ops.BackupCorrupt();
                MessageBox.Show("Không đọc được data\\machines.json (" + ex.Message + ").\r\nĐã chép bản hỏng ra machines.json.hong-*; ứng dụng sẽ KHÔNG ghi đè tệp cho tới khi anh sửa hoặc khôi phục bản sao lưu.",
                                "Hoanng Fleet", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            RefreshList();
        }

        void StartupChecks()
        {
            if (!File.Exists(Ops.Ssh) || !File.Exists(Ops.Keygen)) { Toast("Máy chưa có OpenSSH Client — Settings › Apps › Optional features › OpenSSH Client.", true); return; }
            if (!Ops.DataWritable()) { Toast("Không ghi được vào " + Ops.DataDir + " — chép ứng dụng ra thư mục khác (vd C:\\HoanngFleet).", true); return; }
            var v = Ops.SshVersion();
            if (v != null && v < new Version(8, 4)) Toast("OpenSSH " + v + " cũ (cần ≥ 8.4 để tự nhập mật khẩu) — cập nhật Windows / OpenSSH.", true);
            SetStatus(machines.Count + " máy · dữ liệu: " + Ops.DataDir);
        }

        // ---------------------------------------------------------------- layout
        void BuildHeader()
        {
            header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Night };
            var logo = new PictureBox { Location = new Point(14, 8), Size = new Size(48, 48), SizeMode = PictureBoxSizeMode.Zoom };
            try { var s = typeof(MainForm).Assembly.GetManifestResourceStream("logo-128.png"); if (s != null) logo.Image = Image.FromStream(s); } catch { }
            header.Controls.Add(logo);
            header.Controls.Add(new Label { Text = "Hoanng Fleet", ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 15), Location = new Point(72, 6), AutoSize = true });
            header.Controls.Add(new Label { Text = "Đội máy Linux trong LAN · SSH một chạm · Claude Code chạy root", ForeColor = ColorTranslator.FromHtml("#b9c8e3"), Font = new Font("Segoe UI", 9.5f), Location = new Point(74, 38), AutoSize = true });
            var about = new Button { Text = "?", FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Night, Size = new Size(36, 36), Font = new Font("Segoe UI Semibold", 12), Cursor = Cursors.Hand, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            about.FlatAppearance.BorderSize = 0; about.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#0d2b5e");
            about.Location = new Point(ClientSize.Width - 52, 14);
            about.Click += delegate { OpenAbout(); };
            new ToolTip().SetToolTip(about, "Giới thiệu & phím tắt");
            header.Controls.Add(about);
        }

        void BuildBar()
        {
            bar = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Color.White };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 7, 0, 0), WrapContents = false, BackColor = Color.White };
            var tip = new ToolTip();
            flow.Controls.Add(Cmd("＋  Thêm máy", "Thêm máy (Ctrl+N)", tip, false, delegate { OpenAdd(); }));
            flow.Controls.Add(Cmd("▶  Kết nối", "Mở SSH vào máy (Enter / nhấp đúp)", tip, true, delegate { DoConnect(false); }));
            flow.Controls.Add(Cmd("◆  Mở Claude", "Mở Claude Code trên máy (Ctrl+Enter)", tip, true, delegate { DoConnect(true); }));
            flow.Controls.Add(Cmd("📁  Tệp", "Duyệt, tải về, tải lên, đồng bộ sao lưu (Ctrl+O)", tip, true, delegate { OpenFiles(); }));
            flow.Controls.Add(Cmd("⚙  Cài Claude", "Cài máy agent: Claude Code chạy root", tip, true, delegate { var m = Sel(); if (m != null) OpenAgent(m); }));
            flow.Controls.Add(Cmd("✎  Sửa", "Sửa tên, địa chỉ, thư mục, ghi chú (F2)", tip, true, delegate { var m = Sel(); if (m != null) OpenEdit(m); }));
            flow.Controls.Add(Cmd("✕  Xoá", "Xoá máy (Del)", tip, true, delegate { var m = Sel(); if (m != null) OpenDelete(m); }));
            flow.Controls.Add(Cmd("↻  Kiểm tra", "Kiểm tra mọi máy (F5)", tip, false, delegate { CheckAll(); }));
            var more = Cmd("⋯  Thêm", "Các lệnh khác", tip, false, null);
            var mm = new ContextMenuStrip { Font = new Font("Segoe UI", 10) };
            mm.Items.Add("Nhập kết nối có sẵn…", null, delegate { OpenImport(); });
            mm.Items.Add("Cài lại khoá (máy dựng lại / khoá bị gỡ)…", null, delegate { var m = Sel(); if (m != null) OpenRekey(m); });
            mm.Items.Add("Cập nhật Claude Code…", null, delegate { var m = Sel(); if (m != null) OpenUpdate(m); });
            mm.Items.Add("Mở thư mục khoá", null, delegate { var m = Sel(); if (m != null) Process.Start("explorer.exe", Ops.Q(Ops.KeyDir(m))); });
            mm.Items.Add(new ToolStripSeparator());
            mm.Items.Add("Sao lưu (zip)…", null, delegate { DoBackup(); });
            mm.Items.Add("Khôi phục từ bản sao lưu…", null, delegate { DoRestore(); });
            mm.Items.Add(new ToolStripSeparator());
            mm.Items.Add("Giới thiệu", null, delegate { OpenAbout(); });
            more.Click += delegate { mm.Show(more, new Point(0, more.Height)); };
            flow.Controls.Add(more);

            var sp = new Panel { Dock = DockStyle.Right, Width = 280, Padding = new Padding(0, 9, 16, 0), BackColor = Color.White };
            search = new TextBox { Dock = DockStyle.Top, Font = new Font("Segoe UI", 10), Name = "search" };
            search.HandleCreated += delegate { SendMessage(search.Handle, 0x1501, (IntPtr)1, "🔍  Lọc theo tên, IP, ghi chú  (Ctrl+F)"); };
            search.TextChanged += delegate { RefreshList(); };
            sp.Controls.Add(search);
            bar.Controls.Add(flow); bar.Controls.Add(sp);
            bar.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Line });
        }

        Button Cmd(string text, string tipText, ToolTip tip, bool requiresSelection, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, Height = 32, FlatStyle = FlatStyle.Flat, ForeColor = AzureText, BackColor = Color.White,
                                 Margin = new Padding(0, 0, 2, 0), Padding = new Padding(4, 0, 4, 0), Cursor = Cursors.Hand };
            b.FlatAppearance.BorderSize = 0; b.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#eef4ff");
            if (click != null) b.Click += click;
            tip.SetToolTip(b, tipText);
            if (requiresSelection) needSel.Add(b);
            return b;
        }

        void BuildToast()
        {
            toast = new Panel { Dock = DockStyle.Top, Height = 34, Visible = false };
            toastText = new Label { Dock = DockStyle.Fill, Padding = new Padding(16, 8, 0, 0), Font = new Font("Segoe UI", 9.5f) };
            var x = new Label { Text = "✕", Dock = DockStyle.Right, Width = 34, TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand };
            x.Click += delegate { toast.Visible = false; };
            toast.Controls.Add(toastText); toast.Controls.Add(x);
            toastTimer = new System.Windows.Forms.Timer { Interval = 6000 };
            toastTimer.Tick += delegate { toastTimer.Stop(); toast.Visible = false; };
        }

        void Toast(string msg, bool error)
        {
            UI(delegate
            {
                toast.BackColor = error ? ColorTranslator.FromHtml("#fde7e9") : ColorTranslator.FromHtml("#dff6dd");
                toastText.ForeColor = error ? ColorTranslator.FromHtml("#a4262c") : ColorTranslator.FromHtml("#107c10");
                toastText.Text = (error ? "⚠  " : "✓  ") + msg;
                toast.Visible = true; toastTimer.Stop();
                if (!error) toastTimer.Start();
            });
        }

        void BuildBody()
        {
            split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2, SplitterWidth = 1, BackColor = Line, Panel1MinSize = 120, Panel2MinSize = 120 };
            split.Panel1.BackColor = Color.White; split.Panel2.BackColor = Color.White;

            listHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
                                  BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 10), Name = "machines" };
            var il = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(18, 30) };
            il.Images.Add("0", Dot(ColorTranslator.FromHtml("#a19f9d")));
            il.Images.Add("1", Dot(ColorTranslator.FromHtml("#107c10")));
            il.Images.Add("2", Dot(ColorTranslator.FromHtml("#f7a600")));
            il.Images.Add("3", Dot(Danger));
            list.SmallImageList = il;
            list.Columns.Add("Tên", 190); list.Columns.Add("Địa chỉ", 150); list.Columns.Add("User", 70); list.Columns.Add("Thư mục", 180);
            list.Columns.Add("Claude", 90); list.Columns.Add("Trạng thái", 230); list.Columns.Add("Ghi chú", 200);
            list.DoubleClick += delegate { DoConnect(false); };
            list.ItemSelectionChanged += delegate { BeginInvoke(new Action(UpdateSelection)); };
            var menu = new ContextMenuStrip { Font = new Font("Segoe UI", 10) };
            menu.Items.Add("Kết nối", null, delegate { DoConnect(false); });
            menu.Items.Add("Mở Claude", null, delegate { DoConnect(true); });
            menu.Items.Add("Mở Claude ở thư mục khác…", null, delegate { OpenFiles(); });
            menu.Items.Add("Tệp (duyệt / tải về / tải lên)…", null, delegate { OpenFiles(); });
            menu.Items.Add("Đồng bộ sao lưu…", null, delegate { var m = Sel(); if (m != null) OpenSync(m, null); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Cài Claude chạy root…", null, delegate { var m = Sel(); if (m != null) OpenAgent(m); });
            menu.Items.Add("Cập nhật Claude Code…", null, delegate { var m = Sel(); if (m != null) OpenUpdate(m); });
            menu.Items.Add("Cài lại khoá…", null, delegate { var m = Sel(); if (m != null) OpenRekey(m); });
            menu.Items.Add("Kiểm tra máy này", null, delegate { var m = Sel(); if (m != null) CheckOne(m); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Sửa / ghi chú…", null, delegate { var m = Sel(); if (m != null) OpenEdit(m); });
            menu.Items.Add("Mở thư mục khoá", null, delegate { var m = Sel(); if (m != null) Process.Start("explorer.exe", Ops.Q(Ops.KeyDir(m))); });
            menu.Items.Add("Xoá…", null, delegate { var m = Sel(); if (m != null) OpenDelete(m); });
            list.ContextMenuStrip = menu;

            empty = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Visible = false };
            emptyTitle = new Label { Text = "Chưa có máy nào", Font = new Font("Segoe UI Semibold", 15), AutoSize = true, Location = new Point(40, 60) };
            emptySub = new Label { Text = "Thêm một máy Linux trong LAN: nhập IP, user, mật khẩu một lần — lần sau bấm là vào, không cần mật khẩu.", ForeColor = Muted, AutoSize = true, Location = new Point(42, 98) };
            emptyBtn = new Button { Text = "＋  Thêm máy đầu tiên", Location = new Point(42, 136), Size = new Size(190, 36), FlatStyle = FlatStyle.Flat, BackColor = Azure, ForeColor = Color.White, Cursor = Cursors.Hand };
            emptyBtn.FlatAppearance.BorderSize = 0; emptyBtn.Click += delegate { OpenAdd(); };
            var importLink = new LinkLabel { Text = "hoặc nhập kết nối có sẵn", AutoSize = true, Location = new Point(246, 145), LinkColor = AzureText };
            importLink.Click += delegate { OpenImport(); };
            empty.Controls.Add(emptyTitle); empty.Controls.Add(emptySub); empty.Controls.Add(emptyBtn); empty.Controls.Add(importLink);
            listHost.Controls.Add(list); listHost.Controls.Add(empty);
            split.Panel1.Controls.Add(listHost);

            details = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(16, 10, 16, 10) };
            details.Controls.Add(new Label { Text = "Thông tin chính", Font = new Font("Segoe UI Semibold", 11), Location = new Point(16, 8), AutoSize = true });
            string[] labels = { "Tên", "Địa chỉ", "Thư mục", "Claude", "Trạng thái", "Tạo lúc", "Khoá", "Sao lưu" };
            for (int i = 0; i < 8; i++)
            {
                int col = i < 4 ? 0 : 1, row = i < 4 ? i : i - 4;
                int x = 16 + col * 380, yy = 38 + row * 26;
                details.Controls.Add(new Label { Text = labels[i], ForeColor = Muted, Location = new Point(x, yy), Size = new Size(90, 22), Font = new Font("Segoe UI", 9.5f) });
                if (i == 6)
                {
                    dKey = new LinkLabel { Location = new Point(x + 92, yy), Size = new Size(280, 22), LinkColor = AzureText, Font = new Font("Segoe UI", 9.5f), AutoEllipsis = true };
                    dKey.Click += delegate { var m = SelQuiet(); if (m != null) Process.Start("explorer.exe", Ops.Q(Ops.KeyDir(m))); };
                    details.Controls.Add(dKey);
                }
                else
                {
                    dVal[i] = new Label { Location = new Point(x + 92, yy), Size = new Size(280, 22), AutoEllipsis = true, Font = new Font("Segoe UI", 9.5f) };
                    details.Controls.Add(dVal[i]);
                }
            }
            details.Controls.Add(new Label { Text = "Ghi chú", ForeColor = Muted, Location = new Point(776, 38), AutoSize = true, Font = new Font("Segoe UI", 9.5f) });
            dNote = new TextBox { Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Soft, Location = new Point(776, 62), Size = new Size(300, 100),
                                  ScrollBars = ScrollBars.Vertical, Font = new Font("Segoe UI", 9.5f) };
            details.Resize += delegate { dNote.Size = new Size(Math.Max(120, details.ClientSize.Width - 776 - 16), Math.Max(40, details.ClientSize.Height - 62 - 10)); };
            details.Controls.Add(dNote);
            split.Panel2.Controls.Add(details);
        }

        static Bitmap Dot(Color c)
        {
            var b = new Bitmap(18, 30);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var br = new SolidBrush(c)) g.FillEllipse(br, 4, 10, 10, 10);
            }
            return b;
        }

        // ---------------------------------------------------------------- list / selection
        void UI(Action a)
        {
            if (IsDisposed) return;
            try
            {
                if (!InvokeRequired) a();                 // UI thread (also before the handle exists, e.g. in the constructor)
                else if (IsHandleCreated) BeginInvoke(a); // worker thread
            }
            catch (InvalidOperationException) { }
        }

        Machine SelQuiet() { return list.SelectedItems.Count == 0 ? null : (Machine)list.SelectedItems[0].Tag; }
        Machine Sel() { var m = SelQuiet(); if (m == null) SetStatus("Chọn một máy trong bảng trước."); return m; }
        void SetStatus(string s) { UI(delegate { status.Text = s; }); }

        void RefreshList()
        {
            UI(delegate
            {
                string q = search.Text.Trim();
                var sel = SelQuiet();
                list.BeginUpdate(); list.Items.Clear();
                foreach (var m in machines)
                {
                    if (q != "" && (m.Name + " " + m.Host + " " + m.User + " " + m.Note + " " + m.Dir).IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string claude = m.ClaudeVer != "" ? m.ClaudeVer + (m.Agent ? " ✓" : "") : (m.Agent ? "✓" : "—");
                    var it = new ListViewItem(new[] { m.Name, m.Host + (m.Port != 22 ? ":" + m.Port : ""), m.User, m.Dir == "" ? "~" : m.Dir, claude,
                                                      m.Status == "" ? "Chưa kiểm" : m.Status, m.Note.Replace("\r", " ").Replace("\n", " ") }) { Tag = m, ImageKey = m.State.ToString() };
                    list.Items.Add(it);
                    if (sel == m) { it.Selected = true; it.Focused = true; }
                }
                list.EndUpdate();
                bool none = list.Items.Count == 0;
                emptyTitle.Text = machines.Count == 0 ? "Chưa có máy nào" : "Không có máy khớp \"" + q + "\"";
                emptySub.Visible = emptyBtn.Visible = machines.Count == 0;
                foreach (Control c in empty.Controls) if (c is LinkLabel) c.Visible = machines.Count == 0;
                empty.Visible = none; if (none) empty.BringToFront(); else list.BringToFront();
                if (status.Text == "" || Regex.IsMatch(status.Text, @"^\d+ máy · "))
                    status.Text = machines.Count + " máy" + (q != "" ? " · đang lọc: " + list.Items.Count : "") + " · dữ liệu: " + Ops.DataDir;
                UpdateSelection();
            });
        }

        void UpdateSelection()
        {
            var m = SelQuiet();
            foreach (var c in needSel) c.Enabled = m != null && !busy;
            if (m == null)
            {
                for (int i = 0; i < 8; i++) if (dVal[i] != null) dVal[i].Text = i == 0 ? "Chọn một máy để xem chi tiết" : "";
                dKey.Text = ""; dNote.Text = ""; return;
            }
            dVal[0].Text = m.Name; dVal[1].Text = m.Target; dVal[2].Text = m.Dir == "" ? "~ (thư mục nhà)" : m.Dir;
            dVal[3].Text = (m.ClaudeVer != "" ? "v" + m.ClaudeVer : "chưa rõ") + (m.Agent ? " · chạy root được" : "");
            dVal[4].Text = m.Status == "" ? "Chưa kiểm" : m.Status;
            dVal[4].ForeColor = m.State == (int)St.Error ? Danger : m.State == (int)St.Online ? ColorTranslator.FromHtml("#107c10") : Color.Black;
            dVal[5].Text = m.Created + (m.Shared ? " · khoá nhập từ ngoài" : "");
            dKey.Text = Ops.KeyDir(m); dNote.Text = m.Note == "" ? "(không có)" : m.Note;
            string ls = Ops.LastSync(m);
            dVal[7].Text = m.SyncLocal == "" ? "chưa đặt" : (ls == "" ? "chưa chạy" : ls) + (m.SyncEvery > 0 ? " · tự động " + m.SyncEvery + " phút" : "");
            dVal[7].ForeColor = ls.Contains("LỖI") || ls.Contains("tệp lỗi") ? Danger : Color.Black;
        }

        void SaveAll()
        {
            if (loadFailed) { Toast("Không lưu: machines.json đang hỏng — khôi phục bản sao lưu trước.", true); return; }
            try { Ops.Save(machines); } catch (Exception ex) { Toast("Không lưu được machines.json: " + ex.Message, true); }
        }

        void Commit() { UI(delegate { SaveAll(); RefreshList(); }); }

        // ---------------------------------------------------------------- keyboard
        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            if (blade.Visible)
            {
                if (k == Keys.Escape) { CloseBlade(); return true; }
                if (k == Keys.Enter && !(ActiveControl is TextBox && ((TextBox)ActiveControl).Multiline) && bladePrimary.Visible && bladePrimary.Enabled)
                { bladePrimary.PerformClick(); return true; }
                return base.ProcessCmdKey(ref msg, k);
            }
            if (k == (Keys.Control | Keys.N)) { OpenAdd(); return true; }
            if (k == (Keys.Control | Keys.F)) { search.Focus(); search.SelectAll(); return true; }
            if (k == Keys.F5) { CheckAll(); return true; }
            if (k == (Keys.Control | Keys.O)) { OpenFiles(); return true; }
            if (k == Keys.Escape && search.Focused) { search.Clear(); list.Focus(); return true; }
            if (list.Focused)   // ActiveControl is the SplitContainer here, so ask the list itself
            {
                if (k == Keys.Enter) { DoConnect(false); return true; }
                if (k == (Keys.Control | Keys.Enter)) { DoConnect(true); return true; }
                if (k == Keys.Delete) { var m = Sel(); if (m != null) OpenDelete(m); return true; }
                if (k == Keys.F2) { var m = Sel(); if (m != null) OpenEdit(m); return true; }
            }
            return base.ProcessCmdKey(ref msg, k);
        }

        // ---------------------------------------------------------------- blade (Azure-style flyout)
        void BuildBlade()
        {
            blade = new Panel { Width = 480, BackColor = Color.White, Visible = false };
            var edge = new Panel { Dock = DockStyle.Left, Width = 1, BackColor = ColorTranslator.FromHtml("#c8c6c4") };
            var top = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = Color.White };
            bladeTitle = new Label { Location = new Point(24, 15), Size = new Size(400, 30), AutoEllipsis = true, Font = new Font("Segoe UI Semibold", 14) };
            var close = new Button { Text = "✕", FlatStyle = FlatStyle.Flat, Size = new Size(36, 36), Location = new Point(432, 11), Cursor = Cursors.Hand };
            close.FlatAppearance.BorderSize = 0; close.Click += delegate { CloseBlade(); };
            top.Controls.Add(bladeTitle); top.Controls.Add(close);
            var foot = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = Color.White };
            bladePrimary = new Button { Location = new Point(24, 16), Size = new Size(140, 34), FlatStyle = FlatStyle.Flat, BackColor = Azure, ForeColor = Color.White, Cursor = Cursors.Hand, Name = "bladePrimary" };
            bladePrimary.FlatAppearance.BorderSize = 0;
            bladePrimary.Click += delegate { if (!busy && primaryAction != null) primaryAction(); };
            bladeCancel = new Button { Text = "Đóng", Location = new Point(174, 16), Size = new Size(100, 34), FlatStyle = FlatStyle.Flat, BackColor = Color.White, Cursor = Cursors.Hand };
            bladeCancel.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#8a8886");
            bladeCancel.Click += delegate { CloseBlade(); };
            bladeBusy = new ProgressBar { Style = ProgressBarStyle.Marquee, Location = new Point(290, 26), Size = new Size(160, 12), Visible = false, MarqueeAnimationSpeed = 30 };
            foot.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Line });
            foot.Controls.Add(bladePrimary); foot.Controls.Add(bladeCancel); foot.Controls.Add(bladeBusy);
            bladeBody = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White };
            blade.Controls.Add(bladeBody); blade.Controls.Add(foot); blade.Controls.Add(top); blade.Controls.Add(edge);
        }

        void PlaceBlade() { blade.SetBounds(ClientSize.Width - blade.Width, header.Height, blade.Width, ClientSize.Height - header.Height - status.Height); }

        void RenderOverlay()
        {
            var r = new Rectangle(0, header.Height, ClientSize.Width, ClientSize.Height - header.Height - status.Height);
            if (r.Width <= 0 || r.Height <= 0) return;
            bool bv = blade.Visible, ov = overlay.Visible;
            blade.Visible = false; overlay.Visible = false;
            var full = new Bitmap(ClientSize.Width, ClientSize.Height);
            try { DrawToBitmap(full, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height)); } catch { }
            var img = new Bitmap(r.Width, r.Height);
            using (var g = Graphics.FromImage(img))
            {
                // DrawToBitmap includes the non-client frame offset; draw the client area of the capture
                var pt = PointToScreen(Point.Empty);
                int dx = pt.X - Left, dy = pt.Y - Top;
                g.DrawImage(full, new Rectangle(0, 0, r.Width, r.Height), new Rectangle(dx, dy + r.Y, r.Width, r.Height), GraphicsUnit.Pixel);
                using (var b = new SolidBrush(Color.FromArgb(110, 0, 0, 0))) g.FillRectangle(b, 0, 0, r.Width, r.Height);
            }
            full.Dispose();
            var old = overlay.Image; overlay.Image = img; if (old != null) old.Dispose();
            overlay.Bounds = r;
            overlay.Visible = ov || bv; blade.Visible = bv;
            overlay.BringToFront(); blade.BringToFront();
        }

        void OpenBlade(string title, string primary, bool danger)
        {
            if (busy) { SetStatus("Đang chạy một việc, chờ xong đã."); return; }
            bladeTitle.Text = title; bladePrimary.Text = primary ?? ""; bladePrimary.Visible = primary != null; bladePrimary.Enabled = true;
            bladePrimary.BackColor = danger ? Danger : Azure;
            primaryAction = null;
            bladeBody.SuspendLayout(); bladeBody.Controls.Clear(); bladeBody.AutoScrollPosition = Point.Empty; bladeBody.ResumeLayout();
            by = 8; bladeLog = null;
            PlaceBlade();
            if (!blade.Visible) { overlay.Visible = true; RenderOverlay(); }
            blade.Visible = true; overlay.BringToFront(); blade.BringToFront();
        }

        void CloseBlade()
        {
            if (busy) { SetStatus("Đang chạy, chờ xong rồi đóng."); return; }
            blade.Visible = false; overlay.Visible = false;
            if (overlay.Image != null) { var i = overlay.Image; overlay.Image = null; i.Dispose(); }
            list.Focus();
        }

        int Y(int y) { return y + bladeBody.AutoScrollPosition.Y; }

        TextBox BField(string label, string value, string name, string hint = null, bool secret = false, bool multiline = false, bool readOnly = false)
        {
            bladeBody.Controls.Add(new Label { Text = label, Location = new Point(24, Y(by)), AutoSize = true, Font = new Font("Segoe UI Semibold", 9.5f) });
            by += 22;
            var t = new TextBox { Text = value, Location = new Point(24, Y(by)), Width = 410, UseSystemPasswordChar = secret, Multiline = multiline, ReadOnly = readOnly,
                                  Height = multiline ? 72 : 26, ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None, Name = name, AccessibleName = name };
            if (readOnly) t.BackColor = ColorTranslator.FromHtml("#f3f2f1");
            bladeBody.Controls.Add(t); by += t.Height + 4;
            if (hint != null) BHint(hint);
            by += 8;
            return t;
        }

        void BHint(string text)
        {
            var l = new Label { Text = text, Location = new Point(24, Y(by)), Width = 410, ForeColor = Muted, Font = new Font("Segoe UI", 8.5f) };
            l.Height = TextRenderer.MeasureText(text, l.Font, new Size(410, 0), TextFormatFlags.WordBreak).Height + 2;
            bladeBody.Controls.Add(l); by += l.Height;
        }

        CheckBox BCheck(string text, bool value, string name)
        {
            var c = new CheckBox { Text = text, Checked = value, Location = new Point(24, Y(by)), Width = 410, Name = name, AccessibleName = name, Font = new Font("Segoe UI", 9.5f) };
            c.Height = TextRenderer.MeasureText(text, c.Font, new Size(385, 0), TextFormatFlags.WordBreak).Height + 8;
            bladeBody.Controls.Add(c); by += c.Height + 6;
            return c;
        }

        void BNote(string text, bool warn)
        {
            var p = new Panel { Location = new Point(24, Y(by)), Width = 410, BackColor = warn ? ColorTranslator.FromHtml("#fff4ce") : ColorTranslator.FromHtml("#eff6fc") };
            var l = new Label { Text = text, Location = new Point(10, 8), Width = 390, Font = new Font("Segoe UI", 9), ForeColor = ColorTranslator.FromHtml("#323130") };
            l.Height = TextRenderer.MeasureText(text, l.Font, new Size(390, 0), TextFormatFlags.WordBreak).Height + 2;
            p.Height = l.Height + 16; p.Controls.Add(l);
            bladeBody.Controls.Add(p); by += p.Height + 12;
        }

        void EnsureLog()
        {
            if (bladeLog != null) return;
            bladeLog = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Location = new Point(24, Y(by)), Size = new Size(410, 220),
                                     Font = new Font("Consolas", 8.5f), BackColor = Soft, Name = "bladeLog", AccessibleName = "bladeLog" };
            bladeBody.Controls.Add(bladeLog); by += 228;
        }

        void BladeLog(string s)
        {
            UI(delegate { EnsureLog(); bladeLog.AppendText(s + "\r\n"); bladeBody.ScrollControlIntoView(bladeLog); });
        }

        void RunJob(Action job, Action<bool> done)
        {
            busy = true; bladePrimary.Enabled = false; bladeCancel.Enabled = false; bladeBusy.Visible = true; UpdateSelection();
            EnsureLog(); bladeLog.Clear(); bladeBody.ScrollControlIntoView(bladeLog);
            var th = new Thread(delegate ()
            {
                bool ok = false;
                try { job(); ok = true; }
                catch (Exception ex) { BladeLog(""); BladeLog("LỖI: " + ex.Message); }
                UI(delegate
                {
                    busy = false; bladePrimary.Enabled = true; bladeCancel.Enabled = true; bladeBusy.Visible = false; UpdateSelection();
                    if (done != null) done(ok);
                });
            });
            th.IsBackground = true; th.Start();
        }

        // ---------------------------------------------------------------- validation
        static string ValidateMachine(Machine m)
        {
            if (!Regex.IsMatch(m.Host, @"^[A-Za-z0-9][A-Za-z0-9.\-:]*$")) return "IP / tên máy không hợp lệ.";
            if (!Regex.IsMatch(m.User, @"^[A-Za-z0-9_][A-Za-z0-9._\-]*$")) return "User không hợp lệ.";
            if (m.Port < 1 || m.Port > 65535) return "Cổng không hợp lệ.";
            if (Regex.IsMatch(m.Dir, "['\"`$%\\\\]")) return "Thư mục không được chứa ' \" ` $ % \\.";
            if (m.Name == "" || Regex.IsMatch(m.Name, "[\"%]")) return "Tên máy không được trống hoặc chứa \" %.";
            if (!File.Exists(Ops.Ssh) || !File.Exists(Ops.Keygen)) return "Máy chưa có OpenSSH Client.";
            return null;
        }

        static string ValidateAgent(Machine m, string intIp)
        {
            if (m.User != "root") return "Cài Claude chạy root cần user root.";
            if (intIp != "" && !Regex.IsMatch(intIp, @"^\d{1,3}(\.\d{1,3}){3}(/\d{1,2})?$")) return "IP mạng nội bộ dạng 10.10.10.201/24.";
            return null;
        }

        Machine Duplicate(Machine m)
        {
            foreach (var x in machines) if (x != m && x.Host.Equals(m.Host, StringComparison.OrdinalIgnoreCase) && x.Port == m.Port && x.User == m.User) return x;
            return null;
        }

        static int ParsePort(string s) { int p; return int.TryParse(s.Trim(), out p) ? p : -1; }

        const string AgentWarning = "Script sẽ: TẮT mọi cập nhật tự động (kể cả bản vá bảo mật), BẬT đăng nhập root bằng mật khẩu qua SSH, nới phân vùng gốc ra hết đĩa, "
                                  + "(nếu điền) đặt IP tĩnh cho NIC thứ hai, cài Claude Code chạy root không hỏi quyền — agent có toàn quyền máy. "
                                  + "Chỉ dùng cho máy ảo dựng lại được, không mở ra Internet.";

        // ---------------------------------------------------------------- actions
        void DoConnect(bool claude)
        {
            var m = Sel(); if (m == null) return;
            try { Ops.Connect(m, claude); SetStatus((claude ? "Đã mở Claude trên " : "Đã mở SSH vào ") + m.Name); }
            catch (Exception ex) { Toast("Không mở được cửa sổ: " + ex.Message, true); return; }
            if (claude)
            {
                var th = new Thread(delegate ()
                {
                    try { if (!Ops.ClaudeLoggedIn(m)) Toast(m.Name + ": Claude chưa đăng nhập — đăng nhập một lần trong cửa sổ vừa mở (làm theo đường link nó in ra).", true); } catch { }
                });
                th.IsBackground = true; th.Start();
            }
        }

        void OpenAdd()
        {
            OpenBlade("Thêm máy", "Thêm máy", false);
            var tHost = BField("Địa chỉ IP / máy *", "", "host");
            var tPort = BField("Cổng SSH", "22", "port");
            var tUser = BField("User *", "root", "user");
            var tPass = BField("Mật khẩu *", "", "password", "Chỉ dùng một lần để đặt khoá riêng cho máy; không lưu ở đâu.", true);
            var tName = BField("Tên máy", "", "name", "Bỏ trống = user@ip.");
            var tDir = BField("Thư mục mở sẵn", "", "dir", "Vd /home/du-an. Bỏ trống = thư mục nhà.");
            var tNote = BField("Ghi chú", "", "note", null, false, true);
            var cAgent = BCheck("Cài luôn Claude Code chạy root (máy ảo Ubuntu agent)", false, "agent");
            var tInt = BField("IP mạng nội bộ (tuỳ chọn)", "", "internalip", "Vd 10.10.10.201/24 cho NIC thứ hai. Bỏ trống = không đụng mạng.");
            BNote(AgentWarning, true);
            var cAck = BCheck("Tôi hiểu — máy này là máy ảo dựng lại được", false, "ack");
            primaryAction = delegate
            {
                var m = new Machine
                {
                    Id = Guid.NewGuid().ToString("N").Substring(0, 10), Host = tHost.Text.Trim(), User = tUser.Text.Trim(), Port = ParsePort(tPort.Text),
                    Dir = tDir.Text.Trim(), Note = tNote.Text.Trim(), Created = DateTime.Now.ToString("dd/MM/yyyy HH:mm")
                };
                m.Name = tName.Text.Trim() != "" ? tName.Text.Trim() : m.User + "@" + m.Host;
                string pass = tPass.Text, intIp = tInt.Text.Trim();
                bool agent = cAgent.Checked;
                string err = m.Host == "" || m.User == "" || pass == "" ? "Cần IP, user và mật khẩu." : ValidateMachine(m);
                if (err == null && agent) err = ValidateAgent(m, intIp);
                if (err == null && agent && !cAck.Checked) err = "Tích ô \"Tôi hiểu\" trước khi cài Claude chạy root.";
                var dup = err == null ? Duplicate(m) : null;
                if (dup != null) err = "Máy này đã có trong danh sách: " + dup.Name + ".";
                if (err != null) { BladeLog(err); return; }
                RunJob(delegate
                {
                    try
                    {
                        BladeLog("1. Tạo khoá ed25519 riêng cho máy"); Ops.CreateKey(m);
                        BladeLog("2. Đăng nhập " + m.User + "@" + m.Host + " bằng mật khẩu, thêm khoá"); Ops.InstallKey(m, pass);
                        BladeLog("   Khoá máy: " + Ops.HostFingerprint(m));
                        BladeLog("3. Thử đăng nhập bằng khoá"); Ops.TestKey(m);
                    }
                    catch { try { Directory.Delete(Ops.KeyDir(m), true); } catch { } throw; }
                    m.State = (int)St.Online; m.Status = "Online";
                    UI(delegate { machines.Add(m); SaveAll(); RefreshList(); SelectMachine(m); });
                    BladeLog("   Đã thêm '" + m.Name + "'.");
                    if (agent) AgentSteps(m, intIp);
                    try { Ops.Check(m); } catch { }
                    Commit();
                    BladeLog(""); BladeLog("XONG. Bấm Kết nối (Enter) hoặc Mở Claude (Ctrl+Enter).");
                }, delegate (bool ok) { tPass.Clear(); if (ok) Toast("Đã thêm máy " + m.Name, false); });
            };
            tHost.Focus();
        }

        void SelectMachine(Machine m)
        {
            foreach (ListViewItem it in list.Items) if (it.Tag == m) { it.Selected = true; it.Focused = true; it.EnsureVisible(); }
        }

        void AgentSteps(Machine m, string intIp)
        {
            BladeLog(""); BladeLog("4. Cài máy agent (setup-ubuntu-agent-vm.sh, vài phút)…");
            bool passed = Ops.RunAgentSetup(m, intIp, delegate (string line) { BladeLog("   " + line); });
            BladeLog(passed ? "   Script: ALL CHECKS PASSED" : "   Script báo có mục FAIL (xem trên) — vẫn thử lệnh Claude.");
            BladeLog("5. Thử: claude --dangerously-skip-permissions --allowedTools \"" + Ops.AgentTools + "\"");
            string msg;
            bool ok = Ops.TestClaude(m, out msg);
            BladeLog("   " + (ok ? "ĐẠT — " : "CHƯA — ") + msg);
            m.Agent = ok;
            m.State = (int)St.Online; m.Status = ok ? "Online · Claude chạy root OK" : "Online · " + msg;
            Commit();
        }

        void OpenAgent(Machine m)
        {
            OpenBlade("Cài Claude chạy root", "Cài đặt", false);
            BField("Máy", m.Name + "  (" + m.Target + ")", "target", null, false, false, true);
            var tInt = BField("IP mạng nội bộ (tuỳ chọn)", "", "internalip", "Vd 10.10.10.201/24 cho NIC thứ hai. Bỏ trống = không đụng mạng.");
            BNote(AgentWarning, true);
            BNote("Chạy bản script ghim trong ứng dụng (" + Ops.ScriptPin + "). Chạy lại nhiều lần không sao: bước đã xong thì bỏ qua. "
                + "Xong sẽ chạy thử đúng lệnh claude --dangerously-skip-permissions --allowedTools \"" + Ops.AgentTools + "\".", false);
            var cAck = BCheck("Tôi hiểu — máy này là máy ảo dựng lại được", false, "ack");
            primaryAction = delegate
            {
                string intIp = tInt.Text.Trim();
                string err = ValidateAgent(m, intIp);
                if (err == null && !cAck.Checked) err = "Tích ô \"Tôi hiểu\" trước.";
                if (err != null) { BladeLog(err); return; }
                RunJob(delegate { Ops.TestKey(m); AgentSteps(m, intIp); try { Ops.Check(m); } catch { } Commit(); BladeLog(""); BladeLog("XONG."); },
                       delegate (bool ok) { if (ok) Toast(m.Name + ": " + (m.Agent ? "Claude chạy root OK" : m.Status), !m.Agent); });
            };
        }

        void OpenUpdate(Machine m)
        {
            OpenBlade("Cập nhật Claude Code", "Cập nhật", false);
            BField("Máy", m.Name + "  (" + m.Target + ")", "target", null, false, false, true);
            BNote("Cài lại bản mới nhất bằng trình cài chính thức (claude.ai/install.sh). Máy agent tắt tự cập nhật, nên cập nhật bằng nút này. Phiên Claude đang chạy trên máy nên thoát trước.", false);
            primaryAction = delegate
            {
                RunJob(delegate
                {
                    BladeLog("Phiên bản hiện tại: " + (m.ClaudeVer == "" ? "chưa rõ" : m.ClaudeVer));
                    string v = Ops.UpdateClaude(m, delegate (string l) { BladeLog("   " + l); });
                    if (v == "") throw new Exception("Không cập nhật được (xem nhật ký).");
                    m.ClaudeVer = v; Commit();
                    BladeLog(""); BladeLog("XONG — Claude Code " + v);
                }, delegate (bool ok) { if (ok) Toast(m.Name + ": Claude Code " + m.ClaudeVer, false); });
            };
        }

        void OpenRekey(Machine m)
        {
            OpenBlade("Cài lại khoá", "Cài lại khoá", false);
            BField("Máy", m.Name + "  (" + m.Target + ")", "target", null, false, false, true);
            BField("Khoá máy đang nhớ", Ops.HostFingerprint(m), "fp", null, false, false, true);
            var cReset = BCheck("Máy đã dựng lại / cài lại (quên khoá máy cũ, nhận khoá mới)", m.Status.Contains("Khoá máy đã đổi"), "reset");
            var cNew = BCheck("Tạo khoá đăng nhập mới cho máy này (khoá cũ bị lộ / mất)", !File.Exists(Ops.Key(m)), "newkey");
            var tPass = BField("Mật khẩu " + m.User + " *", "", "password", "Dùng một lần để đặt lại khoá; không lưu.", true);
            BNote("Chỉ tích \"Máy đã dựng lại\" khi anh chắc máy vừa cài lại. Nếu không, khoá máy đổi có thể là dấu hiệu bị chặn giữa đường (MITM).", true);
            primaryAction = delegate
            {
                string pass = tPass.Text;
                if (pass == "") { BladeLog("Nhập mật khẩu."); return; }
                bool reset = cReset.Checked, newKey = cNew.Checked;
                RunJob(delegate
                {
                    if (reset) { BladeLog("Quên khoá máy cũ (" + Ops.HostFingerprint(m) + ")"); Ops.ForgetHostKey(m); }
                    if (newKey || !File.Exists(Ops.Key(m))) { BladeLog("Tạo khoá đăng nhập mới"); Ops.CreateKey(m); m.Shared = false; }
                    else { Ops.FixAcl(Ops.Key(m)); if (m.KeyText == "") Ops.ReadKeyText(m); }
                    BladeLog("Đăng nhập bằng mật khẩu, thêm khoá"); Ops.InstallKey(m, pass);
                    BladeLog("Khoá máy mới: " + Ops.HostFingerprint(m));
                    BladeLog("Thử đăng nhập bằng khoá"); Ops.TestKey(m);
                    Ops.Check(m); Commit();
                    BladeLog(""); BladeLog("XONG — " + m.Status);
                }, delegate (bool ok) { tPass.Clear(); if (ok) Toast(m.Name + ": đã cài lại khoá", false); });
            };
        }

        void OpenEdit(Machine m)
        {
            OpenBlade("Sửa máy", "Lưu", false);
            var tName = BField("Tên máy", m.Name, "name");
            var tHost = BField("Địa chỉ IP / máy", m.Host, "host", "Đổi IP: lưu xong ứng dụng kiểm lại. Máy khác hẳn → dùng \"Cài lại khoá\".");
            var tPort = BField("Cổng SSH", m.Port.ToString(), "port");
            BField("User", m.User, "user", "Đổi user = thêm máy mới (khoá nằm trong ~/.ssh của từng user).", false, false, true);
            var tDir = BField("Thư mục mở sẵn", m.Dir, "dir");
            var tNote = BField("Ghi chú", m.Note, "note", null, false, true);
            primaryAction = delegate
            {
                var probe = new Machine { Name = tName.Text.Trim(), Host = tHost.Text.Trim(), Port = ParsePort(tPort.Text), User = m.User, Dir = tDir.Text.Trim() };
                string err = ValidateMachine(probe);
                if (err == null) { probe.Id = m.Id; var d = Duplicate(probe); if (d != null && d != m) err = "Trùng với máy " + d.Name + "."; }
                if (err != null) { BladeLog(err); return; }
                bool moved = probe.Host != m.Host || probe.Port != m.Port;
                m.Name = probe.Name; m.Host = probe.Host; m.Port = probe.Port; m.Dir = probe.Dir; m.Note = tNote.Text.Trim();
                if (moved) { m.State = (int)St.Unknown; m.Status = ""; }
                SaveAll(); RefreshList(); CloseBlade(); Toast("Đã lưu " + m.Name, false);
                if (moved) CheckOne(m);
            };
            tName.Focus();
        }

        void OpenDelete(Machine m)
        {
            OpenBlade("Xoá máy", "Xoá", true);
            BField("Máy", m.Name + "  (" + m.Target + ")", "target", null, false, false, true);
            BNote("Xoá '" + m.Name + "' khỏi danh sách. Khoá trên máy này được chuyển vào data\\trash (lấy lại được), không xoá hẳn.", true);
            var cRemote = BCheck("Gỡ luôn khoá của ứng dụng khỏi ~/.ssh/authorized_keys trên máy (sao lưu trước khi sửa)", !m.Shared, "remote");
            if (m.Shared) BHint("Khoá này nhập từ kết nối có sẵn — có thể còn nơi khác dùng, nên mặc định không gỡ.");
            primaryAction = delegate
            {
                bool remote = cRemote.Checked;
                RunJob(delegate
                {
                    if (remote)
                    {
                        BladeLog("Gỡ khoá trên máy…");
                        BladeLog("   " + (File.Exists(Ops.Key(m)) ? Ops.RemoveRemoteKey(m) : "không có khoá"));
                    }
                    if (Directory.Exists(Ops.KeyDir(m)))
                    {
                        string trash = Path.Combine(Ops.DataDir, "trash", m.Id + "-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
                        Directory.CreateDirectory(Path.GetDirectoryName(trash));
                        Directory.Move(Ops.KeyDir(m), trash);
                        BladeLog("Khoá chuyển vào " + trash);
                    }
                    UI(delegate { machines.Remove(m); SaveAll(); RefreshList(); });
                    BladeLog(""); BladeLog("ĐÃ XOÁ " + m.Name);
                }, delegate (bool ok) { if (ok) { bladePrimary.Visible = false; Toast("Đã xoá " + m.Name, false); } });
            };
        }

        void OpenImport()
        {
            OpenBlade("Nhập kết nối có sẵn", "Nhập", false);
            BNote("Chọn thư mục có khoá riêng id_ed25519 (và known_hosts nếu có), vd một thư mục kết nối .cmd cũ. Ứng dụng CHÉP khoá vào data\\keys, không đụng bản gốc.", false);
            var tDirLocal = BField("Thư mục khoá trên máy này *", "", "keyfolder");
            var pick = new LinkLabel { Text = "Chọn thư mục…", Location = new Point(24, Y(by - 8)), AutoSize = true, LinkColor = AzureText };
            bladeBody.Controls.Add(pick); by += 18;
            var tHost = BField("Địa chỉ IP / máy *", "", "host");
            var tPort = BField("Cổng SSH", "22", "port");
            var tUser = BField("User *", "root", "user");
            var tName = BField("Tên máy", "", "name", "Bỏ trống = user@ip.");
            var tDir = BField("Thư mục mở sẵn", "", "dir");
            var tNote = BField("Ghi chú", "", "note", null, false, true);
            Action<string> fill = delegate (string folder)
            {
                foreach (var cmd in Directory.GetFiles(folder, "*.cmd"))
                {
                    string t = File.ReadAllText(cmd);
                    var a = Regex.Match(t, @"\s([A-Za-z0-9_][A-Za-z0-9._\-]*)@([A-Za-z0-9][A-Za-z0-9.\-]*)\s");
                    if (a.Success) { tUser.Text = a.Groups[1].Value; tHost.Text = a.Groups[2].Value; }
                    var p = Regex.Match(t, @"\s-p\s+(\d+)"); if (p.Success) tPort.Text = p.Groups[1].Value;
                    var d = Regex.Match(t, @"cd '([^']+)'"); if (d.Success) tDir.Text = d.Groups[1].Value;
                    if (tName.Text == "") tName.Text = Path.GetFileNameWithoutExtension(cmd);
                    break;
                }
            };
            pick.Click += delegate
            {
                using (var fb = new FolderBrowserDialog { Description = "Thư mục chứa id_ed25519" })
                    if (fb.ShowDialog(this) == DialogResult.OK) { tDirLocal.Text = fb.SelectedPath; try { fill(fb.SelectedPath); } catch { } }
            };
            tDirLocal.Leave += delegate
            {
                string f = tDirLocal.Text.Trim();
                if (f != "" && Directory.Exists(f) && tHost.Text == "") { try { fill(f); } catch { } }
            };
            primaryAction = delegate
            {
                string src = tDirLocal.Text.Trim();
                if (Directory.Exists(src) && !File.Exists(Path.Combine(src, "id_ed25519")) && Directory.Exists(Path.Combine(src, "data"))) src = Path.Combine(src, "data");
                var m = new Machine
                {
                    Id = Guid.NewGuid().ToString("N").Substring(0, 10), Host = tHost.Text.Trim(), User = tUser.Text.Trim(), Port = ParsePort(tPort.Text),
                    Dir = tDir.Text.Trim(), Note = tNote.Text.Trim(), Created = DateTime.Now.ToString("dd/MM/yyyy HH:mm"), Shared = true
                };
                m.Name = tName.Text.Trim() != "" ? tName.Text.Trim() : m.User + "@" + m.Host;
                string err = !File.Exists(Path.Combine(src, "id_ed25519")) ? "Không thấy id_ed25519 trong thư mục đó." : m.Host == "" ? "Cần IP." : ValidateMachine(m);
                var dup = err == null ? Duplicate(m) : null;
                if (dup != null) err = "Máy này đã có trong danh sách: " + dup.Name + ".";
                if (err != null) { BladeLog(err); return; }
                RunJob(delegate
                {
                    try
                    {
                        Directory.CreateDirectory(Ops.KeyDir(m));
                        BladeLog("Chép khoá từ " + src);
                        File.Copy(Path.Combine(src, "id_ed25519"), Ops.Key(m));
                        Ops.FixAcl(Ops.Key(m));
                        if (File.Exists(Path.Combine(src, "id_ed25519.pub"))) File.Copy(Path.Combine(src, "id_ed25519.pub"), Ops.Key(m) + ".pub");
                        else File.WriteAllText(Ops.Key(m) + ".pub", Ops.Run(Ops.Keygen, "-y -f " + Ops.Q(Ops.Key(m)), null, null, 30000, null).Output.Trim() + "\n", Encoding.ASCII);
                        Ops.ReadKeyText(m);
                        if (File.Exists(Path.Combine(src, "known_hosts"))) File.Copy(Path.Combine(src, "known_hosts"), Ops.KnownHosts(m));
                        else throw new Exception("Thiếu known_hosts: không xác minh được máy. Dùng \"Thêm máy\" (có mật khẩu) thay vì nhập.");
                        BladeLog("Khoá máy: " + Ops.HostFingerprint(m));
                        BladeLog("Thử đăng nhập bằng khoá"); Ops.TestKey(m);
                    }
                    catch { try { Directory.Delete(Ops.KeyDir(m), true); } catch { } throw; }
                    Ops.Check(m);
                    UI(delegate { machines.Add(m); SaveAll(); RefreshList(); SelectMachine(m); });
                    BladeLog(""); BladeLog("XONG — đã nhập " + m.Name + " (" + m.Status + "). Bản gốc giữ nguyên.");
                }, delegate (bool ok) { if (ok) Toast("Đã nhập " + m.Name, false); });
            };
        }

        void OpenAbout()
        {
            OpenBlade("Giới thiệu", null, false);
            BNote("Hoanng Fleet " + Version + " — Hoanng Labs\r\nQuản lý đội máy Linux trong LAN: SSH một chạm bằng khoá riêng từng máy, mở Claude Code chạy root, cài máy ảo Ubuntu làm máy agent.", false);
            BField("Dữ liệu", Ops.DataDir, "datadir", "machines.json (không có mật khẩu) + keys\\<máy>\\ (khoá riêng chỉ tài khoản Windows này đọc được). Sao lưu ở menu ⋯.", false, false, true);
            BField("Script máy agent", Ops.ScriptPin, "pin", null, false, false, true);
            BField("Lệnh Claude", "claude --dangerously-skip-permissions --allowedTools \"" + Ops.AgentTools + "\"", "cmd", null, false, false, true);
            BField("Phím tắt", "Ctrl+N thêm · Enter kết nối · Ctrl+Enter Claude · F2 sửa · Del xoá · F5 kiểm tra · Ctrl+F tìm · Esc đóng", "keys", null, false, true, true);
        }

        // ---------------------------------------------------------------- files / sync
        void OpenFiles()
        {
            var m = Sel(); if (m == null) return;
            new FileForm(m, this).Show();
        }

        public void Persist() { SaveAll(); RefreshList(); }

        public void OpenSync(Machine m, string remoteDefault, string localDefault = null)
        {
            Activate();
            OpenBlade("Đồng bộ sao lưu", "Lưu & đồng bộ ngay", false);
            BField("Máy", m.Name + "  (" + m.Target + ")", "target", null, false, false, true);
            var tRemote = BField("Thư mục trên máy (nguồn) *", m.SyncRemote != "" ? m.SyncRemote : (remoteDefault ?? (m.Dir != "" ? m.Dir : "~")), "syncremote",
                                 "Vd /home/du-an. Chỉ tải tệp mới / đã đổi (so kích thước + giờ sửa).");
            string defLocal = m.SyncLocal != "" ? m.SyncLocal : localDefault != null ? Path.Combine(localDefault, Regex.Replace(((remoteDefault ?? "").TrimEnd('/') == "" ? m.Name : (remoteDefault ?? "").TrimEnd('/').Substring((remoteDefault ?? "").TrimEnd('/').LastIndexOf('/') + 1)), "[\\\\/:*?\"<>|~]", "_"))
                            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HoanngFleet-backup",
                                                                             Regex.Replace(m.Name, "[\\\\/:*?\"<>|]", "_"));
            var tLocal = BField("Thư mục trên máy tính (đích) *", defLocal, "synclocal");
            var pick = new LinkLabel { Text = "Chọn thư mục…", Location = new Point(24, Y(by - 8)), AutoSize = true, LinkColor = AzureText };
            pick.Click += delegate
            {
                using (var fb = new FolderBrowserDialog { Description = "Thư mục sao lưu trên máy tính", SelectedPath = tLocal.Text })
                    if (fb.ShowDialog(this) == DialogResult.OK) tLocal.Text = fb.SelectedPath;
            };
            bladeBody.Controls.Add(pick); by += 18;
            var tEx = BField("Bỏ qua (tên thư mục / tệp, cách nhau dấu phẩy)", m.SyncExclude, "syncexclude", "Áp ở mọi cấp, có thể dùng *. Vd node_modules,.cache,*.log");
            var cMirror = BCheck("Gương: tệp đã xoá trên máy thì chuyển vào .hf-trash ở máy tính (không xoá hẳn)", m.SyncMirror, "syncmirror");
            var tEvery = BField("Tự sao lưu mỗi (phút, 0 = tắt)", m.SyncEvery.ToString(), "syncevery",
                                "Dùng Windows Task Scheduler — chạy cả khi ứng dụng đóng, khi anh đang đăng nhập Windows. Nhật ký: data\\sync\\<máy>.log");
            string last = Ops.LastSync(m);
            if (last != "") BNote("Lần gần nhất: " + last, last.Contains("LỖI"));
            primaryAction = delegate
            {
                string remote = tRemote.Text.Trim(), local = tLocal.Text.Trim();
                int every; int.TryParse(tEvery.Text.Trim(), out every);
                string err = remote == "" || local == "" ? "Cần thư mục nguồn và đích." : !Path.IsPathRooted(local) ? "Thư mục đích phải là đường dẫn đầy đủ (vd D:\\Backup\\du-an)." :
                             every != 0 && (every < 5 || every > 1440) ? "Chu kỳ 5–1440 phút (hoặc 0 = tắt)." : null;
                if (err != null) { BladeLog(err); return; }
                m.SyncRemote = remote; m.SyncLocal = local; m.SyncExclude = tEx.Text.Trim(); m.SyncMirror = cMirror.Checked; m.SyncEvery = every;
                SaveAll();
                RunJob(delegate
                {
                    BladeLog(Ops.Schedule(m));
                    using (var mx = new Mutex(false, "HoanngFleetSync-" + m.Id))
                    {
                        if (!mx.WaitOne(0)) throw new Exception("Một lượt đồng bộ khác của máy này đang chạy (Task Scheduler) — thử lại sau.");
                        try { Ops.Sync(m, delegate (string s) { BladeLog(s); }); } finally { mx.ReleaseMutex(); }
                    }
                }, delegate (bool ok) { UpdateSelection(); if (ok) Toast(m.Name + ": đã đồng bộ về " + m.SyncLocal, false); });
            };
        }

        // ---------------------------------------------------------------- check / backup
        void CheckOne(Machine m)
        {
            m.State = (int)St.Checking; m.Status = "Đang kiểm…"; RefreshList();
            var th = new Thread(delegate ()
            {
                try { Ops.Check(m); } catch (Exception ex) { m.State = (int)St.Error; m.Status = ex.Message; }
                UI(delegate { SaveAll(); RefreshList(); SetStatus("Kiểm " + m.Name + " lúc " + DateTime.Now.ToString("HH:mm:ss") + ": " + m.Status); });
            });
            th.IsBackground = true; th.Start();
        }

        void CheckAll()
        {
            if (machines.Count == 0) { SetStatus("Chưa có máy nào."); return; }
            SetStatus("Đang kiểm " + machines.Count + " máy…");
            foreach (var m in machines.ToArray()) CheckOne(m);
        }

        void DoBackup()
        {
            using (var d = new SaveFileDialog { Filter = "Bản sao lưu Hoanng Fleet (*.zip)|*.zip", FileName = "hoanng-fleet-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".zip" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string tmp = Path.Combine(Path.GetTempPath(), "hf-bk-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tmp);
                    SaveAll();
                    File.Copy(Path.Combine(Ops.DataDir, "machines.json"), Path.Combine(tmp, "machines.json"));
                    foreach (var m in machines)
                        if (Directory.Exists(Ops.KeyDir(m)))
                        {
                            string dst = Path.Combine(tmp, "keys", m.Id); Directory.CreateDirectory(dst);
                            foreach (var f in Directory.GetFiles(Ops.KeyDir(m))) if (Path.GetFileName(f) != "config") File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
                        }
                    if (File.Exists(d.FileName)) File.Delete(d.FileName);
                    ZipFile.CreateFromDirectory(tmp, d.FileName);
                    Directory.Delete(tmp, true);
                    Toast("Đã sao lưu " + machines.Count + " máy vào " + d.FileName + " — tệp chứa KHOÁ RIÊNG, cất nơi an toàn.", false);
                }
                catch (Exception ex) { Toast("Sao lưu lỗi: " + ex.Message, true); }
            }
        }

        void DoRestore()
        {
            using (var d = new OpenFileDialog { Filter = "Bản sao lưu Hoanng Fleet (*.zip)|*.zip" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string tmp = Path.Combine(Path.GetTempPath(), "hf-rs-" + Guid.NewGuid().ToString("N"));
                    ZipFile.ExtractToDirectory(d.FileName, tmp);
                    List<Machine> incoming;
                    using (var s = File.OpenRead(Path.Combine(tmp, "machines.json")))
                        incoming = (List<Machine>)new DataContractJsonSerializer(typeof(List<Machine>)).ReadObject(s);
                    int added = 0, skipped = 0;
                    foreach (var m in incoming)
                    {
                        bool exists = false;
                        foreach (var x in machines) if (x.Id == m.Id || (x.Host == m.Host && x.Port == m.Port && x.User == m.User)) exists = true;
                        string src = Path.Combine(tmp, "keys", m.Id);
                        if (exists || !Regex.IsMatch(m.Id, "^[A-Za-z0-9]+$") || !Directory.Exists(src)) { skipped++; continue; }
                        Directory.CreateDirectory(Ops.KeyDir(m));
                        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(Ops.KeyDir(m), Path.GetFileName(f)), true);
                        if (File.Exists(Ops.Key(m))) Ops.FixAcl(Ops.Key(m));
                        m.State = (int)St.Unknown; m.Status = "";
                        machines.Add(m); added++;
                    }
                    Directory.Delete(tmp, true);
                    loadFailed = false; SaveAll(); RefreshList();
                    Toast("Khôi phục: thêm " + added + " máy, bỏ qua " + skipped + " (đã có / thiếu khoá). Bấm Kiểm tra (F5).", false);
                }
                catch (Exception ex) { Toast("Khôi phục lỗi: " + ex.Message, true); }
            }
        }
    }

    class Entry { public string Name; public string FullPath; public bool Dir; public bool Link; public long Size; public DateTime Mtime; public string Mode; }

    // One side of the file manager (FileZilla-style): header, path box, folder tree on top, file list below.
    // Local = this Windows PC, Remote = the SSH machine. Folder loading runs in the background.
    class Pane : Panel
    {
        public readonly bool Remote;
        readonly Machine m;
        public string Cwd = "";
        public readonly TextBox PathBox;
        public readonly TreeView Tree;
        public readonly ListView List;
        readonly Label status;
        public readonly SplitContainer Split;
        readonly List<TreeNode> starts = new List<TreeNode>();
        int seq;
        bool treePicked;                                      // last pick was a folder in the tree (not items in the list)
        public Action<Pane, List<Entry>> TransferRequested;  // (source pane, or null = dropped from Windows Explorer; items)
        public const string DragFormat = "HoanngFleet.Pane";

        public Pane(bool remote, Machine machine, ImageList il, string title)
        {
            Remote = remote; m = machine; Dock = DockStyle.Fill; BackColor = Color.White;
            var head = new Label { Dock = DockStyle.Top, Height = 30, Text = title, Font = new Font("Segoe UI Semibold", 10), Padding = new Padding(10, 6, 0, 0),
                                   BackColor = ColorTranslator.FromHtml(remote ? "#eef4ff" : "#f3f2f1") };
            var pathPanel = new Panel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(8, 5, 8, 5), BackColor = Color.White };
            PathBox = new TextBox { Dock = DockStyle.Fill, Font = new Font("Consolas", 10), Name = remote ? "rpath" : "lpath" };
            PathBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Go(PathBox.Text.Trim()); } };
            pathPanel.Controls.Add(PathBox);

            Tree = new TreeView { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, HideSelection = false, ImageList = il, ImageKey = "d", SelectedImageKey = "d",
                                  ItemHeight = 22, Font = new Font("Segoe UI", 9.5f), Name = remote ? "rtree" : "ltree" };
            Tree.BeforeExpand += (s, e) => LoadChildren(e.Node, null);
            Tree.AfterSelect += (s, e) =>
            {
                if (e.Action == TreeViewAction.Unknown || !(e.Node.Tag is string) || (string)e.Node.Tag == "") return;
                treePicked = true; Go((string)e.Node.Tag);
            };
            Tree.NodeMouseClick += (s, e) => { if (e.Button == MouseButtons.Right) Tree.SelectedNode = e.Node; };

            List = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false, BorderStyle = BorderStyle.None,
                                  SmallImageList = il, AllowDrop = true, Font = new Font("Segoe UI", 9.5f), Name = remote ? "rlist" : "llist" };
            List.Columns.Add("Tên", 250); List.Columns.Add("Kích thước", 85, HorizontalAlignment.Right); List.Columns.Add("Sửa lúc", 125);
            if (remote) List.Columns.Add("Quyền", 55);
            List.DoubleClick += delegate { OpenItem(); };
            List.ItemSelectionChanged += (s, e) => { if (e.IsSelected) treePicked = false; };
            List.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.Handled = true; OpenItem(); }
                else if (e.KeyCode == Keys.Back) { e.Handled = true; Up(); }
                else if (e.KeyCode == Keys.F5) { e.Handled = true; Refresh_(); }
                else if (e.KeyCode == Keys.A && e.Control) { foreach (ListViewItem it in List.Items) if (it.Tag != null) it.Selected = true; }
            };
            List.ItemDrag += (s, e) => { if (Selected().Count > 0) List.DoDragDrop(new DataObject(DragFormat, this), DragDropEffects.Copy); };
            List.DragEnter += (s, e) => { e.Effect = AcceptDrop(e.Data) ? DragDropEffects.Copy : DragDropEffects.None; };
            List.DragDrop += (s, e) => OnDrop(e.Data);

            Split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 4, BackColor = ColorTranslator.FromHtml("#e1e4e8") };
            Split.Panel1.BackColor = Color.White; Split.Panel2.BackColor = Color.White;
            Split.Panel1.Controls.Add(Tree); Split.Panel2.Controls.Add(List);
            status = new Label { Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(10, 4, 0, 0), ForeColor = ColorTranslator.FromHtml("#605e5c"),
                                 Font = new Font("Segoe UI", 9), BackColor = ColorTranslator.FromHtml("#f6f8fa"), Name = remote ? "rstatus" : "lstatus" };
            Controls.Add(Split); Controls.Add(pathPanel); Controls.Add(head); Controls.Add(status);
        }

        void UI(Action a) { if (IsDisposed) return; try { if (!InvokeRequired) a(); else if (IsHandleCreated) BeginInvoke(a); } catch (InvalidOperationException) { } }

        StringComparison Cmp { get { return Remote ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase; } }
        char Sep { get { return Remote ? '/' : '\\'; } }

        public string Join(string dir, string name) { return Remote ? (dir == "/" ? "/" + name : dir.TrimEnd('/') + "/" + name) : Path.Combine(dir, name); }

        public string ParentOf(string p)
        {
            if (Remote) return p == "/" || p == "" ? null : p.Substring(0, Math.Max(1, p.TrimEnd('/').LastIndexOf('/')));
            string root = Path.GetPathRoot(p);
            if (string.Equals(root.TrimEnd('\\'), p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return null;
            return Path.GetDirectoryName(p.TrimEnd('\\'));
        }

        static string FmtSize(long n)
        {
            if (n < 1024) return n + " B";
            if (n < 1024 * 1024) return (n / 1024.0).ToString("0.0") + " KB";
            if (n < 1024L * 1024 * 1024) return (n / 1024.0 / 1024).ToString("0.0") + " MB";
            return (n / 1024.0 / 1024 / 1024).ToString("0.00") + " GB";
        }

        List<Entry> Load(string p, out string canon)
        {
            var l = new List<Entry>();
            if (Remote)
            {
                foreach (var it in Ops.ListDir(m, p == "" ? "~" : p, out canon))
                    l.Add(new Entry { Name = it.Name, Dir = it.Dir, Link = it.Link, Size = it.Size, Mtime = it.Mtime.ToLocalTime(), Mode = it.Mode });
            }
            else
            {
                canon = Path.GetFullPath(p.Length == 2 && p[1] == ':' ? p + "\\" : p);
                var di = new DirectoryInfo(canon);
                if (!di.Exists) throw new Exception("Không có thư mục " + canon);
                var hs = FileAttributes.Hidden | FileAttributes.System;
                foreach (var d in di.GetDirectories()) if ((d.Attributes & hs) != hs) l.Add(new Entry { Name = d.Name, Dir = true, Mtime = d.LastWriteTime });
                foreach (var f in di.GetFiles()) if ((f.Attributes & hs) != hs) l.Add(new Entry { Name = f.Name, Size = f.Length, Mtime = f.LastWriteTime });
            }
            foreach (var e in l) e.FullPath = Join(canon, e.Name);
            return l;
        }

        // ------------------------------------------------------------ navigation
        public void Init(string start)
        {
            try { Split.SplitterDistance = Math.Max(80, (int)(Split.Height * 0.42)); } catch { }
            if (Remote)
            {
                var th = new Thread(delegate ()
                {
                    string h = null;
                    try { string pwd; Ops.ListDir(m, "~", out pwd); h = pwd; } catch { }
                    UI(delegate
                    {
                        if (h != null && h != "/") AddStart("~  " + h, h);
                        AddStart("/", "/");
                        Go(start);
                    });
                });
                th.IsBackground = true; th.Start();
            }
            else
            {
                string up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                AddStart("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
                AddStart("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
                if (Directory.Exists(Path.Combine(up, "Downloads"))) AddStart("Downloads", Path.Combine(up, "Downloads"));
                foreach (var d in DriveInfo.GetDrives())
                {
                    string label = "";
                    try { if (d.IsReady) label = d.VolumeLabel; else continue; } catch { continue; }
                    AddStart(d.Name.TrimEnd('\\') + (label != "" ? "  " + label : ""), d.Name);
                }
                Go(start);
            }
        }

        void AddStart(string text, string tag)
        {
            var n = new TreeNode(text) { Tag = tag }; n.Nodes.Add(Dummy());
            Tree.Nodes.Add(n); starts.Add(n);
        }

        static TreeNode Dummy() { return new TreeNode("Đang tải…") { ForeColor = Color.Gray, Tag = null, ImageKey = "f", SelectedImageKey = "f" }; }
        static bool IsLoaded(TreeNode n) { return !(n.Nodes.Count == 1 && n.Nodes[0].Tag == null); }

        void LoadChildren(TreeNode n, Action then)
        {
            if (IsLoaded(n)) { if (then != null) then(); return; }
            if (n.Nodes[0].Text == "Đang tải… ") return;
            n.Nodes[0].Text = "Đang tải… ";
            string p = (string)n.Tag;
            var th = new Thread(delegate ()
            {
                List<Entry> items = null; string err = null; string canon;
                try { items = Load(p, out canon); } catch (Exception ex) { err = ex.Message; }
                UI(delegate
                {
                    Tree.BeginUpdate(); n.Nodes.Clear();
                    if (err != null) n.Nodes.Add(new TreeNode("(không mở được)") { ForeColor = Color.Gray, Tag = "", ImageKey = "f", SelectedImageKey = "f" });
                    else
                    {
                        items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                        foreach (var it in items) if (it.Dir) { var c = new TreeNode(it.Name) { Tag = it.FullPath }; c.Nodes.Add(Dummy()); n.Nodes.Add(c); }
                    }
                    Tree.EndUpdate();
                    if (then != null) then();
                });
            });
            th.IsBackground = true; th.Start();
        }

        bool Under(string p, string root)
        {
            if (string.Equals(p.TrimEnd(Sep), root.TrimEnd(Sep), Cmp)) return true;
            string r = root.EndsWith(Sep.ToString()) ? root : root + Sep;
            return p.StartsWith(r, Cmp);
        }

        // Expands the tree down to p from the closest start node (longest matching prefix) and selects it.
        void Reveal(string p)
        {
            TreeNode best = null;
            foreach (var s in starts) if (Under(p, (string)s.Tag) && (best == null || ((string)s.Tag).Length > ((string)best.Tag).Length)) best = s;
            if (best == null) return;
            string rel = p.Length > ((string)best.Tag).Length ? p.Substring(((string)best.Tag).Length).Trim(Sep) : "";
            RevealStep(best, rel == "" ? new string[0] : rel.Split(Sep), 0, p);
        }

        void RevealStep(TreeNode n, string[] segs, int i, string target)
        {
            if (Cwd != target) return;
            if (i == segs.Length) { Tree.SelectedNode = n; n.EnsureVisible(); return; }
            LoadChildren(n, delegate
            {
                n.Expand();
                foreach (TreeNode c in n.Nodes) if (string.Equals(c.Text, segs[i], Cmp)) { RevealStep(c, segs, i + 1, target); return; }
                Tree.SelectedNode = n;
            });
        }

        public void Go(string p)
        {
            int my = ++seq;
            status.ForeColor = ColorTranslator.FromHtml("#605e5c"); status.Text = "Đang mở " + p + "…";
            var th = new Thread(delegate ()
            {
                List<Entry> items = null; string canon = null, err = null;
                try { items = Load(p, out canon); } catch (Exception ex) { err = ex.Message; }
                UI(delegate
                {
                    if (my != seq) return;
                    if (err != null) { status.ForeColor = ColorTranslator.FromHtml("#a4262c"); status.Text = "LỖI: " + err; PathBox.Text = Cwd; return; }
                    Cwd = canon; PathBox.Text = canon;
                    items.Sort((a, b) => a.Dir != b.Dir ? (a.Dir ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                    List.BeginUpdate(); List.Items.Clear();
                    if (ParentOf(Cwd) != null) List.Items.Add(new ListViewItem(new[] { "..", "", "", "" }) { ImageKey = "d", Tag = null });
                    long total = 0; int files = 0;
                    foreach (var it in items)
                    {
                        var cols = new List<string> { it.Name + (it.Link ? "  →" : ""), it.Dir ? "" : FmtSize(it.Size), it.Mtime.ToString("dd/MM/yyyy HH:mm") };
                        if (Remote) cols.Add(it.Mode);
                        List.Items.Add(new ListViewItem(cols.ToArray()) { ImageKey = it.Dir ? "d" : it.Link ? "l" : "f", Tag = it });
                        if (!it.Dir) { total += it.Size; files++; }
                    }
                    List.EndUpdate();
                    status.Text = items.Count + " mục · " + files + " tệp · " + FmtSize(total);
                    Reveal(canon);
                });
            });
            th.IsBackground = true; th.Start();
        }

        public void Refresh_() { if (Cwd != "") Go(Cwd); }
        public void Up() { var p = ParentOf(Cwd); if (p != null) Go(p); }

        void OpenItem()
        {
            if (List.SelectedItems.Count != 1) return;
            var e = List.SelectedItems[0].Tag as Entry;
            if (e == null) { Up(); return; }
            if (e.Dir) Go(e.FullPath);
            else if (TransferRequested != null) TransferRequested(this, new List<Entry> { e });   // double-click a file = send it to the other side
        }

        public void RefreshBranch()
        {
            var n = Tree.SelectedNode; if (n == null || !(n.Tag is string)) return;
            n.Nodes.Clear(); n.Nodes.Add(Dummy()); n.Collapse(); n.Expand();
        }

        // ------------------------------------------------------------ selection
        public List<Entry> Selected()
        {
            var l = new List<Entry>();
            foreach (ListViewItem it in List.SelectedItems) if (it.Tag is Entry) l.Add((Entry)it.Tag);
            return l;
        }

        public string TreeDir() { var n = Tree.SelectedNode; return n != null && n.Tag is string && (string)n.Tag != "" ? (string)n.Tag : null; }

        public Entry TreeEntry()
        {
            string d = TreeDir();
            if (d == null || ParentOf(d) == null) return null;   // not a drive / filesystem root
            string name = d.TrimEnd(Sep); name = name.Substring(name.LastIndexOf(Sep) + 1);
            return new Entry { Name = name, FullPath = d, Dir = true };
        }

        // What a transfer from this side takes: the items selected in the list, else the folder just picked in the tree.
        public List<Entry> SelectedOrTree()
        {
            var l = Selected();
            if (l.Count == 0 && treePicked) { var t = TreeEntry(); if (t != null) l.Add(t); }
            return l;
        }

        public string TargetDir()
        {
            var s = Selected();
            return s.Count == 1 && s[0].Dir ? s[0].FullPath : Cwd;
        }

        public HashSet<string> CurrentNames()
        {
            var h = new HashSet<string>(Remote ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
            foreach (ListViewItem it in List.Items) if (it.Tag is Entry) h.Add(((Entry)it.Tag).Name);
            return h;
        }

        // ------------------------------------------------------------ drag & drop
        bool AcceptDrop(IDataObject d)
        {
            if (d.GetDataPresent(DragFormat)) return d.GetData(DragFormat) != this;
            return Remote && d.GetDataPresent(DataFormats.FileDrop);
        }

        void OnDrop(IDataObject d)
        {
            if (TransferRequested == null) return;
            if (d.GetDataPresent(DragFormat))
            {
                var src = d.GetData(DragFormat) as Pane;
                if (src != null && src != this) TransferRequested(src, src.SelectedOrTree());
            }
            else if (Remote && d.GetDataPresent(DataFormats.FileDrop))
            {
                var l = new List<Entry>();
                foreach (var f in (string[])d.GetData(DataFormats.FileDrop))
                    l.Add(new Entry { Name = Path.GetFileName(f.TrimEnd('\\')), FullPath = f, Dir = Directory.Exists(f) });
                TransferRequested(null, l);
            }
        }
    }

    // File manager for one machine: two equal halves — this Windows PC (left) and the SSH machine (right), each with
    // a folder tree and a file list. Upload / download act on what is selected on each side.
    class FileForm : Form
    {
        readonly Machine m;
        readonly MainForm owner;
        readonly Pane local, remote;
        readonly SplitContainer main;
        readonly Label st;
        readonly ProgressBar pb;
        readonly List<Button> acts = new List<Button>();
        bool busy;

        public FileForm(Machine machine, MainForm mainForm)
        {
            m = machine; owner = mainForm;
            Text = m.Name + " — Tệp · Hoanng Fleet";
            ClientSize = new Size(1400, 780); MinimumSize = new Size(1000, 520);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 10); BackColor = Color.White;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var il = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(20, 22) };
            il.Images.Add("d", Ico(ColorTranslator.FromHtml("#e8a317"), true));
            il.Images.Add("f", Ico(ColorTranslator.FromHtml("#8a8886"), false));
            il.Images.Add("l", Ico(ColorTranslator.FromHtml("#0d6efd"), false));

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(8, 7, 0, 0), WrapContents = false, BackColor = Color.White };
            var up = B("⬆  Tải lên  →", "Tải tệp / thư mục đang chọn ở bên TRÁI vào thư mục đang mở bên PHẢI", delegate { Transfer(true, null); });
            var down = B("⬇  Tải về  ←", "Tải tệp / thư mục đang chọn ở bên PHẢI về thư mục đang mở bên TRÁI", delegate { Transfer(false, null); });
            up.ForeColor = down.ForeColor = Color.White; up.BackColor = down.BackColor = ColorTranslator.FromHtml("#0d6efd");
            up.FlatAppearance.MouseOverBackColor = down.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#0b5cd5");
            bar.Controls.Add(up); bar.Controls.Add(down);
            bar.Controls.Add(new Label { Width = 16 });
            var bClaude = B("◆  Claude ở thư mục máy", "Mở Claude Code ở thư mục đang mở / đang chọn bên phải", delegate { OpenHere(true, remote.TargetDir()); });
            bClaude.ForeColor = ColorTranslator.FromHtml("#c15f3c");
            bar.Controls.Add(bClaude);
            bar.Controls.Add(B("▶  SSH ở thư mục máy", "Mở SSH ở thư mục đang mở / đang chọn bên phải", delegate { OpenHere(false, remote.TargetDir()); }));
            bar.Controls.Add(B("⟳  Làm mới", "Làm mới cả hai bên (F5 trong từng danh sách)", delegate { local.Refresh_(); remote.Refresh_(); }));
            bar.Controls.Add(B("🔁  Đồng bộ sao lưu…", "Đồng bộ thư mục bên phải về thư mục bên trái (thủ công hoặc định kỳ)", delegate { owner.OpenSync(m, remote.Cwd, local.Cwd); }));

            local = new Pane(false, m, il, "💻  Máy tính này — " + Environment.MachineName);
            remote = new Pane(true, m, il, "🖧  " + m.Name + "  —  " + m.Target);
            local.TransferRequested = (src, items) => Transfer(true, items);
            remote.TransferRequested = (src, items) => Transfer(src == null || !src.Remote, items);

            var lm = new ContextMenuStrip { Font = new Font("Segoe UI", 10) };
            lm.Items.Add("⬆  Tải lên máy (vào thư mục đang mở bên phải)", null, delegate { Transfer(true, null); });
            lm.Items.Add("Mở trong Explorer", null, delegate { Process.Start("explorer.exe", Ops.Q(local.TargetDir())); });
            lm.Items.Add("Sao chép đường dẫn", null, delegate { Clipboard.SetText(local.TargetDir()); });
            lm.Items.Add("Làm mới", null, delegate { local.Refresh_(); });
            local.List.ContextMenuStrip = lm;
            var ltm = new ContextMenuStrip { Font = new Font("Segoe UI", 10) };
            ltm.Items.Add("⬆  Tải thư mục này lên máy", null, delegate { var e = local.TreeEntry(); if (e != null) Transfer(true, new List<Entry> { e }); });
            ltm.Items.Add("Mở trong Explorer", null, delegate { var d = local.TreeDir(); if (d != null) Process.Start("explorer.exe", Ops.Q(d)); });
            ltm.Items.Add("Làm mới nhánh", null, delegate { local.RefreshBranch(); });
            local.Tree.ContextMenuStrip = ltm;

            var rm = new ContextMenuStrip { Font = new Font("Segoe UI", 10) };
            rm.Items.Add("⬇  Tải về máy tính (vào thư mục đang mở bên trái)", null, delegate { Transfer(false, null); });
            rm.Items.Add("◆  Mở Claude ở thư mục này", null, delegate { OpenHere(true, remote.TargetDir()); });
            rm.Items.Add("▶  Mở SSH ở thư mục này", null, delegate { OpenHere(false, remote.TargetDir()); });
            rm.Items.Add("Đồng bộ thư mục này về máy tính…", null, delegate { owner.OpenSync(m, remote.TargetDir(), local.Cwd); });
            rm.Items.Add("Sao chép đường dẫn", null, delegate { Clipboard.SetText(remote.TargetDir()); });
            rm.Items.Add("Làm mới", null, delegate { remote.Refresh_(); });
            remote.List.ContextMenuStrip = rm;
            var rtm = new ContextMenuStrip { Font = new Font("Segoe UI", 10) };
            rtm.Items.Add("◆  Mở Claude ở thư mục này", null, delegate { var d = remote.TreeDir(); if (d != null) OpenHere(true, d); });
            rtm.Items.Add("▶  Mở SSH ở thư mục này", null, delegate { var d = remote.TreeDir(); if (d != null) OpenHere(false, d); });
            rtm.Items.Add("⬇  Tải thư mục này về máy tính", null, delegate { var e = remote.TreeEntry(); if (e != null) Transfer(false, new List<Entry> { e }); });
            rtm.Items.Add("Đồng bộ thư mục này về máy tính…", null, delegate { var d = remote.TreeDir(); if (d != null) owner.OpenSync(m, d, local.Cwd); });
            rtm.Items.Add("Sao chép đường dẫn", null, delegate { var d = remote.TreeDir(); if (d != null) Clipboard.SetText(d); });
            rtm.Items.Add("Làm mới nhánh", null, delegate { remote.RefreshBranch(); });
            remote.Tree.ContextMenuStrip = rtm;

            main = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 6, BackColor = ColorTranslator.FromHtml("#c8c6c4") };
            main.Panel1.Controls.Add(local); main.Panel2.Controls.Add(remote);

            var foot = new Panel { Dock = DockStyle.Bottom, Height = 28, BackColor = ColorTranslator.FromHtml("#f6f8fa") };
            st = new Label { Dock = DockStyle.Fill, Padding = new Padding(12, 6, 0, 0), ForeColor = ColorTranslator.FromHtml("#605e5c"), Font = new Font("Segoe UI", 9), Name = "status",
                             Text = "Chọn tệp / thư mục ở một bên rồi bấm Tải lên → hoặc Tải về ←. Kéo thả giữa hai bên, hoặc nhấp đúp một tệp để chuyển sang bên kia." };
            pb = new ProgressBar { Dock = DockStyle.Right, Width = 180, Style = ProgressBarStyle.Marquee, Visible = false, MarqueeAnimationSpeed = 30 };
            foot.Controls.Add(st); foot.Controls.Add(pb);

            Controls.Add(main); Controls.Add(bar); Controls.Add(foot);
            Shown += delegate
            {
                main.SplitterDistance = main.Width / 2;
                string ld = m.LastLocalDir != "" && Directory.Exists(m.LastLocalDir) ? m.LastLocalDir
                          : Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"))
                            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
                            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                local.Init(ld);
                remote.Init(m.Dir != "" ? m.Dir : "~");
            };
            FormClosed += delegate { if (local.Cwd != "" && local.Cwd != m.LastLocalDir) { m.LastLocalDir = local.Cwd; owner.Persist(); } };
        }

        Button B(string text, string tipText, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, Height = 32, FlatStyle = FlatStyle.Flat, ForeColor = ColorTranslator.FromHtml("#0b5cd5"), BackColor = Color.White,
                                 Margin = new Padding(0, 0, 4, 0), Padding = new Padding(6, 0, 6, 0), Cursor = Cursors.Hand };
            b.FlatAppearance.BorderSize = 0; b.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#eef4ff");
            b.Click += click; acts.Add(b);
            new ToolTip().SetToolTip(b, tipText);
            return b;
        }

        static Bitmap Ico(Color c, bool folder)
        {
            var b = new Bitmap(20, 22);
            using (var g = Graphics.FromImage(b))
            using (var br = new SolidBrush(c))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (folder) { g.FillRectangle(br, 2, 5, 7, 3); g.FillRectangle(br, 2, 7, 16, 10); }
                else { g.FillRectangle(br, 5, 3, 11, 15); using (var w = new SolidBrush(Color.White)) { g.FillRectangle(w, 7, 7, 7, 1); g.FillRectangle(w, 7, 10, 7, 1); g.FillRectangle(w, 7, 13, 5, 1); } }
            }
            return b;
        }

        void UI(Action a) { if (IsDisposed) return; try { if (!InvokeRequired) a(); else if (IsHandleCreated) BeginInvoke(a); } catch (InvalidOperationException) { } }

        void OpenHere(bool claude, string dir)
        {
            if (dir == "") return;
            try { Ops.Connect(m, claude, dir); st.Text = (claude ? "Đã mở Claude ở " : "Đã mở SSH ở ") + dir; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Hoanng Fleet", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        static readonly char[] BadWin = { ':', '*', '?', '"', '<', '>', '|', '\\' };

        // upload = local → remote (into remote.Cwd); otherwise remote → local (into local.Cwd).
        void Transfer(bool upload, List<Entry> items)
        {
            if (busy) { st.Text = "Đang chuyển một lượt khác, chờ xong đã."; return; }
            Pane src = upload ? local : remote, dst = upload ? remote : local;
            if (items == null) items = src.SelectedOrTree();
            if (items.Count == 0) { st.Text = "Chọn tệp / thư mục ở bên " + (upload ? "TRÁI (máy tính)" : "PHẢI (máy SSH)") + " trước (Ctrl+A chọn hết)."; return; }
            if (dst.Cwd == "") { st.Text = "Bên " + (upload ? "phải" : "trái") + " chưa mở xong thư mục đích."; return; }
            string dest = dst.Cwd;

            var skip = new List<Entry>();
            if (!upload) foreach (var e in items) if (e.Name.IndexOfAny(BadWin) >= 0) skip.Add(e);
            var names = dst.CurrentNames();
            var clash = new List<string>();
            foreach (var e in items) if (!skip.Contains(e) && names.Contains(e.Name)) clash.Add(e.Name);
            bool overwrite = true;
            if (clash.Count > 0)
            {
                var r = MessageBox.Show(this, clash.Count + " mục đã có ở " + dest + ":\r\n  " + string.Join("\r\n  ", clash.GetRange(0, Math.Min(8, clash.Count)).ToArray())
                                             + (clash.Count > 8 ? "\r\n  …" : "") + "\r\n\r\nCó = ghi đè · Không = bỏ qua các mục trùng · Huỷ = thôi",
                                        "Mục trùng tên", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (r == DialogResult.Cancel) return;
                overwrite = r == DialogResult.Yes;
            }

            var lines = new List<string>(); int count = 0;
            foreach (var e in items)
            {
                if (skip.Contains(e) || (!overwrite && clash.Contains(e.Name))) continue;
                count++;
                if (upload)
                {
                    string target = remote.Join(dest, e.Name);
                    // folders: target the PARENT (dest) so sftp creates exactly one level named after the folder, also when it already exists
                    if (e.Dir) lines.Add("put -R -p " + Ops.SE(Ops.L(e.FullPath)) + " " + Ops.SE(dest));
                    else lines.Add("put -p " + Ops.SE(Ops.L(e.FullPath)) + " " + Ops.SE(target));
                }
                else
                {
                    string target = Ops.L(Path.Combine(dest, e.Name));
                    lines.Add(e.Dir ? "get -R -p " + Ops.SE(e.FullPath) + " " + Ops.SE(Ops.L(dest)) : "get -p " + Ops.SE(e.FullPath) + " " + Ops.SE(target));
                }
            }
            if (count == 0) { st.Text = "Không còn mục nào để chuyển" + (skip.Count > 0 ? " (" + skip.Count + " tên không hợp lệ trên Windows)" : "") + "."; return; }

            string what = (upload ? "Tải lên " : "Tải về ") + count + " mục → " + dest;
            busy = true; pb.Visible = true; st.Text = "Đang " + what.Substring(0, 1).ToLower() + what.Substring(1) + "…";
            foreach (var b in acts) b.Enabled = false;
            var th = new Thread(delegate ()
            {
                Ops.Result r = null; string err = null;
                try { r = Ops.SftpBatch(m, lines, 6 * 3600000, null); } catch (Exception ex) { err = ex.Message; }
                UI(delegate
                {
                    busy = false; pb.Visible = false; foreach (var b in acts) b.Enabled = true;
                    if (err == null && r.Code != 0) err = Ops.Explain(r.Output, false);
                    if (err != null) { st.Text = "LỖI: " + err; MessageBox.Show(this, err + (r != null ? "\r\n\r\n" + r.Output.Trim() : ""), "Chuyển tệp lỗi", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                    else st.Text = "✓ " + what + (skip.Count > 0 ? " · bỏ " + skip.Count + " tên không hợp lệ trên Windows" : "");
                    dst.Refresh_();
                });
            });
            th.IsBackground = true; th.Start();
        }
    }
}
