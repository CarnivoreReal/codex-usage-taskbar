using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace CodexUsage
{
    internal sealed class UsageWindow
    {
        public int UsedPercent;
        public long ResetAt;
    }

    internal sealed class UsageSnapshot
    {
        public UsageWindow ShortWindow;
        public UsageWindow WeekWindow;
    }

    internal sealed class UsageClient : IDisposable
    {
        private readonly Process process;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private readonly object writeLock = new object();
        private readonly object pendingLock = new object();
        private readonly Dictionary<int, TaskCompletionSource<Dictionary<string, object>>> pending =
            new Dictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
        private int nextId;
        private bool initialized;

        public UsageClient()
        {
            string cli = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "codex.cmd");
            if (!File.Exists(cli))
                throw new FileNotFoundException("找不到 Codex CLI。请先安装 Codex CLI，或把 codex.cmd 放到 %APPDATA%\\npm。", cli);

            process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = "/d /s /c \"\"" + cli + "\" app-server --stdio\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            process.OutputDataReceived += OnOutput;
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        public async Task<UsageSnapshot> ReadAsync()
        {
            if (!initialized)
            {
                int initializeId = InterlockedIncrement(ref nextId);
                var init = new Dictionary<string, object>
                {
                    { "clientInfo", new Dictionary<string, object> { { "name", "codexUsageTaskbar" }, { "title", "Codex Usage Taskbar" }, { "version", "1.0.0" } } },
                    { "capabilities", new Dictionary<string, object> { { "experimentalApi", true } } }
                };
                Dictionary<string, object> initResponse = await RequestAsync("initialize", init, initializeId).ConfigureAwait(false);
                if (initResponse.ContainsKey("error"))
                    throw new InvalidOperationException("Codex app-server 初始化失败。");
                Send(new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "method", "initialized" } });
                initialized = true;
            }            Dictionary<string, object> response = await RequestAsync("account/rateLimits/read", new Dictionary<string, object>(), null).ConfigureAwait(false);
            object error;
            if (response.TryGetValue("error", out error))
                throw new InvalidOperationException("读取 Codex 用量失败。");

            var result = AsMap(response.ContainsKey("result") ? response["result"] : null);
            var byId = AsMap(result.ContainsKey("rateLimitsByLimitId") ? result["rateLimitsByLimitId"] : null);
            var limits = AsMap(byId.ContainsKey("codex") ? byId["codex"] : (result.ContainsKey("rateLimits") ? result["rateLimits"] : null));
            var snapshot = new UsageSnapshot();
            snapshot.ShortWindow = ReadWindow(limits.ContainsKey("primary") ? limits["primary"] : null);
            snapshot.WeekWindow = ReadWindow(limits.ContainsKey("secondary") ? limits["secondary"] : null);

            // Some accounts return a single bucket only. Classify it by its duration.
            if (snapshot.ShortWindow == null || snapshot.WeekWindow == null)
            {
                var candidates = new List<UsageWindow>();
                foreach (object value in limits.Values)
                {
                    UsageWindow candidate = ReadWindow(value);
                    if (candidate != null) candidates.Add(candidate);
                }
                foreach (UsageWindow candidate in candidates)
                {
                    long secondsLeft = candidate.ResetAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    if (secondsLeft <= 12 * 60 * 60 && snapshot.ShortWindow == null) snapshot.ShortWindow = candidate;
                    if (secondsLeft > 12 * 60 * 60 && snapshot.WeekWindow == null) snapshot.WeekWindow = candidate;
                }
            }
            return snapshot;
        }

        private async Task<Dictionary<string, object>> RequestAsync(string method, Dictionary<string, object> parameters, int? fixedId)
        {
            int id = fixedId ?? InterlockedIncrement(ref nextId);
            var completion = new TaskCompletionSource<Dictionary<string, object>>();
            lock (pendingLock) pending[id] = completion;
            var message = new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id }, { "method", method }, { "params", parameters } };
            Send(message);
            Task finished = await Task.WhenAny(completion.Task, Task.Delay(12000)).ConfigureAwait(false);
            if (finished != completion.Task)
            {
                lock (pendingLock) pending.Remove(id);
                throw new TimeoutException("Codex 用量接口暂时没有响应。");
            }
            return await completion.Task.ConfigureAwait(false);
        }

        private void Send(Dictionary<string, object> message)
        {
            lock (writeLock)
            {
                if (process.HasExited) throw new InvalidOperationException("Codex app-server 已退出。");
                process.StandardInput.WriteLine(json.Serialize(message));
                process.StandardInput.Flush();
            }
        }

        private void OnOutput(object sender, DataReceivedEventArgs e)
        {
            if (String.IsNullOrWhiteSpace(e.Data)) return;
            try
            {
                var map = AsMap(json.DeserializeObject(e.Data));
                if (!map.ContainsKey("id")) return;
                int id = Convert.ToInt32(map["id"], CultureInfo.InvariantCulture);
                TaskCompletionSource<Dictionary<string, object>> completion;
                lock (pendingLock)
                {
                    if (!pending.TryGetValue(id, out completion)) return;
                    pending.Remove(id);
                }
                completion.TrySetResult(map);
            }
            catch { }
        }

        private static int InterlockedIncrement(ref int value) { return System.Threading.Interlocked.Increment(ref value); }
        private static Dictionary<string, object> AsMap(object value) { return value as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        private static UsageWindow ReadWindow(object value)
        {
            var map = AsMap(value);
            if (!map.ContainsKey("usedPercent")) return null;
            object reset;
            long resetAt = map.TryGetValue("resetsAt", out reset) && reset != null ? Convert.ToInt64(reset, CultureInfo.InvariantCulture) : 0;
            object used = map["usedPercent"];
            return new UsageWindow { UsedPercent = Math.Max(0, Math.Min(100, Convert.ToInt32(used, CultureInfo.InvariantCulture))), ResetAt = resetAt };
        }

        public void Dispose()
        {
            try { if (!process.HasExited) { process.StandardInput.Close(); process.Kill(); } } catch { }
            process.Dispose();
        }
    }

    internal sealed class TaskbarWidget : Form
    {
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const int WM_SETTINGCHANGE = 0x001A;
        private readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer layoutTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer clockTimer = new System.Windows.Forms.Timer();
        private readonly ToolTip toolTip = new ToolTip();
        private readonly ContextMenuStrip contextMenu = new ContextMenuStrip();
        private UsageClient client;
        private UsageSnapshot snapshot;
        private string status = "正在读取 Codex 用量…";
        private bool busy;
        private readonly Font rowFont = new Font("Segoe UI", 9.0f, FontStyle.Regular, GraphicsUnit.Point);
        private readonly Font valueFont = new Font("Segoe UI", 9.0f, FontStyle.Bold, GraphicsUnit.Point);
        private readonly Brush textBrush = new SolidBrush(Color.FromArgb(20, 20, 20));
        private readonly Brush dimBrush = new SolidBrush(Color.FromArgb(20, 20, 20));
        private readonly Color transparentColor = Color.Magenta;

        public TaskbarWidget()
        {
            Text = "Codex 用量";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(182, 40);
            BackColor = transparentColor;
            TransparencyKey = transparentColor;
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
            toolTip.SetToolTip(this, "左键立即刷新，右键菜单可退出");
            refreshTimer.Interval = 60000;
            refreshTimer.Tick += async (s, e) => await RefreshUsageAsync();
            layoutTimer.Interval = 2500;
            layoutTimer.Tick += (s, e) => AttachToTaskbar();
            clockTimer.Interval = 1000;
            clockTimer.Tick += (s, e) => Invalidate();
            var refreshItem = new ToolStripMenuItem("立即刷新");
            refreshItem.Click += async (s, e) => await RefreshUsageAsync();
            var exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += (s, e) => Close();
            contextMenu.Items.Add(refreshItem);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(exitItem);
            ContextMenuStrip = contextMenu;
            MouseClick += async (s, e) =>
            {
                if (e.Button == MouseButtons.Left) await RefreshUsageAsync();
            };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            AttachToTaskbar();
            layoutTimer.Start();
            refreshTimer.Start();
            clockTimer.Start();
            RefreshUsageAsync().ContinueWith(delegate { });
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
            g.Clear(transparentColor);
            if (snapshot == null)
            {
                g.DrawString(status, rowFont, textBrush, 10, 12);
            }
            else
            {
                DrawRow(g, snapshot.ShortWindow, "5小时", 5);
                DrawRow(g, snapshot.WeekWindow, "1周", 21);
            }
            base.OnPaint(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        private void DrawRow(Graphics g, UsageWindow window, string label, int y)
        {
            g.DrawString(label, rowFont, dimBrush, 10, y);
            if (window == null)
            {
                g.DrawString("--", valueFont, textBrush, 71, y);
                return;
            }
            g.DrawString((100 - window.UsedPercent).ToString(CultureInfo.InvariantCulture) + "%", valueFont, textBrush, 65, y);
            string reset = window.ResetAt > 0 ? FormatReset(window.ResetAt, "5小时" == label) : "--";
            SizeF size = g.MeasureString(reset, rowFont);
            g.DrawString(reset, rowFont, dimBrush, Width - size.Width - 8, y);
        }

        private static string FormatReset(long timestamp, bool shortWindow)
        {
            DateTime local = DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime().DateTime;
            if (shortWindow) return local.ToString("HH:mm", CultureInfo.InvariantCulture);
            return local.ToString("M月d日", CultureInfo.GetCultureInfo("zh-CN"));
        }

        private async Task RefreshUsageAsync()
        {
            if (busy) return;
            busy = true;
            try
            {
                if (client == null) client = new UsageClient();
                snapshot = await client.ReadAsync();
                status = "";
                string shortReset = snapshot.ShortWindow == null || snapshot.ShortWindow.ResetAt == 0
                    ? "未知"
                    : DateTimeOffset.FromUnixTimeSeconds(snapshot.ShortWindow.ResetAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                string weekReset = snapshot.WeekWindow == null || snapshot.WeekWindow.ResetAt == 0
                    ? "未知"
                    : DateTimeOffset.FromUnixTimeSeconds(snapshot.WeekWindow.ResetAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                toolTip.SetToolTip(this, "下次额度重置时间（本地）：5小时 " + shortReset + "；1周 " + weekReset + "\n用量每分钟自动刷新；左键可立即刷新");
            }
            catch
            {
                status = "用量读取失败";
                toolTip.SetToolTip(this, "用量读取失败；确认 Codex CLI 已安装且已登录");
                if (client != null)
                {
                    client.Dispose();
                    client = null;
                }
            }
            finally
            {
                busy = false;
                if (!IsDisposed) Invalidate();
            }
        }

        private void AttachToTaskbar()
        {
            IntPtr taskbar = Native.FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero) return;
            IntPtr notify = Native.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            Native.RECT taskbarScreen;
            if (!Native.GetWindowRect(taskbar, out taskbarScreen)) return;
            int x = taskbarScreen.Right - Width - 100;
            int y = taskbarScreen.Top + (taskbarScreen.Bottom - taskbarScreen.Top - Height) / 2;
            if (notify != IntPtr.Zero)
            {
                Native.RECT notifyRect;
                if (Native.GetWindowRect(notify, out notifyRect))
                    x = notifyRect.Left - Width - 6;
            }
            if (x < taskbarScreen.Left) x = taskbarScreen.Right - Width - 8;
            Native.SetWindowPos(Handle, new IntPtr(-1), x, y, Width, Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_SETTINGCHANGE) AttachToTaskbar();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            refreshTimer.Stop();
            layoutTimer.Stop();
            clockTimer.Stop();
            if (client != null) client.Dispose();
            base.OnFormClosed(e);
        }

        private static void LogError(Exception ex)
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsage", "errors.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool firstInstance;
            using (var mutex = new System.Threading.Mutex(true, "Local\\CodexUsageTaskbarSingleInstance", out firstInstance))
            {
                if (!firstInstance) return;
                Application.ThreadException += (s, e) => TaskbarWidgetErrorLog(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => TaskbarWidgetErrorLog(e.ExceptionObject as Exception);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TaskbarWidget());
            }
        }

        private static void TaskbarWidgetErrorLog(Exception ex)
        {
            if (ex == null) return;
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsage", "errors.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }
}





