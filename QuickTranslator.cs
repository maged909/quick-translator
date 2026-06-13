// QuickTranslator - lightweight native tray translator (WinForms, .NET Framework)
// Single process: tray + global hotkey + Quick window + Main/Settings window.
// No AutoHotkey, no PowerShell. Compiled with the in-box .NET Framework csc.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace QuickTranslator
{
    // ---------------- theme ----------------
    static class Theme
    {
        public static Color Bg, Card, Card2, Text, Muted, Accent, AccentH, Ink;
        public static bool IsDark = true;
        public static float Scale = 1f;
        static Theme() { Apply("dark", 1f); }
        public static void Apply(string mode, float scale)
        {
            Scale = scale <= 0 ? 1f : scale;
            bool dark = mode == "dark" || (mode == "system" && SystemDark());
            IsDark = dark;
            Accent = Color.FromArgb(59, 130, 246); AccentH = Color.FromArgb(96, 165, 250); Ink = Color.FromArgb(20, 21, 27);
            if (dark)
            {
                Bg = Color.FromArgb(24, 24, 28); Card = Color.FromArgb(37, 37, 44); Card2 = Color.FromArgb(48, 48, 56);
                Text = Color.FromArgb(238, 238, 242); Muted = Color.FromArgb(150, 150, 162);
            }
            else
            {
                Bg = Color.FromArgb(245, 246, 248); Card = Color.FromArgb(255, 255, 255); Card2 = Color.FromArgb(228, 231, 236);
                Text = Color.FromArgb(24, 26, 33); Muted = Color.FromArgb(110, 116, 128);
            }
        }
        static bool SystemDark()
        {
            try { var v = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1); if (v != null) return ((int)v) == 0; } catch { }
            return true;
        }
        public static Font F(float s) { return new Font("Segoe UI", s * Scale); }
        public static Font FS(float s) { return new Font("Segoe UI Semibold", s * Scale); }
    }

    static class RUtil
    {
        public static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d <= 0) { p.AddRectangle(r); p.CloseFigure(); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // ---------------- native ----------------
    static class Native
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);
        [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
        [DllImport("uxtheme.dll", EntryPoint = "#133")] static extern int AllowDarkModeForWindow(IntPtr h, bool a);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr h, string sub, string list);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int n);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
        static readonly IntPtr DPI_PMV2 = new IntPtr(-4);   // PER_MONITOR_AWARE_V2
        public static IntPtr DpiAwareOn() { try { return SetThreadDpiAwarenessContext(DPI_PMV2); } catch { return IntPtr.Zero; } }
        public static void DpiAwareOff(IntPtr prev) { try { if (prev != IntPtr.Zero) SetThreadDpiAwarenessContext(prev); } catch { } }
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte sc, uint f, IntPtr e);
        [DllImport("winmm.dll", CharSet = CharSet.Auto)] static extern int mciSendString(string cmd, System.Text.StringBuilder ret, int len, IntPtr cb);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)] public static extern IntPtr GetModuleHandle(string name);

        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr extra; }

        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
        public const int VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        public static bool Down(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
        public static void PlayMp3(string path)
        {
            try
            {
                mciSendString("close qtts", null, 0, IntPtr.Zero);
                mciSendString("open \"" + path + "\" type mpegvideo alias qtts", null, 0, IntPtr.Zero);
                mciSendString("play qtts", null, 0, IntPtr.Zero);
            }
            catch { }
        }

        public static void DarkMode() { try { SetPreferredAppMode(Theme.IsDark ? 2 : 3); } catch { } }  // 2=ForceDark 3=ForceLight
        public static void DarkTitle(IntPtr h)
        {
            try { int d = Theme.IsDark ? 1 : 0; DwmSetWindowAttribute(h, 20, ref d, 4); int r = 2; DwmSetWindowAttribute(h, 33, ref r, 4); } catch { }
        }
        public static void DarkScroll(IntPtr h) { try { AllowDarkModeForWindow(h, Theme.IsDark); SetWindowTheme(h, Theme.IsDark ? "DarkMode_Explorer" : "Explorer", null); } catch { } }
        public static void ShowFront(IntPtr h) { try { ShowWindow(h, 9); SetForegroundWindow(h); } catch { } }
        public static void SendCopy()
        {
            const byte C = 0x43; const uint UP = 2;
            // The hotkey leaves modifiers physically held (e.g. Ctrl, maybe Shift/Alt).
            // Release them first so we inject a CLEAN Ctrl+C (not Ctrl+Shift+C etc.) —
            // this is the main cause of the occasional "no selection copied".
            if (Down(VK_SHIFT)) keybd_event((byte)VK_SHIFT, 0, UP, IntPtr.Zero);
            if (Down(VK_MENU)) keybd_event((byte)VK_MENU, 0, UP, IntPtr.Zero);
            if (Down(VK_LWIN)) keybd_event((byte)VK_LWIN, 0, UP, IntPtr.Zero);
            if (Down(VK_RWIN)) keybd_event((byte)VK_RWIN, 0, UP, IntPtr.Zero);
            if (Down(VK_CONTROL)) keybd_event((byte)VK_CONTROL, 0, UP, IntPtr.Zero);
            System.Threading.Thread.Sleep(8);
            keybd_event((byte)VK_CONTROL, 0, 0, IntPtr.Zero);
            keybd_event(C, 0, 0, IntPtr.Zero);
            keybd_event(C, 0, UP, IntPtr.Zero);
            keybd_event((byte)VK_CONTROL, 0, UP, IntPtr.Zero);
        }
        public static void PasteTo(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            const byte V = 0x56; const uint KEYUP = 2;
            SetForegroundWindow(h);
            System.Threading.Thread.Sleep(80);
            keybd_event((byte)VK_CONTROL, 0, 0, IntPtr.Zero);
            keybd_event(V, 0, 0, IntPtr.Zero);
            keybd_event(V, 0, KEYUP, IntPtr.Zero);
            keybd_event((byte)VK_CONTROL, 0, KEYUP, IntPtr.Zero);
        }
    }

    // ---------------- rounded controls ----------------
    class RButton : Button
    {
        public Color Fill = Theme.Card;
        public Color Hover = Theme.Card2;
        public int Radius = 9;
        public ContentAlignment Align = ContentAlignment.MiddleCenter;
        bool over;
        public RButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
            BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.F(10f); Cursor = Cursors.Hand;
        }
        protected override void OnMouseEnter(EventArgs e) { over = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { over = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var p = RUtil.Round(r, Radius))
            using (var b = new SolidBrush(over && Enabled ? Hover : Fill))
                g.FillPath(b, p);
            var fl = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
            if (Align == ContentAlignment.MiddleLeft) { fl |= TextFormatFlags.Left; r.X += 10; r.Width -= 12; }
            else fl |= TextFormatFlags.HorizontalCenter;
            TextRenderer.DrawText(g, Text, Font, r, Enabled ? ForeColor : Theme.Muted, fl);
        }
    }

    class RInput : Panel
    {
        public TextBox Box;
        public int Radius = 9;
        public RInput(bool multiline)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg; Padding = new Padding(11, 9, 8, 9);
            Box = new TextBox();
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Theme.Card; Box.ForeColor = Theme.Text;
            Box.Multiline = multiline; Box.Font = Theme.F(11f); Box.Dock = DockStyle.Fill;
            if (multiline) Box.ScrollBars = ScrollBars.Vertical;
            Box.HandleCreated += delegate { Native.DarkScroll(Box.Handle); };
            Controls.Add(Box);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            using (var p = RUtil.Round(new Rectangle(0, 0, Width - 1, Height - 1), Radius))
            using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, p);
        }
    }

    class LoadingBar : Control
    {
        System.Windows.Forms.Timer t; int pos;
        public LoadingBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Theme.Bg; Height = 4; Visible = false;
            t = new System.Windows.Forms.Timer(); t.Interval = 16; t.Tick += delegate { pos = (pos + 6) % (Width + 80); Invalidate(); };
        }
        public void Start() { Visible = true; t.Start(); }
        public void Stop() { t.Stop(); Visible = false; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            int seg = 70; int x = pos - 80;
            using (var b = new SolidBrush(Theme.Accent))
            using (var p = RUtil.Round(new Rectangle(x, 0, seg, Height), 2)) g.FillPath(b, p);
        }
    }

    class DarkList : ListBox
    {
        public DarkList()
        {
            DrawMode = DrawMode.OwnerDrawFixed; ItemHeight = 30;
            BorderStyle = BorderStyle.None; BackColor = Theme.Card; ForeColor = Theme.Text;
            Font = Theme.F(10.5f); IntegralHeight = false;
            HandleCreated += delegate { Native.DarkScroll(Handle); };
        }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var bg = new SolidBrush(Theme.Card)) g.FillRectangle(bg, e.Bounds);
            if (sel)
            {
                var r = new Rectangle(e.Bounds.X + 3, e.Bounds.Y + 2, e.Bounds.Width - 6, e.Bounds.Height - 4);
                using (var b = new SolidBrush(Theme.Accent)) using (var p = RUtil.Round(r, 7)) g.FillPath(b, p);
            }
            string txt = Items[e.Index].ToString();
            var tr = new Rectangle(e.Bounds.X + 12, e.Bounds.Y, e.Bounds.Width - 14, e.Bounds.Height);
            TextRenderer.DrawText(g, txt, Font, tr, sel ? Color.White : Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }

    // ---------------- data ----------------
    static class Lang
    {
        public const string AUTO = "Auto-detect";
        public static List<string> Names = new List<string>();
        public static Dictionary<string, string> Code = new Dictionary<string, string>();
        static Lang()
        {
            string data = "Afrikaans:af;Albanian:sq;Amharic:am;Arabic:ar;Armenian:hy;Azerbaijani:az;Basque:eu;Belarusian:be;Bengali:bn;Bosnian:bs;Bulgarian:bg;Catalan:ca;Cebuano:ceb;Chichewa:ny;Chinese (Simplified):zh-CN;Chinese (Traditional):zh-TW;Corsican:co;Croatian:hr;Czech:cs;Danish:da;Dutch:nl;English:en;Esperanto:eo;Estonian:et;Filipino:tl;Finnish:fi;French:fr;Frisian:fy;Galician:gl;Georgian:ka;German:de;Greek:el;Gujarati:gu;Haitian Creole:ht;Hausa:ha;Hawaiian:haw;Hebrew:iw;Hindi:hi;Hmong:hmn;Hungarian:hu;Icelandic:is;Igbo:ig;Indonesian:id;Irish:ga;Italian:it;Japanese:ja;Javanese:jw;Kannada:kn;Kazakh:kk;Khmer:km;Kinyarwanda:rw;Korean:ko;Kurdish:ku;Kyrgyz:ky;Lao:lo;Latin:la;Latvian:lv;Lithuanian:lt;Luxembourgish:lb;Macedonian:mk;Malagasy:mg;Malay:ms;Malayalam:ml;Maltese:mt;Maori:mi;Marathi:mr;Mongolian:mn;Myanmar (Burmese):my;Nepali:ne;Norwegian:no;Odia (Oriya):or;Pashto:ps;Persian:fa;Polish:pl;Portuguese:pt;Punjabi:pa;Romanian:ro;Russian:ru;Samoan:sm;Scots Gaelic:gd;Serbian:sr;Sesotho:st;Shona:sn;Sindhi:sd;Sinhala:si;Slovak:sk;Slovenian:sl;Somali:so;Spanish:es;Sundanese:su;Swahili:sw;Swedish:sv;Tajik:tg;Tamil:ta;Tatar:tt;Telugu:te;Thai:th;Turkish:tr;Turkmen:tk;Ukrainian:uk;Urdu:ur;Uyghur:ug;Uzbek:uz;Vietnamese:vi;Welsh:cy;Xhosa:xh;Yiddish:yi;Yoruba:yo;Zulu:zu";
            foreach (string pair in data.Split(';'))
            {
                int i = pair.LastIndexOf(':');
                string n = pair.Substring(0, i); string c = pair.Substring(i + 1);
                Names.Add(n); Code[n] = c;
            }
        }
        public static string Of(string name)
        {
            if (name == AUTO) return "auto";
            return Code.ContainsKey(name) ? Code[name] : "en";
        }
        public static string NameOfCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            foreach (var kv in Code) if (kv.Value == code) return kv.Key;
            return null;
        }
    }

    class Res { public string Text; public string Src; public List<string> Alts; public string Rom; public string Engine; }

    static class Engine
    {
        static readonly HttpClient http;
        static readonly JavaScriptSerializer ser = new JavaScriptSerializer();
        public static readonly string[] Names = { "Google", "MyMemory", "Lingva", "LibreTranslate" };
        static Engine()
        {
            http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(12);
            http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
        }
        public static async Task<Res> Translate(string engine, string text, string sl, string tl)
        {
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return new Res { Text = "", Src = "" };
            // try the chosen engine, then automatically fall back to the others
            var order = new List<string>(); order.Add(engine);
            foreach (var n in Names) if (n != engine) order.Add(n);
            Exception last = null;
            foreach (var n in order)
            {
                try
                {
                    Res r = await One(n, text, sl, tl);
                    if (r != null && !string.IsNullOrEmpty(r.Text)) { r.Engine = n; return r; }
                }
                catch (Exception e) { last = e; }
            }
            return new Res { Text = "[All engines failed] " + (last != null ? last.Message : ""), Src = "", Engine = engine };
        }
        static async Task<Res> One(string engine, string text, string sl, string tl)
        {
            if (engine == "MyMemory") return await MyMemory(text, sl, tl);
            if (engine == "Lingva") return await Lingva(text, sl, tl);
            if (engine == "LibreTranslate") return await Libre(text, sl, tl);
            return await Google(text, sl, tl);
        }
        static async Task<Res> Google(string text, string sl, string tl)
        {
            string u = "https://translate.googleapis.com/translate_a/single?client=gtx&dt=t&dt=at&dt=rm&sl=" + sl + "&tl=" + tl + "&q=" + Uri.EscapeDataString(text);
            string json = await http.GetStringAsync(u);
            var root = ser.DeserializeObject(json) as object[];
            var sb = new StringBuilder();
            string src = ""; string rom = "";
            var alts = new List<string>();
            if (root != null)
            {
                var sents = root.Length > 0 ? root[0] as object[] : null;
                if (sents != null)
                    foreach (var s in sents)
                    {
                        var seg = s as object[]; if (seg == null) continue;
                        if (seg.Length > 0 && seg[0] != null) sb.Append(seg[0].ToString());
                        else if (seg.Length > 3 && seg[3] != null) rom += seg[3].ToString();   // romanization of the translation
                    }
                if (root.Length > 2 && root[2] != null) src = root[2].ToString();
                if (root.Length > 5)
                {
                    var ab = root[5] as object[];
                    if (ab != null)
                        foreach (var ob in ab)
                        {
                            var item = ob as object[]; if (item == null) continue;
                            foreach (var el in item)
                            {
                                var arr = el as object[]; if (arr == null) continue;
                                foreach (var a in arr)
                                {
                                    string s2 = a as string;
                                    if (s2 == null) { var ai = a as object[]; if (ai != null && ai.Length > 0) s2 = ai[0] as string; }
                                    if (!string.IsNullOrEmpty(s2) && !alts.Contains(s2)) alts.Add(s2);
                                }
                            }
                        }
                }
            }
            string main = sb.ToString();
            alts.Remove(main);
            if (alts.Count > 6) alts.RemoveRange(6, alts.Count - 6);
            if (rom == main) rom = "";
            return new Res { Text = main, Src = src, Alts = alts, Rom = rom };
        }
        static async Task<string> Detect(string text)
        {
            try { var r = await Google(text, "auto", "en"); return r.Src; } catch { return ""; }
        }
        static async Task<Res> MyMemory(string text, string sl, string tl)
        {
            if (sl == "auto") { string d = await Detect(text); sl = string.IsNullOrEmpty(d) ? "en" : d; }
            string u = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(text) + "&langpair=" + sl + "|" + tl;
            string json = await http.GetStringAsync(u);
            var root = ser.DeserializeObject(json) as Dictionary<string, object>;
            string t = "";
            if (root != null && root.ContainsKey("responseData"))
            {
                var rd = root["responseData"] as Dictionary<string, object>;
                if (rd != null && rd.ContainsKey("translatedText") && rd["translatedText"] != null) t = rd["translatedText"].ToString();
            }
            return new Res { Text = WebUtility.HtmlDecode(t), Src = sl };
        }
        static async Task<Res> Lingva(string text, string sl, string tl)
        {
            string u = "https://lingva.ml/api/v1/" + sl + "/" + tl + "/" + Uri.EscapeDataString(text);
            string json = await http.GetStringAsync(u);
            var root = ser.DeserializeObject(json) as Dictionary<string, object>;
            string t = ""; string src = "";
            if (root != null)
            {
                if (root.ContainsKey("translation") && root["translation"] != null) t = root["translation"].ToString();
                var info = root.ContainsKey("info") ? root["info"] as Dictionary<string, object> : null;
                if (info != null && info.ContainsKey("detectedSource") && info["detectedSource"] != null) src = info["detectedSource"].ToString();
            }
            return new Res { Text = t, Src = src };
        }
        static async Task<Res> Libre(string text, string sl, string tl)
        {
            var content = new FormUrlEncodedContent(new[] {
                new KeyValuePair<string,string>("q", text),
                new KeyValuePair<string,string>("source", sl),
                new KeyValuePair<string,string>("target", tl),
                new KeyValuePair<string,string>("format", "text")
            });
            var resp = await http.PostAsync("https://libretranslate.com/translate", content);
            string json = await resp.Content.ReadAsStringAsync();
            var root = ser.DeserializeObject(json) as Dictionary<string, object>;
            string t = ""; string src = "";
            if (root != null)
            {
                if (root.ContainsKey("translatedText") && root["translatedText"] != null) t = root["translatedText"].ToString();
                var dl = root.ContainsKey("detectedLanguage") ? root["detectedLanguage"] as Dictionary<string, object> : null;
                if (dl != null && dl.ContainsKey("language") && dl["language"] != null) src = dl["language"].ToString();
            }
            return new Res { Text = t, Src = src };
        }
    }

    // ---------------- text to speech (offline Windows SAPI voices) ----------------
    static class Tts
    {
        static System.Speech.Synthesis.SpeechSynthesizer synth;
        static System.Speech.Synthesis.SpeechSynthesizer S()
        {
            if (synth == null) { synth = new System.Speech.Synthesis.SpeechSynthesizer(); synth.SetOutputToDefaultAudioDevice(); }
            return synth;
        }
        public static string[] Voices()
        {
            var list = new List<string>();
            try { foreach (var v in S().GetInstalledVoices()) if (v.Enabled) list.Add(v.VoiceInfo.Name); } catch { }
            return list.ToArray();
        }
        public static void Speak(string text, string langCode)
        {
            if (!Config.S.EnableTts) return;
            try
            {
                string t = Config.S.TtsSkipEmoji ? StripEmoji(text) : text;
                if (string.IsNullOrEmpty(t) || t.Trim().Length == 0) return;
                var sy = S();
                sy.SpeakAsyncCancelAll();
                SelectVoice(sy, langCode);
                sy.SpeakAsync(t);
            }
            catch { }
        }
        public static void Preview(string voice)
        {
            try
            {
                var sy = S(); sy.SpeakAsyncCancelAll();
                if (!string.IsNullOrEmpty(voice)) { try { sy.SelectVoice(voice); } catch { } }
                sy.SpeakAsync("This is a voice preview. Hello!");
            }
            catch { }
        }
        static void SelectVoice(System.Speech.Synthesis.SpeechSynthesizer sy, string langCode)
        {
            if (!string.IsNullOrEmpty(Config.S.Voice)) { try { sy.SelectVoice(Config.S.Voice); return; } catch { } }
            if (!string.IsNullOrEmpty(langCode) && langCode != "auto")
            {
                string two = langCode.Length >= 2 ? langCode.Substring(0, 2).ToLower() : langCode.ToLower();
                try
                {
                    foreach (var v in sy.GetInstalledVoices())
                    {
                        if (!v.Enabled) continue;
                        var ci = v.VoiceInfo.Culture;
                        if (ci != null && ci.TwoLetterISOLanguageName.ToLower() == two) { sy.SelectVoice(v.VoiceInfo.Name); return; }
                    }
                }
                catch { }
            }
        }
        static string StripEmoji(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    int cp = char.ConvertToUtf32(c, s[i + 1]); i++;
                    if (cp >= 0x1F000 && cp <= 0x1FAFF) continue;
                    if (cp >= 0x1F1E6 && cp <= 0x1F1FF) continue;
                    sb.Append(char.ConvertFromUtf32(cp)); continue;
                }
                int u = (int)c;
                if (u == 0xFE0F || u == 0x200D) continue;
                if (u >= 0x2600 && u <= 0x27BF) continue;
                if (u >= 0x2B00 && u <= 0x2BFF) continue;
                sb.Append(c);
            }
            return sb.ToString();
        }
    }

    // ---------------- settings ----------------
    public class AppSettings
    {
        public string Engine { get; set; }
        public List<string> Pinned { get; set; }
        public bool RunOnStartup { get; set; }
        public bool KeepHistory { get; set; }
        public bool PasteOnClose { get; set; }
        public bool ShowVariants { get; set; }
        public bool AutoCopy { get; set; }
        public string SelFrom { get; set; }   // defaults for "text was selected" mode
        public string SelTo { get; set; }
        public string TypeFrom { get; set; }  // defaults for "nothing selected" mode
        public string TypeTo { get; set; }
        public int HotMods { get; set; }       // 1=Ctrl 2=Alt 4=Shift 8=Win
        public int HotScan { get; set; }       // scan code of the hotkey
        public string ThemeMode { get; set; }  // dark | light | system
        public double FontScale { get; set; }
        public bool EnableTts { get; set; }
        public bool EnableOcr { get; set; }
        public bool TtsSkipEmoji { get; set; }
        public string Voice { get; set; }       // "" = auto-pick by language
        public AppSettings()
        {
            Engine = "Google";
            Pinned = new List<string> { "Arabic", "English", "Spanish" };
            RunOnStartup = true;
            KeepHistory = false;
            PasteOnClose = false;
            ShowVariants = true;
            AutoCopy = true;
            SelFrom = "Auto-detect"; SelTo = "English";
            TypeFrom = "Auto-detect"; TypeTo = "Spanish";
            HotMods = 1; HotScan = 0x27;        // Ctrl + ';'
            ThemeMode = "dark"; FontScale = 1.0;
            EnableTts = true; EnableOcr = true; TtsSkipEmoji = true; Voice = "";
        }
    }
    static class Config
    {
        static string Path_() { return System.IO.Path.Combine(App.Dir, "settings.json"); }
        public static AppSettings S = new AppSettings();
        public static void Load()
        {
            try { if (File.Exists(Path_())) { var s = new JavaScriptSerializer().Deserialize<AppSettings>(File.ReadAllText(Path_())); if (s != null) S = s; } }
            catch { }
            if (S.Pinned == null) S.Pinned = new List<string>();
            if (string.IsNullOrEmpty(S.Engine)) S.Engine = "Google";
            if (string.IsNullOrEmpty(S.SelFrom)) S.SelFrom = "Auto-detect";
            if (string.IsNullOrEmpty(S.SelTo)) S.SelTo = "English";
            if (string.IsNullOrEmpty(S.TypeFrom)) S.TypeFrom = "Auto-detect";
            if (string.IsNullOrEmpty(S.TypeTo)) S.TypeTo = "Spanish";
            if (S.HotScan == 0) { S.HotScan = 0x27; S.HotMods = 1; }
            if (string.IsNullOrEmpty(S.ThemeMode)) S.ThemeMode = "dark";
            if (S.FontScale < 0.5 || S.FontScale > 2.0) S.FontScale = 1.0;
        }
        public static void Save()
        {
            try { File.WriteAllText(Path_(), new JavaScriptSerializer().Serialize(S)); } catch { }
        }
        public static void ApplyStartup()
        {
            try
            {
                var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (k == null) return;
                if (S.RunOnStartup) k.SetValue("QuickTranslator", "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue("QuickTranslator", false);
                k.Close();
            }
            catch { }
        }
    }

    static class App
    {
        public static string Dir { get { return System.IO.Path.GetDirectoryName(Application.ExecutablePath); } }
        public static Icon AppIcon;
        public static IntPtr LastForeground = IntPtr.Zero;   // window to paste back into on close
        public static bool HotkeyCapturing = false;          // pause the global hook while rebinding
        public static Action Rebuild;                        // rebuild windows after a theme/font change
        public static void LoadIcon()
        {
            try { string p = System.IO.Path.Combine(Dir, "Translator.ico"); if (File.Exists(p)) AppIcon = new Icon(p); } catch { }
            if (AppIcon == null) AppIcon = SystemIcons.Application;
        }
    }

    // ---------------- history ----------------
    public class HistoryEntry
    {
        public string Src { get; set; }
        public string Result { get; set; }
        public string From { get; set; }
        public string To { get; set; }
        public string Engine { get; set; }
        public string Time { get; set; }
        public bool Fav { get; set; }
    }
    static class Hist
    {
        static string Path_() { return System.IO.Path.Combine(App.Dir, "history.json"); }
        public static List<HistoryEntry> All = new List<HistoryEntry>();
        public static void Load()
        {
            try { if (File.Exists(Path_())) { var s = new JavaScriptSerializer(); s.MaxJsonLength = int.MaxValue; var l = s.Deserialize<List<HistoryEntry>>(File.ReadAllText(Path_())); if (l != null) All = l; } } catch { }
        }
        static void Save()
        {
            try { var s = new JavaScriptSerializer(); s.MaxJsonLength = int.MaxValue; File.WriteAllText(Path_(), s.Serialize(All)); } catch { }
        }
        public static void Add(string src, string result, string from, string to, string engine)
        {
            var e = new HistoryEntry();
            e.Src = src; e.Result = result; e.From = from; e.To = to; e.Engine = engine;
            e.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            All.Insert(0, e);
            if (All.Count > 500) All.RemoveRange(500, All.Count - 500);
            Save();
        }
        public static void Clear() { All.Clear(); Save(); }
        public static void ToggleFav(int i) { if (i >= 0 && i < All.Count) { All[i].Fav = !All[i].Fav; Save(); } }
        public static void ExportTo(string path)
        {
            try { var s = new JavaScriptSerializer(); s.MaxJsonLength = int.MaxValue; File.WriteAllText(path, s.Serialize(All)); } catch { }
        }
        public static int ImportFrom(string path)
        {
            try
            {
                var s = new JavaScriptSerializer(); s.MaxJsonLength = int.MaxValue;
                var l = s.Deserialize<List<HistoryEntry>>(File.ReadAllText(path));
                if (l == null) return 0;
                int added = 0;
                foreach (var e in l) { if (e != null) { All.Add(e); added++; } }
                if (All.Count > 2000) All.RemoveRange(2000, All.Count - 2000);
                Save();
                return added;
            }
            catch { return 0; }
        }
    }

    // ---------------- reusable modal language picker (used by Settings) ----------------
    static class LangDialog
    {
        public static string Pick(IWin32Window owner, string current, bool includeAuto)
        {
            var dlg = new Form();
            dlg.Text = "Select language"; dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.StartPosition = FormStartPosition.CenterParent; dlg.ClientSize = new Size(340, 460);
            dlg.MaximizeBox = false; dlg.MinimizeBox = false; dlg.ShowInTaskbar = false;
            dlg.BackColor = Theme.Bg; dlg.ForeColor = Theme.Text; dlg.Icon = App.AppIcon; dlg.KeyPreview = true;
            var search = new RInput(false); search.SetBounds(14, 14, 312, 40);
            var list = new DarkList(); list.SetBounds(14, 62, 312, 384);
            dlg.Controls.Add(search); dlg.Controls.Add(list);
            var rows = new List<string>();
            Action doBuild = delegate
            {
                string f = search.Box.Text.Trim().ToLower();
                list.BeginUpdate(); list.Items.Clear(); rows.Clear();
                var cands = new List<string>(); if (includeAuto) cands.Add(Lang.AUTO); cands.AddRange(Lang.Names);
                var pins = new List<string>(); foreach (var p in Config.S.Pinned) if (cands.Contains(p)) pins.Add(p);
                foreach (var n in pins) if (f == "" || n.ToLower().Contains(f)) { list.Items.Add("★   " + n); rows.Add(n); }
                if (pins.Count > 0 && f == "") { list.Items.Add(new string('─', 16)); rows.Add(""); }
                foreach (var n in cands) { if (pins.Contains(n)) continue; if (f == "" || n.ToLower().Contains(f)) { list.Items.Add("      " + n); rows.Add(n); } }
                list.EndUpdate();
                for (int i = 0; i < rows.Count; i++) if (rows[i] != "") { list.SelectedIndex = i; break; }
            };
            Action pick = delegate
            {
                int i = list.SelectedIndex; if (i < 0 || i >= rows.Count || rows[i] == "") return;
                dlg.Tag = rows[i]; dlg.DialogResult = DialogResult.OK; dlg.Close();
            };
            search.Box.TextChanged += delegate { doBuild(); };
            list.DoubleClick += delegate { pick(); };
            list.MouseDown += delegate (object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Right) return;
                int idx = list.IndexFromPoint(e.Location); if (idx < 0 || idx >= rows.Count || rows[idx] == "") return;
                string n = rows[idx]; if (Config.S.Pinned.Contains(n)) Config.S.Pinned.Remove(n); else Config.S.Pinned.Add(n);
                Config.Save(); doBuild();
            };
            search.Box.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { pick(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Down) { int n = list.SelectedIndex + 1; while (n < rows.Count && rows[n] == "") n++; if (n < rows.Count) list.SelectedIndex = n; e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Up) { int n = list.SelectedIndex - 1; while (n >= 0 && rows[n] == "") n--; if (n >= 0) list.SelectedIndex = n; e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Escape) { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); }
            };
            dlg.HandleCreated += delegate { Native.DarkTitle(dlg.Handle); };
            doBuild();
            for (int i = 0; i < rows.Count; i++) if (rows[i] == current) { list.SelectedIndex = i; break; }
            search.Box.Select();
            var r = dlg.ShowDialog(owner);
            string val = r == DialogResult.OK ? (string)dlg.Tag : null;
            dlg.Dispose();
            return val;
        }
    }

    // ---------------- hotkey rebinding ----------------
    static class HotkeyDialog
    {
        public static bool Pick(IWin32Window owner, out int mods, out int scan)
        {
            mods = 0; scan = 0;
            int cm = 0, cs = 0;
            var dlg = new Form();
            dlg.Text = "Set shortcut"; dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.StartPosition = FormStartPosition.CenterParent; dlg.ClientSize = new Size(340, 140);
            dlg.MaximizeBox = false; dlg.MinimizeBox = false; dlg.ShowInTaskbar = false; dlg.KeyPreview = true;
            dlg.BackColor = Theme.Bg; dlg.ForeColor = Theme.Text; dlg.Icon = App.AppIcon;
            var info = new Label(); info.Text = "Press a key combination (e.g. Ctrl + ;)"; info.SetBounds(18, 16, 304, 22); info.ForeColor = Theme.Muted; info.Font = Theme.F(9.5f);
            var combo = new Label(); combo.SetBounds(18, 42, 304, 34); combo.ForeColor = Theme.Text; combo.Font = Theme.FS(14f);
            combo.Text = HotkeyLabel(Config.S.HotMods, Config.S.HotScan);
            dlg.Controls.Add(info); dlg.Controls.Add(combo);
            dlg.KeyDown += delegate (object s, KeyEventArgs e)
            {
                int vk = e.KeyValue;
                if (vk == 17 || vk == 16 || vk == 18 || vk == 91 || vk == 92) { e.SuppressKeyPress = true; return; } // modifier alone
                if (e.KeyCode == Keys.Escape) { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); return; }
                int m = 0; if (e.Control) m |= 1; if (e.Alt) m |= 2; if (e.Shift) m |= 4;
                cm = m; cs = (int)Native.MapVirtualKey((uint)vk, 0);   // VK -> scan code
                combo.Text = HotkeyLabel(cm, cs);
                e.SuppressKeyPress = true;
            };
            var ok = new RButton(); ok.Text = "Save"; ok.Fill = Theme.Accent; ok.Hover = Theme.AccentH; ok.SetBounds(168, 96, 72, 30);
            var cn = new RButton(); cn.Text = "Cancel"; cn.Fill = Theme.Card; cn.Hover = Theme.Card2; cn.SetBounds(250, 96, 72, 30);
            ok.Click += delegate { dlg.DialogResult = DialogResult.OK; dlg.Close(); };
            cn.Click += delegate { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };
            dlg.Controls.Add(ok); dlg.Controls.Add(cn);
            dlg.HandleCreated += delegate { Native.DarkTitle(dlg.Handle); };
            var r = dlg.ShowDialog(owner); dlg.Dispose();
            if (r == DialogResult.OK && cs != 0) { mods = cm; scan = cs; return true; }
            return false;
        }
        public static string HotkeyLabel(int mods, int scan)
        {
            var sb = new StringBuilder();
            if ((mods & 1) != 0) sb.Append("Ctrl + ");
            if ((mods & 2) != 0) sb.Append("Alt + ");
            if ((mods & 4) != 0) sb.Append("Shift + ");
            sb.Append(KeyName(Native.MapVirtualKey((uint)scan, 1)));   // scan -> VK
            return sb.ToString();
        }
        static string KeyName(uint vk)
        {
            switch ((int)vk)
            {
                case 0xBA: return ";"; case 0xBB: return "="; case 0xBC: return ","; case 0xBD: return "-";
                case 0xBE: return "."; case 0xBF: return "/"; case 0xC0: return "`"; case 0xDB: return "[";
                case 0xDC: return "\\"; case 0xDD: return "]"; case 0xDE: return "'";
            }
            if (vk == 0) return "?";
            try { return ((Keys)vk).ToString(); } catch { return "?"; }
        }
    }

    // ---------------- simple list picker (used by the voice chooser) ----------------
    static class ListDialog
    {
        public static string Pick(IWin32Window owner, string title, string[] options, string current)
        {
            var dlg = new Form();
            dlg.Text = title; dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.StartPosition = FormStartPosition.CenterParent; dlg.ClientSize = new Size(360, 440);
            dlg.MaximizeBox = false; dlg.MinimizeBox = false; dlg.ShowInTaskbar = false; dlg.KeyPreview = true;
            dlg.BackColor = Theme.Bg; dlg.ForeColor = Theme.Text; dlg.Icon = App.AppIcon;
            var list = new DarkList(); list.SetBounds(14, 14, 332, 412);
            foreach (var o in options) list.Items.Add(o);
            int ci = Array.IndexOf(options, current); if (ci >= 0) list.SelectedIndex = ci;
            dlg.Controls.Add(list);
            Action pick = delegate { if (list.SelectedIndex >= 0) { dlg.Tag = options[list.SelectedIndex]; dlg.DialogResult = DialogResult.OK; dlg.Close(); } };
            list.DoubleClick += delegate { pick(); };
            dlg.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) pick(); else if (e.KeyCode == Keys.Escape) { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); } };
            dlg.HandleCreated += delegate { Native.DarkTitle(dlg.Handle); };
            var r = dlg.ShowDialog(owner); string val = r == DialogResult.OK ? (string)dlg.Tag : null; dlg.Dispose(); return val;
        }
    }

    // ---------------- translator panel (reused by both windows) ----------------
    class TranslatorPanel : Panel
    {
        public string From = Lang.AUTO;
        public string To = "Spanish";
        string detected = "";
        CancellationTokenSource cts;

        RInput src, res;
        RButton bFrom, bSwap, bTo, bTranslate, bCopy;
        Label lblStatus, romLbl;
        LoadingBar bar;
        TableLayoutPanel grid;
        FlowLayoutPanel variants;
        int varRow, romRow;
        public bool CloseOnCopy = true;
        public Action OnCloseRequested;

        // picker overlay
        Panel picker;
        RInput pickSearch;
        DarkList pickList;
        Label pickTitle;
        List<string> rows = new List<string>();
        string pickMode = "to";

        public TranslatorPanel(bool compact)
        {
            BackColor = Theme.Bg;
            Padding = new Padding(compact ? 12 : 16);
            BuildUI(compact);
            BuildPicker();
        }

        void BuildUI(bool compact)
        {
            grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill; grid.BackColor = Theme.Bg; grid.ColumnCount = 1;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));   // 0 src header
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 42));    // 1 src (expands)
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));   // 2 langs
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));   // 3 actions
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));   // 4 result header
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 58));    // 5 res (expands)
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));    // 6 romanization
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));    // 7 variants
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 6));    // 8 bar
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));   // 9 status

            src = new RInput(true); src.Dock = DockStyle.Fill; src.Margin = new Padding(0, 2, 0, 2); src.Box.KeyDown += SrcKey;
            res = new RInput(true); res.Dock = DockStyle.Fill; res.Box.ReadOnly = true; res.Margin = new Padding(0, 2, 0, 2);

            grid.Controls.Add(SrcHeader(), 0, 0);
            grid.Controls.Add(src, 0, 1);

            // langs: from | swap | to
            var lg = new TableLayoutPanel(); lg.Dock = DockStyle.Fill; lg.BackColor = Theme.Bg; lg.ColumnCount = 3; lg.RowCount = 1;
            lg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            lg.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
            lg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            bFrom = Btn("", Theme.Card, false); bFrom.Align = ContentAlignment.MiddleLeft; bFrom.Margin = new Padding(0, 4, 4, 4);
            bTo = Btn("", Theme.Card, false); bTo.Align = ContentAlignment.MiddleLeft; bTo.Margin = new Padding(4, 4, 0, 4);
            bSwap = Btn("⇄", Theme.Card, false); bSwap.Font = Theme.F(13f); bSwap.Margin = new Padding(0, 4, 0, 4);
            bFrom.Click += delegate { OpenPicker("from"); };
            bTo.Click += delegate { OpenPicker("to"); };
            bSwap.Click += delegate { Swap(); };
            lg.Controls.Add(bFrom, 0, 0); lg.Controls.Add(bSwap, 1, 0); lg.Controls.Add(bTo, 2, 0);
            grid.Controls.Add(lg, 0, 2);

            // actions
            var ac = new TableLayoutPanel(); ac.Dock = DockStyle.Fill; ac.BackColor = Theme.Bg; ac.ColumnCount = 2; ac.RowCount = 1;
            ac.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            ac.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            bTranslate = Btn("Translate", Theme.Accent, true); bTranslate.Hover = Theme.AccentH; bTranslate.Font = Theme.FS(10f); bTranslate.Margin = new Padding(0, 3, 4, 3);
            bCopy = Btn("Copy && Close", Theme.Card, false); bCopy.Margin = new Padding(4, 3, 0, 3);
            bTranslate.Click += delegate { DoTranslate(); };
            bCopy.Click += delegate { CopyAndClose(); };
            ac.Controls.Add(bTranslate, 0, 0); ac.Controls.Add(bCopy, 1, 0);
            grid.Controls.Add(ac, 0, 3);

            grid.Controls.Add(ResHeader(), 0, 4);
            grid.Controls.Add(res, 0, 5);

            romLbl = new Label(); romLbl.Dock = DockStyle.Fill; romLbl.ForeColor = Theme.Muted; romLbl.Font = Theme.F(9f);
            romLbl.TextAlign = ContentAlignment.MiddleLeft; romLbl.AutoEllipsis = true;
            grid.Controls.Add(romLbl, 0, 6); romRow = 6;

            variants = new FlowLayoutPanel(); variants.Dock = DockStyle.Fill; variants.BackColor = Theme.Bg;
            variants.WrapContents = false; variants.AutoScroll = true; variants.Margin = new Padding(0); variants.Padding = new Padding(0);
            grid.Controls.Add(variants, 0, 7); varRow = 7;

            bar = new LoadingBar(); bar.Dock = DockStyle.Fill; bar.Margin = new Padding(2, 1, 2, 1);
            grid.Controls.Add(bar, 0, 8);

            lblStatus = new Label(); lblStatus.Text = "Ready."; lblStatus.Dock = DockStyle.Fill; lblStatus.ForeColor = Theme.Muted;
            lblStatus.Font = Theme.F(8.5f); lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            grid.Controls.Add(lblStatus, 0, 9);

            Controls.Add(grid);
            UpdateLangButtons();
        }

        Label Lbl(string t)
        {
            var l = new Label(); l.Text = t; l.Dock = DockStyle.Fill; l.ForeColor = Theme.Muted;
            l.Font = Theme.F(8.5f); l.TextAlign = ContentAlignment.MiddleLeft; return l;
        }
        RButton IconBtn(string glyph, string tip)
        {
            if (string.IsNullOrEmpty(glyph)) glyph = (tip != null && tip.Contains("OCR")) ? "" : "";
            var b = new RButton(); b.Text = glyph; b.Fill = Theme.Bg; b.Hover = Theme.Card2; b.Radius = 6;
            b.Width = 28; b.Height = 22; b.Margin = new Padding(2, 1, 0, 1);
            b.Font = new Font("Segoe MDL2 Assets", 10f * Theme.Scale);
            if (tip != null) { var tt = new ToolTip(); tt.SetToolTip(b, tip); }
            return b;
        }
        Control SrcHeader()
        {
            var btns = new List<Control>();
            if (Config.S.EnableTts) { var spk = IconBtn("", "Speak the source"); spk.Click += delegate { Tts.Speak(src.Box.Text, From == Lang.AUTO ? (detected != "" ? detected : "en") : Lang.Of(From)); }; btns.Add(spk); }
            if (Config.S.EnableOcr) { var ocr = IconBtn("", "Capture text from screen (OCR)"); ocr.Click += delegate { DoOcr(); }; btns.Add(ocr); }
            return HeaderRow("Text", btns);
        }
        Control ResHeader()
        {
            var btns = new List<Control>();
            if (Config.S.EnableTts) { var spk = IconBtn("", "Speak the translation"); spk.Click += delegate { Tts.Speak(res.Box.Text, Lang.Of(To)); }; btns.Add(spk); }
            return HeaderRow("Translation", btns);
        }
        Control HeaderRow(string label, List<Control> btns)
        {
            var t = new TableLayoutPanel(); t.Dock = DockStyle.Fill; t.BackColor = Theme.Bg; t.RowCount = 1; t.Margin = new Padding(0);
            t.ColumnCount = 1 + btns.Count;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < btns.Count; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32));
            t.Controls.Add(Lbl(label), 0, 0);
            for (int i = 0; i < btns.Count; i++) t.Controls.Add(btns[i], i + 1, 0);
            return t;
        }
        public async void DoOcr()
        {
            var form = TopLevelControl as Form;
            try { if (form != null) form.Hide(); } catch { }
            Application.DoEvents(); Thread.Sleep(140);   // hide our window before the screenshot is grabbed
            Bitmap cap = null;
            IntPtr prevDpi = Native.DpiAwareOn();   // capture & show the snip at true physical resolution (no zoom/blur)
            try { using (var snip = new SnipForm()) { snip.ShowDialog(); cap = snip.Cropped; } }
            finally { Native.DpiAwareOff(prevDpi); }
            try { if (form != null) { form.Show(); Native.ShowFront(form.Handle); } } catch { }
            if (cap == null) return;
            try   // OCR is far more reliable on larger images -> upscale small crops
            {
                if (cap.Height < 280)
                {
                    double scale = cap.Height < 140 ? 3.0 : 2.0;
                    var big = new Bitmap((int)(cap.Width * scale), (int)(cap.Height * scale));
                    using (var g = Graphics.FromImage(big)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(cap, 0, 0, big.Width, big.Height); }
                    cap.Dispose(); cap = big;
                }
            }
            catch { }
            lblStatus.Text = "Reading text…"; bar.Start();
            string text = "";
            Bitmap b2 = cap;
            try { text = await Task.Run(new Func<string>(delegate { return Ocr.Recognize(b2); })); } catch (Exception ex) { bar.Stop(); lblStatus.Text = "OCR unavailable: " + ex.Message; cap.Dispose(); return; }
            cap.Dispose(); bar.Stop();
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) { lblStatus.Text = "No text found in selection."; return; }
            src.Box.Text = text.Trim();
            DoTranslate();
        }
        RButton Btn(string t, Color fill, bool accent)
        {
            var b = new RButton(); b.Text = t; b.Fill = fill; b.Dock = DockStyle.Fill;
            b.Hover = accent ? Theme.AccentH : Theme.Card2; b.ForeColor = Theme.Text; return b;
        }

        // ----- picker overlay -----
        void BuildPicker()
        {
            picker = new Panel(); picker.BackColor = Theme.Bg; picker.Visible = false; picker.Dock = DockStyle.Fill; picker.Padding = new Padding(16, 14, 16, 14);
            var g = new TableLayoutPanel(); g.Dock = DockStyle.Fill; g.BackColor = Theme.Bg; g.ColumnCount = 1;
            g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            g.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            g.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            g.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            g.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            pickTitle = new Label(); pickTitle.Text = "Translate to"; pickTitle.Dock = DockStyle.Fill; pickTitle.ForeColor = Theme.Text;
            pickTitle.Font = Theme.FS(11f); pickTitle.TextAlign = ContentAlignment.MiddleLeft;
            pickSearch = new RInput(false); pickSearch.Dock = DockStyle.Fill; pickSearch.Margin = new Padding(0, 2, 0, 2);
            pickSearch.Box.TextChanged += delegate { BuildList(pickSearch.Box.Text); };
            pickSearch.Box.KeyDown += PickKey;
            var hint = new Label(); hint.Text = "Enter to pick   ·   right-click to pin"; hint.Dock = DockStyle.Fill;
            hint.ForeColor = Theme.Muted; hint.Font = Theme.F(8f); hint.TextAlign = ContentAlignment.MiddleLeft;
            pickList = new DarkList(); pickList.Dock = DockStyle.Fill;
            pickList.DoubleClick += delegate { PickCurrent(); };
            pickList.MouseDown += PickMouse;
            g.Controls.Add(pickTitle, 0, 0); g.Controls.Add(pickSearch, 0, 1); g.Controls.Add(hint, 0, 2); g.Controls.Add(pickList, 0, 3);
            picker.Controls.Add(g);
            Controls.Add(picker);
        }
        void OpenPicker(string mode)
        {
            pickMode = mode;
            pickTitle.Text = mode == "from" ? "Translate from" : "Translate to";
            picker.Visible = true; picker.BringToFront();
            pickSearch.Box.Text = "";
            BuildList("");
            string cur = mode == "from" ? From : To;
            for (int i = 0; i < rows.Count; i++) if (rows[i] == cur) { pickList.SelectedIndex = i; break; }
            pickSearch.Box.Focus();
        }
        void ClosePicker() { picker.Visible = false; src.Box.Focus(); }
        void BuildList(string filter)
        {
            string f = (filter ?? "").Trim().ToLower();
            pickList.BeginUpdate(); pickList.Items.Clear(); rows.Clear();
            var cands = new List<string>();
            if (pickMode == "from") cands.Add(Lang.AUTO);
            cands.AddRange(Lang.Names);
            var pins = new List<string>();
            foreach (string p in Config.S.Pinned) if (cands.Contains(p)) pins.Add(p);
            foreach (string n in pins)
                if (f == "" || n.ToLower().Contains(f)) { pickList.Items.Add("★   " + n); rows.Add(n); }
            if (pins.Count > 0 && f == "") { pickList.Items.Add(new string('─', 16)); rows.Add(""); }
            foreach (string n in cands)
            {
                if (pins.Contains(n)) continue;
                if (f == "" || n.ToLower().Contains(f)) { pickList.Items.Add("      " + n); rows.Add(n); }
            }
            pickList.EndUpdate();
            for (int i = 0; i < rows.Count; i++) if (rows[i] != "") { pickList.SelectedIndex = i; break; }
        }
        void MovePick(int d)
        {
            if (pickList.Items.Count == 0) return;
            int n = pickList.SelectedIndex + d;
            while (n >= 0 && n < rows.Count && rows[n] == "") n += d;
            if (n >= 0 && n < rows.Count) pickList.SelectedIndex = n;
        }
        void PickCurrent()
        {
            int i = pickList.SelectedIndex;
            if (i < 0 || i >= rows.Count || rows[i] == "") return;
            if (pickMode == "from") From = rows[i]; else To = rows[i];
            UpdateLangButtons(); ClosePicker(); DoTranslate();
        }
        void PickKey(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { PickCurrent(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Down) { MovePick(1); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Up) { MovePick(-1); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape) { ClosePicker(); e.SuppressKeyPress = true; }
        }
        void PickMouse(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            int idx = pickList.IndexFromPoint(e.Location);
            if (idx < 0 || idx >= rows.Count || rows[idx] == "") return;
            string n = rows[idx];
            if (Config.S.Pinned.Contains(n)) Config.S.Pinned.Remove(n); else Config.S.Pinned.Add(n);
            Config.Save(); BuildList(pickSearch.Box.Text);
        }

        // ----- behaviour -----
        void UpdateLangButtons()
        {
            bFrom.Text = "  " + From + "        ▾";
            bTo.Text = "  " + To + "        ▾";
        }
        void SrcKey(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift) { DoTranslate(); e.SuppressKeyPress = true; }
        }
        public void SetLangs(string from, string to) { From = from; To = to; UpdateLangButtons(); }
        public void Load(string srcText, string from, string to)
        {
            if (!string.IsNullOrEmpty(from)) From = from;
            if (!string.IsNullOrEmpty(to)) To = to;
            UpdateLangButtons(); src.Box.Text = srcText == null ? "" : srcText; DoTranslate();
        }
        public void SetSource(string t) { src.Box.Text = t == null ? "" : t; }
        public void ClearAll() { src.Box.Clear(); res.Box.Clear(); lblStatus.Text = "Type text, press Enter."; bar.Stop(); ShowVariants(null); ShowRom(null); }
        public void FocusSource() { src.Box.Focus(); }
        public void CopyResult()
        {
            string t = res.Box.Text;
            if (t != null && t.Trim().Length > 0) Clip.SetText(t);
        }
        void CopyAndClose()
        {
            CopyResult();
            if (OnCloseRequested != null) OnCloseRequested();
        }
        public void Swap()
        {
            if (From == Lang.AUTO)
            {
                string nf = To;
                string nt = Lang.NameOfCode(detected);
                if (nt == null || nt == nf || nt == Lang.AUTO)
                {
                    nt = null;
                    foreach (string p in Config.S.Pinned) if (p != nf && p != Lang.AUTO) { nt = p; break; }
                    if (nt == null) nt = "Spanish";
                }
                From = nf; To = nt;
            }
            else { string tmp = From; From = To; To = tmp; }
            UpdateLangButtons(); DoTranslate();
        }
        public async void DoTranslate()
        {
            if (picker.Visible) ClosePicker();
            string text = src.Box.Text;
            if (cts != null) { cts.Cancel(); }
            ShowVariants(null); ShowRom(null);
            if (text == null || text.Trim().Length == 0) { res.Box.Clear(); lblStatus.Text = "Ready."; bar.Stop(); return; }
            lblStatus.Text = "Translating…"; bar.Start();
            var myCts = new CancellationTokenSource(); cts = myCts;
            string eng = Config.S.Engine; string sl = Lang.Of(From); string tl = Lang.Of(To);
            Res r = await Engine.Translate(eng, text, sl, tl);
            if (myCts.IsCancellationRequested) return;
            cts = null; bar.Stop();
            if (!string.IsNullOrEmpty(r.Engine)) eng = r.Engine;   // show the engine that actually answered (fallback)
            res.Box.Text = r.Text;
            ShowRom(r.Rom);
            if (!string.IsNullOrEmpty(r.Src)) detected = r.Src;
            if (From == Lang.AUTO && !string.IsNullOrEmpty(r.Src))
            {
                string dn = Lang.NameOfCode(r.Src); if (dn == null) dn = r.Src;
                lblStatus.Text = "Detected: " + dn + "   →   " + To + "    (" + eng + ")";
            }
            else lblStatus.Text = From + "   →   " + To + "    (" + eng + ")";
            ShowVariants(r.Alts);
            bool ok = !string.IsNullOrEmpty(r.Text) && r.Text[0] != '[';
            if (ok && Config.S.AutoCopy) Clip.SetText(r.Text);
            if (ok && Config.S.KeepHistory) Hist.Add(text, r.Text, From, To, eng);
        }
        void ShowRom(string rom)
        {
            if (romLbl == null) return;
            if (string.IsNullOrEmpty(rom)) { romLbl.Text = ""; grid.RowStyles[romRow].Height = 0; return; }
            romLbl.Text = rom;
            grid.RowStyles[romRow].Height = 22;
        }
        void ShowVariants(List<string> alts)
        {
            if (variants == null) return;
            variants.Controls.Clear();
            if (alts == null || alts.Count == 0 || !Config.S.ShowVariants) { grid.RowStyles[varRow].Height = 0; return; }
            foreach (var a in alts)
            {
                var chip = new RButton(); chip.Text = a; chip.Fill = Theme.Card2; chip.Hover = Theme.Accent; chip.Radius = 13;
                chip.Font = Theme.F(9f); chip.AutoSize = false; chip.Height = 26;
                chip.Width = Math.Min(TextRenderer.MeasureText(a, chip.Font).Width + 22, 260);
                chip.Margin = new Padding(0, 2, 6, 2);
                string val = a;
                chip.Click += delegate { res.Box.Text = val; };
                variants.Controls.Add(chip);
            }
            grid.RowStyles[varRow].Height = 34;
        }
    }

    static class Clip
    {
        public static void SetText(string t)
        {
            for (int i = 0; i < 5; i++) { try { Clipboard.SetText(t); return; } catch { Thread.Sleep(30); } }
        }
        public static string GetText()
        {
            for (int i = 0; i < 5; i++) { try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; } catch { Thread.Sleep(30); } }
            return "";
        }
        public static void Clear() { try { Clipboard.Clear(); } catch { } }
    }

    // ---------------- screen-region capture (for OCR) ----------------
    // Freeze-frame snip: grab the whole screen FIRST, let the user select on the
    // frozen image, then crop from that grab. Avoids capturing a dimming overlay.
    class SnipForm : Form
    {
        Bitmap shot; Point start; Rectangle sel; bool dragging;
        public Bitmap Cropped;   // cropped result (caller owns/disposes)
        public SnipForm()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual; Bounds = SystemInformation.VirtualScreen;
            var vs = Bounds;
            shot = new Bitmap(vs.Width, vs.Height);
            try { using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(vs.Location, Point.Empty, vs.Size); } catch { }
            Cursor = Cursors.Cross; DoubleBuffered = true;
            KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { Cropped = null; Close(); } };
        }
        protected override void OnMouseDown(MouseEventArgs e) { start = e.Location; dragging = true; }
        protected override void OnMouseMove(MouseEventArgs e) { if (dragging) { sel = Rect(start, e.Location); Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            dragging = false; var s = Rect(start, e.Location);
            if (s.Width >= 5 && s.Height >= 5 && shot != null)
            {
                try
                {
                    Cropped = new Bitmap(s.Width, s.Height);
                    using (var g = Graphics.FromImage(Cropped)) g.DrawImage(shot, new Rectangle(0, 0, s.Width, s.Height), s, GraphicsUnit.Pixel);
                }
                catch { Cropped = null; }
            }
            Close();
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            if (shot != null) g.DrawImageUnscaled(shot, 0, 0);
            using (var dim = new SolidBrush(Color.FromArgb(110, 0, 0, 0))) g.FillRectangle(dim, ClientRectangle);
            if (sel.Width > 0 && shot != null)
            {
                g.DrawImage(shot, sel, sel, GraphicsUnit.Pixel);   // un-dim the selection
                using (var p = new Pen(Color.FromArgb(59, 130, 246), 2)) g.DrawRectangle(p, sel);
            }
        }
        protected override void Dispose(bool disposing) { try { if (shot != null) shot.Dispose(); } catch { } base.Dispose(disposing); }
        static Rectangle Rect(Point a, Point b) { return new Rectangle(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)); }
    }

    static class Ocr
    {
        // Block on a WinRT IAsyncOperation using only per-namespace projections
        // (avoids System.Runtime.WindowsRuntime, which needs the union Windows.winmd).
        static T Await<T>(Windows.Foundation.IAsyncOperation<T> op)
        {
            var done = new System.Threading.ManualResetEventSlim(false);
            op.Completed = delegate (Windows.Foundation.IAsyncOperation<T> a, Windows.Foundation.AsyncStatus s) { done.Set(); };
            done.Wait();
            return op.GetResults();
        }
        // Synchronous; call via Task.Run so the UI stays responsive.
        public static string Recognize(Bitmap bmp)
        {
            byte[] png;
            using (var ms = new MemoryStream()) { bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png); png = ms.ToArray(); }
            var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var dw = new Windows.Storage.Streams.DataWriter(ras);
            dw.WriteBytes(png);
            Await<uint>(dw.StoreAsync());
            dw.DetachStream();
            ras.Seek(0);
            var dec = Await<Windows.Graphics.Imaging.BitmapDecoder>(Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras));
            var sb = Await<Windows.Graphics.Imaging.SoftwareBitmap>(dec.GetSoftwareBitmapAsync());
            var eng = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            if (eng == null) return "";
            var r = Await<Windows.Media.Ocr.OcrResult>(eng.RecognizeAsync(sb));
            return r == null ? "" : r.Text;
        }
    }

    // ---------------- quick window ----------------
    class QuickForm : Form
    {
        TranslatorPanel panel;
        bool typeMode;
        public QuickForm()
        {
            Text = "Quick Translator"; Icon = App.AppIcon;
            FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = false;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(430, 360); MinimumSize = new Size(380, 300);
            BackColor = Theme.Bg; ForeColor = Theme.Text; ShowInTaskbar = true; TopMost = true; KeyPreview = true;
            panel = new TranslatorPanel(true); panel.Dock = DockStyle.Fill; panel.CloseOnCopy = true;
            panel.OnCloseRequested = delegate { CloseAndPaste(); };
            Controls.Add(panel);
            KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { Hide(); e.SuppressKeyPress = true; }
                else if (e.Control && e.KeyCode == Keys.Return) { panel.CopyResult(); CloseAndPaste(); e.SuppressKeyPress = true; }
            };
            HandleCreated += delegate { Native.DarkTitle(Handle); };
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            base.OnFormClosing(e);
        }
        public void ShowWith(string sel)
        {
            bool has = sel != null && sel.Trim().Length > 0;
            typeMode = !has;   // only auto-paste on close when the user typed (not when they translated a selection)
            if (has) { panel.SetLangs(Config.S.SelFrom, Config.S.SelTo); panel.SetSource(sel); }
            else { panel.SetLangs(Config.S.TypeFrom, Config.S.TypeTo); panel.ClearAll(); }
            ShowFront();
            if (has) panel.DoTranslate(); else panel.FocusSource();
        }
        public void DoCopyClose() { panel.CopyResult(); CloseAndPaste(); }
        void CloseAndPaste()
        {
            bool paste = Config.S.PasteOnClose && typeMode;
            Hide();
            if (paste) Native.PasteTo(App.LastForeground);
        }
        public void Prewarm()
        {
            // realize handles + JIT the paint/layout code off-screen so the first
            // real open is instant (no cold-start lag).
            Location = new Point(-32000, -32000);
            Show(); Application.DoEvents(); Hide();
        }
        void ShowFront()
        {
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + (wa.Height - Height) / 2);
            Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Native.ShowFront(Handle); Activate(); BringToFront(); TopMost = true;
        }
    }

    // ---------------- main / settings window ----------------
    class MainForm : Form
    {
        TranslatorPanel panel;
        Panel content, settings, history;
        RButton navT, navH, navS;
        DarkList histList;
        RInput histSearch;
        List<int> histMap = new List<int>();
        bool favOnly = false;
        RButton[] engBtns;

        public MainForm()
        {
            Text = "Quick Translator"; Icon = App.AppIcon;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(740, 560); MinimumSize = new Size(640, 480);
            BackColor = Theme.Bg; ForeColor = Theme.Text; KeyPreview = true;

            var side = new Panel(); side.Dock = DockStyle.Left; side.Width = 150; side.BackColor = Theme.Card;
            navT = NavBtn("Translate"); navT.Top = 16;
            navH = NavBtn("History"); navH.Top = 60;
            navS = NavBtn("Settings"); navS.Top = 104;
            navT.Click += delegate { ShowTab("t"); };
            navH.Click += delegate { ShowTab("h"); };
            navS.Click += delegate { ShowTab("s"); };
            side.Controls.Add(navT); side.Controls.Add(navH); side.Controls.Add(navS);

            content = new Panel(); content.Dock = DockStyle.Fill; content.BackColor = Theme.Bg;
            panel = new TranslatorPanel(false); panel.Dock = DockStyle.Fill; panel.CloseOnCopy = false;
            panel.OnCloseRequested = delegate { Hide(); };
            history = BuildHistory(); history.Dock = DockStyle.Fill; history.Visible = false;
            settings = BuildSettings(); settings.Dock = DockStyle.Fill; settings.Visible = false;
            content.Controls.Add(panel); content.Controls.Add(history); content.Controls.Add(settings);

            Controls.Add(content); Controls.Add(side);
            ShowTab("t");
            KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Hide(); };
            HandleCreated += delegate { Native.DarkTitle(Handle); };
        }
        RButton NavBtn(string t)
        {
            var b = new RButton(); b.Text = "   " + t; b.Fill = Theme.Card; b.Hover = Theme.Card2; b.Align = ContentAlignment.MiddleLeft;
            b.Left = 10; b.Width = 130; b.Height = 38; b.Font = Theme.F(10.5f); return b;
        }
        void ShowTab(string which)
        {
            panel.Visible = which == "t"; history.Visible = which == "h"; settings.Visible = which == "s";
            navT.Fill = which == "t" ? Theme.Card2 : Theme.Card;
            navH.Fill = which == "h" ? Theme.Card2 : Theme.Card;
            navS.Fill = which == "s" ? Theme.Card2 : Theme.Card;
            navT.Invalidate(); navH.Invalidate(); navS.Invalidate();
            if (which == "t") panel.BringToFront();
            else if (which == "h") { RefreshHistory(); history.BringToFront(); }
            else settings.BringToFront();
        }

        // ----- history tab -----
        Panel BuildHistory()
        {
            var p = new Panel(); p.BackColor = Theme.Bg;
            var tl = new TableLayoutPanel(); tl.Dock = DockStyle.Fill; tl.BackColor = Theme.Bg; tl.ColumnCount = 1; tl.Padding = new Padding(18, 14, 18, 14);
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tl.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            tl.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            tl.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            tl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var hb = new TableLayoutPanel(); hb.Dock = DockStyle.Fill; hb.BackColor = Theme.Bg; hb.ColumnCount = 5; hb.RowCount = 1;
            hb.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            hb.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
            hb.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            hb.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            hb.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            var h = new Label(); h.Text = "History"; h.Dock = DockStyle.Fill; h.Font = Theme.FS(13f); h.ForeColor = Theme.Text; h.TextAlign = ContentAlignment.MiddleLeft;
            var favBtn = new RButton(); favBtn.Text = "Favs"; favBtn.Dock = DockStyle.Fill; favBtn.Margin = new Padding(2, 4, 2, 4); favBtn.Fill = Theme.Card; favBtn.Hover = Theme.Card2; favBtn.Font = Theme.F(9f);
            favBtn.Click += delegate { favOnly = !favOnly; favBtn.Fill = favOnly ? Theme.Accent : Theme.Card; favBtn.Hover = favOnly ? Theme.AccentH : Theme.Card2; favBtn.Invalidate(); RefreshHistory(); };
            var exp = new RButton(); exp.Text = "Export"; exp.Dock = DockStyle.Fill; exp.Margin = new Padding(2, 4, 2, 4); exp.Fill = Theme.Card; exp.Hover = Theme.Card2; exp.Font = Theme.F(9f);
            exp.Click += delegate { var d = new SaveFileDialog(); d.Filter = "JSON|*.json"; d.FileName = "translator-history.json"; if (d.ShowDialog() == DialogResult.OK) Hist.ExportTo(d.FileName); };
            var imp = new RButton(); imp.Text = "Import"; imp.Dock = DockStyle.Fill; imp.Margin = new Padding(2, 4, 2, 4); imp.Fill = Theme.Card; imp.Hover = Theme.Card2; imp.Font = Theme.F(9f);
            imp.Click += delegate { var d = new OpenFileDialog(); d.Filter = "JSON|*.json"; if (d.ShowDialog() == DialogResult.OK) { Hist.ImportFrom(d.FileName); RefreshHistory(); } };
            var clr = new RButton(); clr.Text = "Clear"; clr.Dock = DockStyle.Fill; clr.Margin = new Padding(2, 4, 0, 4); clr.Fill = Theme.Card; clr.Hover = Theme.Card2; clr.Font = Theme.F(9f);
            clr.Click += delegate { Hist.Clear(); RefreshHistory(); };
            hb.Controls.Add(h, 0, 0); hb.Controls.Add(favBtn, 1, 0); hb.Controls.Add(exp, 2, 0); hb.Controls.Add(imp, 3, 0); hb.Controls.Add(clr, 4, 0);
            histSearch = new RInput(false); histSearch.Dock = DockStyle.Fill; histSearch.Margin = new Padding(0, 2, 0, 2);
            histSearch.Box.TextChanged += delegate { RefreshHistory(); };
            var hint = new Label(); hint.Text = "Search above  ·  double-click to load  ·  right-click to favorite. Enable history in Settings."; hint.Dock = DockStyle.Fill;
            hint.ForeColor = Theme.Muted; hint.Font = Theme.F(8.5f); hint.TextAlign = ContentAlignment.MiddleLeft;
            histList = new DarkList(); histList.Dock = DockStyle.Fill; histList.ItemHeight = 40; histList.DoubleClick += HistOpen; histList.MouseDown += HistMouse;
            tl.Controls.Add(hb, 0, 0); tl.Controls.Add(histSearch, 0, 1); tl.Controls.Add(hint, 0, 2); tl.Controls.Add(histList, 0, 3);
            p.Controls.Add(tl);
            return p;
        }
        void RefreshHistory()
        {
            string f = histSearch != null ? histSearch.Box.Text.Trim().ToLower() : "";
            histList.BeginUpdate(); histList.Items.Clear(); histMap.Clear();
            for (int i = 0; i < Hist.All.Count; i++)
            {
                var e = Hist.All[i];
                if (favOnly && !e.Fav) continue;
                if (f != "" && !((e.Src ?? "").ToLower().Contains(f) || (e.Result ?? "").ToLower().Contains(f))) continue;
                string star = e.Fav ? "★ " : "    ";
                histList.Items.Add(star + Trunc(e.Src, 40) + "    →    " + Trunc(e.Result, 40));
                histMap.Add(i);
            }
            histList.EndUpdate();
        }
        void HistOpen(object s, EventArgs e)
        {
            int i = histList.SelectedIndex;
            if (i < 0 || i >= histMap.Count) return;
            var he = Hist.All[histMap[i]];
            ShowTab("t");
            panel.Load(he.Src, he.From, he.To);
        }
        void HistMouse(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            int idx = histList.IndexFromPoint(e.Location);
            if (idx < 0 || idx >= histMap.Count) return;
            Hist.ToggleFav(histMap[idx]); RefreshHistory();
        }
        static string Trunc(string s, int n)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > n ? s.Substring(0, n) + "…" : s;
        }

        // ----- settings tab -----
        Panel BuildSettings()
        {
            var p = new Panel(); p.BackColor = Theme.Bg; p.Padding = new Padding(18, 14, 18, 14); p.AutoScroll = true;
            int y = 6;
            p.Controls.Add(Head("Translation engine", y)); y += 28;
            p.Controls.Add(Sub("All free, no API keys. Google is the most reliable & supports variants.", y)); y += 28;
            engBtns = new RButton[Engine.Names.Length];
            int x = 0;
            for (int i = 0; i < Engine.Names.Length; i++)
            {
                var b = new RButton(); b.Text = Engine.Names[i]; b.Width = 150; b.Height = 34; b.Left = x; b.Top = y;
                bool on0 = Engine.Names[i] == Config.S.Engine; b.Fill = on0 ? Theme.Accent : Theme.Card; b.Hover = on0 ? Theme.AccentH : Theme.Card2;
                string name = Engine.Names[i];
                b.Click += delegate { Config.S.Engine = name; Config.Save(); RefreshEngines(); };
                engBtns[i] = b; p.Controls.Add(b);
                x += 158; if (x > 320) { x = 0; y += 42; }
            }
            y += 56;

            p.Controls.Add(Head("Behaviour", y)); y += 30;
            AddToggle(p, "Copy translation automatically", delegate { return Config.S.AutoCopy; }, delegate (bool v) { Config.S.AutoCopy = v; }, ref y);
            AddToggle(p, "Keep translation history", delegate { return Config.S.KeepHistory; }, delegate (bool v) { Config.S.KeepHistory = v; }, ref y);
            AddToggle(p, "Paste typed translation into previous app on close", delegate { return Config.S.PasteOnClose; }, delegate (bool v) { Config.S.PasteOnClose = v; }, ref y);
            AddToggle(p, "Show alternatives & romanization", delegate { return Config.S.ShowVariants; }, delegate (bool v) { Config.S.ShowVariants = v; }, ref y);
            AddToggle(p, "Run on startup", delegate { return Config.S.RunOnStartup; }, delegate (bool v) { Config.S.RunOnStartup = v; Config.ApplyStartup(); }, ref y);
            y += 14;

            p.Controls.Add(Head("Speech & capture", y)); y += 30;
            AddToggle(p, "Enable text-to-speech", delegate { return Config.S.EnableTts; }, delegate (bool v) { Config.S.EnableTts = v; if (App.Rebuild != null) App.Rebuild(); }, ref y);
            AddToggle(p, "Skip emojis when speaking", delegate { return Config.S.TtsSkipEmoji; }, delegate (bool v) { Config.S.TtsSkipEmoji = v; }, ref y);
            AddToggle(p, "Enable OCR (capture text from screen)", delegate { return Config.S.EnableOcr; }, delegate (bool v) { Config.S.EnableOcr = v; if (App.Rebuild != null) App.Rebuild(); }, ref y);
            var vLbl = new Label(); vLbl.Text = "Voice"; vLbl.Font = Theme.F(9.5f); vLbl.ForeColor = Theme.Text; vLbl.AutoSize = true; vLbl.Left = 0; vLbl.Top = y + 8; p.Controls.Add(vLbl);
            var vBtn = new RButton(); vBtn.Align = ContentAlignment.MiddleLeft; vBtn.Fill = Theme.Card; vBtn.Hover = Theme.Card2; vBtn.Left = 110; vBtn.Top = y; vBtn.Width = 200; vBtn.Height = 32; vBtn.Font = Theme.F(9.5f);
            vBtn.Text = "  " + (string.IsNullOrEmpty(Config.S.Voice) ? "Auto (match language)" : Config.S.Voice);
            vBtn.Click += delegate {
                var opts = new List<string>(); opts.Add("Auto (match language)"); opts.AddRange(Tts.Voices());
                string cur = string.IsNullOrEmpty(Config.S.Voice) ? "Auto (match language)" : Config.S.Voice;
                string r = ListDialog.Pick(this, "Choose a voice", opts.ToArray(), cur);
                if (r != null) { Config.S.Voice = (r == "Auto (match language)") ? "" : r; Config.Save(); vBtn.Text = "  " + r; }
            };
            var prev = new RButton(); prev.Text = "Preview"; prev.Fill = Theme.Accent; prev.Hover = Theme.AccentH; prev.Left = 320; prev.Top = y; prev.Width = 80; prev.Height = 32; prev.Font = Theme.F(9f);
            prev.Click += delegate { Tts.Preview(Config.S.Voice); };
            p.Controls.Add(vBtn); p.Controls.Add(prev); y += 50;

            p.Controls.Add(Head("Default languages", y)); y += 30;
            p.Controls.Add(Sub("On selected text  (shortcut with a selection)", y)); y += 22;
            AddLangPair(p, true, y); y += 42;
            p.Controls.Add(Sub("When typing  (shortcut, nothing selected)", y)); y += 22;
            AddLangPair(p, false, y); y += 50;

            p.Controls.Add(Head("Shortcut", y)); y += 30;
            var hkLbl = new Label(); hkLbl.Text = "Global hotkey"; hkLbl.Font = Theme.F(9.5f); hkLbl.ForeColor = Theme.Text; hkLbl.AutoSize = true; hkLbl.Left = 0; hkLbl.Top = y + 8; p.Controls.Add(hkLbl);
            var hkBtn = new RButton(); hkBtn.Align = ContentAlignment.MiddleLeft; hkBtn.Fill = Theme.Card; hkBtn.Hover = Theme.Card2; hkBtn.Left = 200; hkBtn.Top = y; hkBtn.Width = 200; hkBtn.Height = 32; hkBtn.Font = Theme.F(9.5f);
            hkBtn.Text = "  " + HotkeyDialog.HotkeyLabel(Config.S.HotMods, Config.S.HotScan);
            hkBtn.Click += delegate {
                int m, sc; App.HotkeyCapturing = true;
                bool got = HotkeyDialog.Pick(this, out m, out sc); App.HotkeyCapturing = false;
                if (got) { Config.S.HotMods = m; Config.S.HotScan = sc; Config.Save(); hkBtn.Text = "  " + HotkeyDialog.HotkeyLabel(m, sc); }
            };
            p.Controls.Add(hkBtn); y += 50;

            p.Controls.Add(Head("Accessibility", y)); y += 30;
            p.Controls.Add(Sub("Theme", y + 6));
            AddSegment(p, 110, y, new string[] { "Dark", "Light", "System" }, delegate { return CapFirst(Config.S.ThemeMode); }, delegate (string v) { Config.S.ThemeMode = v.ToLower(); Config.Save(); ApplyTheme(); }); y += 44;
            p.Controls.Add(Sub("Text size", y + 6));
            AddSegment(p, 110, y, new string[] { "Small", "Normal", "Large", "Larger" }, delegate { return FontName(Config.S.FontScale); }, delegate (string v) { Config.S.FontScale = FontVal(v); Config.Save(); ApplyTheme(); }); y += 50;

            p.Controls.Add(Sub("Pin a language: open a language list and right-click it.", y));
            p.HandleCreated += delegate { Native.DarkScroll(p.Handle); };
            return p;
        }
        void AddSegment(Panel p, int left, int y, string[] opts, Func<string> get, Action<string> set)
        {
            var btns = new RButton[opts.Length];
            int x = left;
            for (int i = 0; i < opts.Length; i++)
            {
                var b = new RButton(); b.Text = opts[i]; b.Width = 78; b.Height = 30; b.Left = x; b.Top = y; b.Font = Theme.F(9f);
                string opt = opts[i];
                b.Click += delegate { set(opt); for (int j = 0; j < btns.Length; j++) { bool on2 = opts[j] == get(); btns[j].Fill = on2 ? Theme.Accent : Theme.Card; btns[j].Hover = on2 ? Theme.AccentH : Theme.Card2; btns[j].Invalidate(); } };
                btns[i] = b; p.Controls.Add(b); x += 84;
            }
            for (int j = 0; j < btns.Length; j++) { bool on2 = opts[j] == get(); btns[j].Fill = on2 ? Theme.Accent : Theme.Card; btns[j].Hover = on2 ? Theme.AccentH : Theme.Card2; }
        }
        static string CapFirst(string s) { if (string.IsNullOrEmpty(s)) return "Dark"; return char.ToUpper(s[0]) + s.Substring(1); }
        static string FontName(double s) { if (s <= 0.95) return "Small"; if (s < 1.08) return "Normal"; if (s < 1.22) return "Large"; return "Larger"; }
        static double FontVal(string n) { if (n == "Small") return 0.9; if (n == "Large") return 1.15; if (n == "Larger") return 1.3; return 1.0; }
        void ApplyTheme() { Theme.Apply(Config.S.ThemeMode, (float)Config.S.FontScale); Native.DarkMode(); if (App.Rebuild != null) App.Rebuild(); }
        void AddToggle(Panel p, string text, Func<bool> get, Action<bool> set, ref int y)
        {
            var l = new Label(); l.Text = text; l.Font = Theme.F(9.5f); l.ForeColor = Theme.Text; l.AutoSize = true; l.Left = 0; l.Top = y + 7;
            var b = new RButton(); b.Width = 66; b.Height = 30; b.Left = 360; b.Top = y; b.Font = Theme.F(9f);
            Action upd = delegate { bool v = get(); b.Text = v ? "On" : "Off"; b.Fill = v ? Theme.Accent : Theme.Card; b.Hover = v ? Theme.AccentH : Theme.Card2; b.Invalidate(); };
            b.Click += delegate { set(!get()); Config.Save(); upd(); };
            upd();
            p.Controls.Add(l); p.Controls.Add(b);
            y += 40;
        }
        void AddLangPair(Panel p, bool selectedMode, int y)
        {
            var bf = new RButton(); bf.Align = ContentAlignment.MiddleLeft; bf.Fill = Theme.Card; bf.Hover = Theme.Card2; bf.Left = 0; bf.Top = y; bf.Width = 175; bf.Height = 32; bf.Font = Theme.F(9.5f);
            var bt = new RButton(); bt.Align = ContentAlignment.MiddleLeft; bt.Fill = Theme.Card; bt.Hover = Theme.Card2; bt.Left = 190; bt.Top = y; bt.Width = 175; bt.Height = 32; bt.Font = Theme.F(9.5f);
            bf.Text = "  " + (selectedMode ? Config.S.SelFrom : Config.S.TypeFrom);
            bt.Text = "  " + (selectedMode ? Config.S.SelTo : Config.S.TypeTo);
            bf.Click += delegate
            {
                string cur = selectedMode ? Config.S.SelFrom : Config.S.TypeFrom;
                string r = LangDialog.Pick(this, cur, true);
                if (r != null) { if (selectedMode) Config.S.SelFrom = r; else Config.S.TypeFrom = r; Config.Save(); bf.Text = "  " + r; }
            };
            bt.Click += delegate
            {
                string cur = selectedMode ? Config.S.SelTo : Config.S.TypeTo;
                string r = LangDialog.Pick(this, cur, false);
                if (r != null) { if (selectedMode) Config.S.SelTo = r; else Config.S.TypeTo = r; Config.Save(); bt.Text = "  " + r; }
            };
            p.Controls.Add(bf); p.Controls.Add(bt);
        }
        void RefreshEngines()
        {
            for (int i = 0; i < engBtns.Length; i++)
            {
                bool on = Engine.Names[i] == Config.S.Engine;
                engBtns[i].Fill = on ? Theme.Accent : Theme.Card;
                engBtns[i].Hover = on ? Theme.AccentH : Theme.Card2;
                engBtns[i].Invalidate();
            }
        }
        Label Head(string t, int y) { var l = new Label(); l.Text = t; l.Font = Theme.FS(12f); l.ForeColor = Theme.Text; l.AutoSize = true; l.Left = 0; l.Top = y; return l; }
        Label Sub(string t, int y) { var l = new Label(); l.Text = t; l.Font = Theme.F(9f); l.ForeColor = Theme.Muted; l.AutoSize = true; l.Left = 0; l.Top = y; return l; }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            base.OnFormClosing(e);
        }
        public void ShowFront()
        {
            Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Native.ShowFront(Handle); Activate(); BringToFront();
        }
    }

    // ---------------- tray context ----------------
    class TrayContext : ApplicationContext
    {
        NotifyIcon tray;
        QuickForm quick;
        MainForm main;
        IntPtr hookId = IntPtr.Zero;
        Native.LowLevelKeyboardProc hookProc;

        public TrayContext(string startArg)
        {
            App.LoadIcon();
            Config.ApplyStartup();
            App.Rebuild = DoRebuild;
            quick = new QuickForm();
            quick.Prewarm(); // realize + JIT off-screen so the first open is instant

            tray = new NotifyIcon();
            tray.Icon = App.AppIcon; tray.Text = "Quick Translator"; tray.Visible = true;
            var menu = new ContextMenuStrip();
            menu.Items.Add("Quick translate", null, delegate { OnHotkey(); });
            menu.Items.Add("Open main window", null, delegate { ShowMain(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitApp(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { OnHotkey(); };

            InstallHook();
            StartPipeServer();

            if (startArg == "main") ShowMain();
        }

        void InstallHook()
        {
            hookProc = HookCallback;
            using (var mod = System.Diagnostics.Process.GetCurrentProcess().MainModule)
                hookId = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, hookProc, Native.GetModuleHandle(mod.ModuleName), 0);
        }
        IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && !App.HotkeyCapturing)
            {
                int msg = wParam.ToInt32();
                if (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN)
                {
                    var kb = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                    if (HotkeyMatch(kb.scanCode))   // configured combo, matched by scan code (layout independent)
                    {
                        try { quick.BeginInvoke(new Action(OnHotkey)); } catch { }
                        return (IntPtr)1; // swallow
                    }
                }
            }
            return Native.CallNextHookEx(hookId, nCode, wParam, lParam);
        }
        static bool HotkeyMatch(uint scan)
        {
            if (scan != (uint)Config.S.HotScan) return false;
            int m = Config.S.HotMods;
            bool ctrl = (m & 1) != 0, alt = (m & 2) != 0, shift = (m & 4) != 0, win = (m & 8) != 0;
            return Native.Down(Native.VK_CONTROL) == ctrl
                && Native.Down(Native.VK_MENU) == alt
                && Native.Down(Native.VK_SHIFT) == shift
                && (Native.Down(Native.VK_LWIN) || Native.Down(Native.VK_RWIN)) == win;
        }

        void OnHotkey()
        {
            try
            {
                IntPtr fg = Native.GetForegroundWindow();
                if (quick.Visible && fg == quick.Handle) { quick.DoCopyClose(); return; }
                if (main != null && main.Visible && fg == main.Handle) { return; }
                App.LastForeground = fg;     // remember where to paste back on close
                string sel = GrabSelection();
                quick.ShowWith(sel);
            }
            catch { }
        }
        string GrabSelection()
        {
            // Fast, low-contention grab: copy the selection and watch the clipboard
            // sequence number. If it changes, something was selected -> read it once.
            // If it doesn't change within the timeout, there was no selection.
            uint before = Native.GetClipboardSequenceNumber();
            Thread.Sleep(40);                        // let the hotkey's physical keystrokes settle first
            Native.SendCopy();
            if (WaitSeq(before, 22)) { Thread.Sleep(25); return Clip.GetText(); }
            Native.SendCopy();                       // retry once — some apps drop the first injected copy
            if (WaitSeq(before, 24)) { Thread.Sleep(25); return Clip.GetText(); }
            return "";
        }
        static bool WaitSeq(uint before, int tries)
        {
            for (int i = 0; i < tries; i++) { if (Native.GetClipboardSequenceNumber() != before) return true; Thread.Sleep(10); }
            return false;
        }

        void ShowMain()
        {
            if (main == null)
            {
                main = new MainForm();
                var _ = main.Handle;
            }
            main.ShowFront();
        }
        void DoRebuild()
        {
            // rebuild both windows so a theme / font-size change takes effect immediately
            try { quick.BeginInvoke(new Action(RebuildWindows)); } catch { try { RebuildWindows(); } catch { } }
        }
        void RebuildWindows()
        {
            try
            {
                var oldQuick = quick;
                quick = new QuickForm(); quick.Prewarm();
                try { oldQuick.Dispose(); } catch { }
                bool mainVisible = main != null && main.Visible;
                if (main != null) { try { main.Dispose(); } catch { } main = null; }
                if (mainVisible) ShowMain();
            }
            catch { }
        }

        void StartPipeServer()
        {
            var th = new Thread(PipeLoop); th.IsBackground = true; th.Start();
        }
        void PipeLoop()
        {
            while (true)
            {
                try
                {
                    using (var server = new NamedPipeServerStream("QuickTranslatorPipe", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                    {
                        server.WaitForConnection();
                        var buf = new byte[64]; int n = server.Read(buf, 0, buf.Length);
                        string cmd = Encoding.UTF8.GetString(buf, 0, n).Trim();
                        try { quick.BeginInvoke(new Action(delegate { if (cmd == "main") ShowMain(); else OnHotkey(); })); } catch { }
                    }
                }
                catch { Thread.Sleep(200); }
            }
        }

        void ExitApp()
        {
            try { if (hookId != IntPtr.Zero) Native.UnhookWindowsHookEx(hookId); } catch { }
            try { tray.Visible = false; tray.Dispose(); } catch { }
            Environment.Exit(0);
        }
    }

    static class Program
    {
        static void OcrSelfTest()
        {
            var log = new StringBuilder();
            try
            {
                var bmp = new Bitmap(700, 220);
                using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.White); g.DrawString("Hello World 12345", new Font("Segoe UI", 48f), Brushes.Black, 20, 60); }
                log.AppendLine("bitmap " + bmp.Width + "x" + bmp.Height);
                try { foreach (var l in Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages) log.AppendLine("recognizer: " + l.DisplayName + " [" + l.LanguageTag + "]"); }
                catch (Exception e) { log.AppendLine("avail-langs ERR: " + e.Message); }
                try { var eng = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages(); log.AppendLine("engineFromProfile null? " + (eng == null)); }
                catch (Exception e) { log.AppendLine("create ERR: " + e.Message); }
                string text = System.Threading.Tasks.Task.Run(new Func<string>(delegate { return Ocr.Recognize(bmp); })).Result;
                log.AppendLine("RESULT=[" + text + "]");

                // test 2: capture a REAL on-screen window via CopyFromScreen, then OCR (tests the capture path)
                var f = new Form(); f.FormBorderStyle = FormBorderStyle.None; f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(100, 100); f.Size = new Size(520, 150); f.BackColor = Color.White; f.TopMost = true; f.ShowInTaskbar = false;
                var lab = new Label(); lab.Text = "Capture 67890"; lab.Font = new Font("Segoe UI", 40f); lab.ForeColor = Color.Black; lab.AutoSize = true; lab.Location = new Point(20, 30); f.Controls.Add(lab);
                f.Show(); Application.DoEvents(); System.Threading.Thread.Sleep(350); Application.DoEvents();
                var cap = new Bitmap(520, 150);
                using (var g = Graphics.FromImage(cap)) g.CopyFromScreen(f.Location, Point.Empty, new Size(520, 150));
                f.Close();
                try { cap.Save(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qt_cap.png"), System.Drawing.Imaging.ImageFormat.Png); } catch { }
                string t2 = System.Threading.Tasks.Task.Run(new Func<string>(delegate { return Ocr.Recognize(cap); })).Result;
                log.AppendLine("CAPTURE-OCR=[" + t2 + "]");
            }
            catch (Exception ex) { log.AppendLine("EXC: " + ex); }
            try { File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qt_ocrtest.txt"), log.ToString()); } catch { }
        }
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "ocrtest") { OcrSelfTest(); return; }
            bool created;
            var mtx = new Mutex(true, "QuickTranslatorSingleton_v2", out created);
            string cmd = (args.Length > 0 && args[0].ToLower().Contains("main")) ? "main" : "quick";
            if (!created)
            {
                try
                {
                    using (var pipe = new NamedPipeClientStream(".", "QuickTranslatorPipe", PipeDirection.Out))
                    {
                        pipe.Connect(1500);
                        var b = Encoding.UTF8.GetBytes(cmd);
                        pipe.Write(b, 0, b.Length);
                    }
                }
                catch { }
                return;
            }
            try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)3072; } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            App.LoadIcon();
            Config.Load();
            Hist.Load();
            Theme.Apply(Config.S.ThemeMode, (float)Config.S.FontScale);
            Native.DarkMode();
            Application.Run(new TrayContext(cmd == "main" ? "main" : ""));
            GC.KeepAlive(mtx);
        }
    }
}
