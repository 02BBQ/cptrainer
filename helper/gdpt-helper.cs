// GD Pattern Trainer helper: captures jump input globally (even while Geometry Dash is focused)
// and streams it to the trainer page over Server-Sent Events. Also serves index.html.
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
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint msg; public IntPtr w, l; public uint time; public int x, y; }

    const int PORT = 8123;
    // Geometry Dash default jump inputs, all treated as one button (single player only)
    static readonly HashSet<int> JUMP = new HashSet<int> { 0x20 /*Space*/, 0x26 /*Up*/, 0x57 /*W*/ };
    // global hotkeys so the trainer can be driven from inside GD
    static readonly Dictionary<int, string> HOT = new Dictionary<int, string> {
        { 0x75, "record" }, { 0x76, "rerecord" }, { 0x77, "practice" }, { 0x78, "stop" }, { 0x79, "auto" } // F6..F10
    };

    static readonly HookProc kbProc = KbHook; // field keep delegates alive for the GC
    static IntPtr kbHook;
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static readonly HashSet<int> down = new HashSet<int>();
    static readonly BlockingCollection<string> queue = new BlockingCollection<string>();
    static readonly List<StreamWriter> clients = new List<StreamWriter>();

    static string Now() {
        return (clock.ElapsedTicks * 1000.0 / Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture);
    }

    static void Jump(int id, bool isDown) {
        string t = Now();
        // any jump key down = pressed; released only when all are up
        if (isDown) { if (down.Add(id) && down.Count == 1) queue.Add("d," + t); }
        else if (down.Remove(id) && down.Count == 0) queue.Add("u," + t);
    }

    static IntPtr KbHook(int code, IntPtr w, IntPtr l) {
        if (code >= 0) {
            int vk = Marshal.ReadInt32(l), msg = (int)w;
            bool isDown = msg == 0x100 || msg == 0x104, isUp = msg == 0x101 || msg == 0x105;
            if (JUMP.Contains(vk) && (isDown || isUp)) Jump(vk, isDown);
            else if (isDown && HOT.ContainsKey(vk)) queue.Add("k," + HOT[vk]);
        }
        return CallNextHookEx(kbHook, code, w, l); // never swallow input
    }

    // hook callbacks must return fast, so network writes happen here
    static void Sender() {
        while (true) {
            string ev;
            if (!queue.TryTake(out ev, 250)) ev = "p," + Now(); // ping keeps clock offset fresh
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
                r.Headers["Cache-Control"] = "no-store";
                if (ctx.Request.Url.AbsolutePath == "/events") {
                    r.ContentType = "text/event-stream";
                    r.SendChunked = true;
                    StreamWriter s = new StreamWriter(r.OutputStream, new UTF8Encoding(false));
                    s.Write("data: p," + Now() + "\n\n"); s.Flush();
                    lock (clients) clients.Add(s);
                } else if (ctx.Request.Url.AbsolutePath == "/") {
                    byte[] b = File.ReadAllBytes(page); // re-read each time so edits show on refresh
                    r.ContentType = "text/html; charset=utf-8";
                    r.OutputStream.Write(b, 0, b.Length);
                    r.Close();
                } else { r.StatusCode = 404; r.Close(); }
            } catch (Exception e) { Console.WriteLine("request failed: " + e.Message); }
        }
    }

    static void Main() {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        string page = Path.Combine(dir, "index.html");
        if (!File.Exists(page)) page = Path.Combine(dir, "..", "index.html");
        if (!File.Exists(page)) { Console.WriteLine("index.html not found next to the exe"); Console.ReadLine(); return; }

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
        Console.WriteLine("Hotkeys: F6 record, F7 re-record, F8 practice, F9 stop, F10 macro playback");
        Console.WriteLine("Close this window to quit.");
        Process.Start(url);

        MSG m; // low-level hooks need a message loop on the installing thread
        while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { }
    }
}
