// GD Pattern Trainer helper: captures jump input globally (even while Geometry Dash is focused)
// and streams it to the trainer page over Server-Sent Events. Also serves index.html, and can
// play a pattern back into GD as real Space key presses (macro playback for checking a pattern).
// Build: build.cmd (uses the C# compiler that ships with Windows, no install needed).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

class Helper {
    delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint tid);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint msg; public IntPtr w, l; public uint time; public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    // INPUT is a union; MOUSEINPUT is its largest member, so it sets the size SendInput expects (x64 build)
    [StructLayout(LayoutKind.Explicit)] struct INPUT {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    const int PORT = 8123;
    static readonly IntPtr MARK = new IntPtr(0x47445054); // dwExtraInfo tag on keys we send
    // Geometry Dash default jump inputs, all treated as one button (single player only)
    static readonly HashSet<int> JUMP = new HashSet<int> { 0x20 /*Space*/, 0x26 /*Up*/, 0x57 /*W*/ };
    // global hotkeys so the trainer can be driven from inside GD
    static readonly Dictionary<int, string> HOT = new Dictionary<int, string> {
        { 0x75, "record" }, { 0x76, "rerecord" }, { 0x77, "practice" }, { 0x78, "stop" }, { 0x79, "auto" }, { 0x7A, "gd" } // F6..F11
    };

    static readonly HookProc kbProc = KbHook; // field keeps the delegate alive for the GC
    static IntPtr kbHook;
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static readonly HashSet<int> down = new HashSet<int>();
    static readonly BlockingCollection<string> queue = new BlockingCollection<string>();
    static readonly List<StreamWriter> clients = new List<StreamWriter>();

    // macro: armed with a pattern, starts on your next real jump press (= the first note).
    // The playback thread is created and warmed up at arm time so the first note is not late.
    static volatile bool armed;
    static volatile int macroGen;
    static double startT0;
    static ManualResetEvent startSignal = new ManualResetEvent(false);

    static double NowMs() { return clock.ElapsedTicks * 1000.0 / Stopwatch.Frequency; }
    static string Fmt(double t) { return t.ToString("F3", CultureInfo.InvariantCulture); }

    static void Jump(int id, bool isDown) {
        double now = NowMs();
        // any jump key down = pressed; released only when all are up
        if (isDown) {
            if (down.Add(id) && down.Count == 1) {
                queue.Add("d," + Fmt(now));
                if (armed) { armed = false; startT0 = now; startSignal.Set(); }
            }
        }
        else if (down.Remove(id) && down.Count == 0) queue.Add("u," + Fmt(now));
    }

    static IntPtr KbHook(int code, IntPtr w, IntPtr l) {
        if (code >= 0) {
            int vk = Marshal.ReadInt32(l), msg = (int)w;
            bool injected = Marshal.ReadIntPtr(l, 16) == MARK; // our own macro presses: don't echo them back
            bool isDown = msg == 0x100 || msg == 0x104, isUp = msg == 0x101 || msg == 0x105;
            if (!injected && JUMP.Contains(vk) && (isDown || isUp)) Jump(vk, isDown);
            else if (!injected && isDown && HOT.ContainsKey(vk)) {
                if (vk == 0x78) StopMacro(); // F9 also cancels a GD macro
                queue.Add("k," + HOT[vk]);
            }
        }
        return CallNextHookEx(kbHook, code, w, l); // never swallow input
    }

    static void SendSpace(bool isDown) {
        INPUT[] i = new INPUT[1];
        i[0].type = 1; // INPUT_KEYBOARD
        i[0].ki.vk = 0x20; i[0].ki.scan = 0x39;
        i[0].ki.flags = isDown ? 0u : 2u; // KEYEVENTF_KEYUP
        i[0].ki.extra = MARK;
        SendInput(1, i, Marshal.SizeOf(typeof(INPUT)));
    }

    static void WaitUntil(double target) {
        while (true) {
            double left = target - NowMs();
            if (left <= 0) return;
            if (left > 3) Thread.Sleep(1); else Thread.SpinWait(100); // sleep coarse, spin the last ms for accuracy
        }
    }

    // times are ms relative to the first note, which you press yourself; the rest is sent as Space
    static void Play(double[] t, double[] d, int gen, ManualResetEvent go) {
        List<KeyValuePair<double, bool>> ev = new List<KeyValuePair<double, bool>>();
        for (int i = 1; i < t.Length; i++) {
            ev.Add(new KeyValuePair<double, bool>(t[i], true));
            ev.Add(new KeyValuePair<double, bool>(t[i] + Math.Max(d[i], 16), false)); // GD needs the key down for a frame
        }
        ev.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Value.CompareTo(b.Value)); // release before press on ties
        SendInput(0, new INPUT[1], Marshal.SizeOf(typeof(INPUT))); // JIT the P/Invoke path before it matters
        WaitUntil(NowMs());
        while (!go.WaitOne(100)) if (gen != macroGen) return; // cancelled before it started
        double t0 = startT0;
        queue.Add("m,playing");
        bool held = false;
        double worst = 0;
        foreach (KeyValuePair<double, bool> e in ev) {
            WaitUntil(t0 + e.Key);
            if (gen != macroGen) { if (held) SendSpace(false); queue.Add("m,stopped"); return; }
            double late = NowMs() - (t0 + e.Key);
            SendSpace(e.Value); held = e.Value;
            worst = Math.Max(worst, late);
        }
        Console.WriteLine("macro done: " + ev.Count / 2 + " notes, worst lateness " + worst.ToString("F2") + " ms");
        queue.Add("m,done");
    }

    static void StopMacro() {
        bool wasActive = armed;
        armed = false; macroGen++;
        if (wasActive) queue.Add("m,stopped");
    }

    // hook callbacks must return fast, so network writes happen here
    static void Sender() {
        while (true) {
            string ev;
            if (!queue.TryTake(out ev, 250)) ev = "p," + Fmt(NowMs()); // ping keeps clock offset fresh
            lock (clients) {
                for (int i = clients.Count - 1; i >= 0; i--) {
                    try { clients[i].Write("data: " + ev + "\n\n"); clients[i].Flush(); }
                    catch { clients.RemoveAt(i); }
                }
            }
        }
    }

    static void Server(HttpListener http, string page) {
        while (true) {
            HttpListenerContext ctx = http.GetContext();
            try {
                HttpListenerResponse r = ctx.Response;
                string path = ctx.Request.Url.AbsolutePath;
                r.Headers["Cache-Control"] = "no-store";
                if (path == "/events") {
                    r.ContentType = "text/event-stream";
                    r.SendChunked = true;
                    StreamWriter s = new StreamWriter(r.OutputStream, new UTF8Encoding(false));
                    s.Write("data: p," + Fmt(NowMs()) + "\n\n"); s.Flush();
                    lock (clients) clients.Add(s);
                } else if (path == "/macro" && ctx.Request.HttpMethod == "POST") {
                    // body: one "t,d" line per note, ms at real speed, t relative to the first note
                    string body = new StreamReader(ctx.Request.InputStream).ReadToEnd();
                    List<double> t = new List<double>(), d = new List<double>();
                    foreach (string line in body.Split('\n')) {
                        string[] p = line.Split(',');
                        if (p.Length != 2) continue;
                        t.Add(double.Parse(p[0], CultureInfo.InvariantCulture));
                        d.Add(double.Parse(p[1], CultureInfo.InvariantCulture));
                    }
                    StopMacro();
                    if (t.Count > 0) {
                        startSignal = new ManualResetEvent(false);
                        double[] ta = t.ToArray(), da = d.ToArray(); int gen = macroGen; ManualResetEvent go = startSignal;
                        new Thread(() => Play(ta, da, gen, go)) { IsBackground = true, Priority = ThreadPriority.Highest }.Start();
                        armed = true; queue.Add("m,armed");
                    }
                    r.Close();
                } else if (path == "/macro/stop" && ctx.Request.HttpMethod == "POST") {
                    StopMacro(); r.Close();
                } else if (path == "/") {
                    byte[] b = File.ReadAllBytes(page); // re-read each time so edits show on refresh
                    r.ContentType = "text/html; charset=utf-8";
                    r.ContentLength64 = b.Length; // explicit length: HTTP/1.0 clients can't take chunked
                    if (ctx.Request.HttpMethod != "HEAD") r.OutputStream.Write(b, 0, b.Length); // HEAD probes get headers only
                    r.Close();
                } else { r.StatusCode = 404; r.Close(); }
            } catch (Exception e) { Console.WriteLine("request failed (" + ctx.Request.HttpMethod + " " + ctx.Request.RawUrl + "): " + e.Message); }
        }
    }

    static void Main() {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        string page = Path.Combine(dir, "index.html");
        if (!File.Exists(page)) page = Path.Combine(dir, "..", "index.html");
        if (!File.Exists(page)) { Console.WriteLine("index.html not found next to the exe"); Console.ReadLine(); return; }

        timeBeginPeriod(1); // 1ms sleep granularity for macro timing

        HttpListener http = new HttpListener();
        http.Prefixes.Add("http://localhost:" + PORT + "/");
        try { http.Start(); }
        catch (Exception e) { Console.WriteLine("port " + PORT + " busy? " + e.Message); Console.ReadLine(); return; }
        new Thread(() => Server(http, page)) { IsBackground = true }.Start();
        new Thread(Sender) { IsBackground = true }.Start();

        IntPtr mod = GetModuleHandle(null);
        kbHook = SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, kbProc, mod, 0);

        string url = "http://localhost:" + PORT + "/";
        Console.WriteLine("GD Pattern Trainer helper running: " + url);
        Console.WriteLine("Jump: Space / Up / W (global, mouse ignored)");
        Console.WriteLine("Hotkeys: F6 record, F7 re-record, F8 practice, F9 stop, F10 macro playback, F11 macro into GD");
        Console.WriteLine("Close this window to quit.");
        Process.Start(url);

        MSG m; // low-level hooks need a message loop on the installing thread
        while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { }
    }
}
