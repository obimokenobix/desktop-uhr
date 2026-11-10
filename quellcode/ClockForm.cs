using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DesktopUhr
{
    public class ClockForm : Form
    {
        readonly Settings s;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ContextMenuStrip glyphMenu = new ContextMenuStrip();
        readonly NotifyIcon tray = new NotifyIcon();
        string lastKey = "";
        float scale = 1f;
        bool ready;

        Layout layout;
        Rectangle R;               // aktuelle Zeichenfläche in Layout-Koordinaten

        // Bearbeiten-Modus
        bool edit;
        string hoverKey, selKey;
        bool dragging, dragPushed;
        Point dragStart;
        string dragKey;
        PointF dragStartOff;

        struct Snapshot { public Dictionary<string, PointF> Off; public HashSet<string> Rem; }
        readonly Stack<Snapshot> undo = new Stack<Snapshot>();

        public static string ScriptPath = "";
        static string StartupFile
        {
            get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Desktop-Uhr.cmd"); }
        }

        internal ClockForm(Settings settings)
        {
            s = settings;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            Text = "Desktop-Uhr";
            try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f; } catch { }

            try { Icon = MakeIcon(); } catch { Icon = SystemIcons.Application; }
            tray.Icon = Icon;
            tray.Text = "Desktop-Uhr";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;

            menu.Opening += delegate { BuildMenu(); };
            BuildMenu();

            if (s.FirstRun) s.Save();
            if (IsAutostart()) SetAutostart(true); // Pfad aktualisieren, falls der Ordner verschoben wurde

            timer.Interval = 200;
            timer.Tick += delegate { Redraw(false); };
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += delegate { EnsureOnScreen(); Redraw(true); };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00080000 /*WS_EX_LAYERED*/ | 0x00000080 /*WS_EX_TOOLWINDOW*/;
                return cp;
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Rectangle r = MeasureR();
            if (s.X == int.MinValue || !OnSomeScreen(new Rectangle(s.X + r.X, s.Y + r.Y, r.Width, r.Height)))
                SetDefaultAnchor(r);
            Bounds = new Rectangle(s.X + r.X, s.Y + r.Y, r.Width, r.Height);
            ApplyLayer();
            ready = true;
            Redraw(true);
            timer.Start();
        }

        // ---------- Zeichnen ----------

        Rectangle MeasureR()
        {
            Rectangle r;
            using (Layout L = Renderer.Build(s, DateTime.Now, scale))
            using (Renderer.Draw(L, s, edit, null, null, out r)) { }
            return r;
        }

        void Redraw(bool force)
        {
            if (!ready) return;
            DateTime now = DateTime.Now;
            string key = Renderer.TimeText(now, s.Seconds) + now.Date.Ticks;
            if (!force && key == lastKey) return;
            lastKey = key;

            if (layout != null) layout.Dispose();
            layout = Renderer.Build(s, now, scale);
            Rectangle r;
            using (Bitmap bmp = Renderer.Draw(layout, s, edit, hoverKey, selKey, out r))
            {
                R = r;
                SetBitmap(bmp, new Point(s.X + r.X, s.Y + r.Y));
            }
        }

        void SetBitmap(Bitmap bmp, Point pos)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                old = SelectObject(memDc, hBmp);
                SIZE size = new SIZE { cx = bmp.Width, cy = bmp.Height };
                POINT src = new POINT { x = 0, y = 0 };
                POINT dst = new POINT { x = pos.X, y = pos.Y };
                BLENDFUNCTION blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2 /*ULW_ALPHA*/);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
                if (hBmp != IntPtr.Zero) { SelectObject(memDc, old); DeleteObject(hBmp); }
                DeleteDC(memDc);
            }
        }

        // ---------- Position ----------

        void SetDefaultAnchor(Rectangle r)
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int m = (int)(40 * scale);
            s.X = wa.Right - m - r.Right;
            s.Y = wa.Top + m - r.Y;
        }

        static bool OnSomeScreen(Rectangle r)
        {
            Point c = new Point(r.Left + r.Width / 2, r.Top + Math.Min(r.Height / 2, 40));
            foreach (Screen sc in Screen.AllScreens)
                if (sc.Bounds.Contains(c)) return true;
            return false;
        }

        void EnsureOnScreen()
        {
            if (!OnSomeScreen(Bounds)) { SetDefaultAnchor(R); s.Save(); }
        }

        void SaveAnchorFromWindow()
        {
            s.X = Left - R.X;
            s.Y = Top - R.Y;
            s.Save();
        }

        void StartWindowDrag()
        {
            ReleaseCapture();
            SendMessage(Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, (IntPtr)2 /*HTCAPTION*/, IntPtr.Zero);
        }

        // ---------- Maus ----------

        Glyph HitTest(Point client)
        {
            if (layout == null) return null;
            PointF p = new PointF(client.X + R.X, client.Y + R.Y);
            for (int i = layout.Glyphs.Count - 1; i >= 0; i--)
            {
                Glyph g = layout.Glyphs[i];
                if (g.Removed && !edit) continue;
                if (g.Cell.Contains(p)) return g;
            }
            return null;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (!edit)
            {
                if (!s.Locked) StartWindowDrag();
                return;
            }
            Activate();
            Glyph g = HitTest(e.Location);
            if (g == null)
            {
                selKey = null;
                Redraw(true);
                StartWindowDrag();
                return;
            }
            selKey = g.Key;
            bool whole = (ModifierKeys & Keys.Control) != 0;
            dragKey = whole ? g.Group + ".*" : g.Key;
            PointF o;
            s.Offsets.TryGetValue(dragKey, out o);
            dragStartOff = o;
            dragStart = Cursor.Position;
            dragging = true;
            dragPushed = false;
            Capture = true;
            Redraw(true);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!edit) return;
            if (dragging && layout != null)
            {
                Point c = Cursor.Position;
                int dx = c.X - dragStart.X, dy = c.Y - dragStart.Y;
                if (!dragPushed && (dx != 0 || dy != 0)) { PushUndo(); dragPushed = true; }
                if (dragPushed)
                {
                    s.Offsets[dragKey] = new PointF(dragStartOff.X + dx / layout.B, dragStartOff.Y + dy / layout.B);
                    Redraw(true);
                }
                return;
            }
            Glyph g = HitTest(e.Location);
            string k = g == null ? null : g.Key;
            Cursor = g == null ? Cursors.Default : Cursors.SizeAll;
            if (k != hoverKey) { hoverKey = k; Redraw(true); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && dragging)
            {
                dragging = false;
                Capture = false;
                if (dragPushed) s.Save();
                return;
            }
            if (e.Button == MouseButtons.Right)
            {
                Glyph g = edit ? HitTest(e.Location) : null;
                if (g != null)
                {
                    selKey = g.Key;
                    Redraw(true);
                    BuildGlyphMenu(g);
                    glyphMenu.Show(Cursor.Position);
                }
                else
                {
                    BuildMenu();
                    menu.Show(Cursor.Position);
                }
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (edit && hoverKey != null && !dragging) { hoverKey = null; Redraw(true); }
        }

        // ---------- Tastatur (im Bearbeiten-Modus) ----------

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            if (k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!edit) return;
            if (e.KeyCode == Keys.Escape) { SetEdit(false); return; }
            if (e.Control && e.KeyCode == Keys.Z) { Undo(); return; }
            Glyph g = FindGlyph(selKey);
            if (g == null) return;
            if (e.KeyCode == Keys.Delete || e.KeyCode == Keys.Back)
            {
                if (e.Control) SetGroupRemoved(g, true); else SetGlyphRemoved(g, !g.Removed);
                return;
            }
            int step = e.Shift ? 10 : 1;
            int dx = 0, dy = 0;
            if (e.KeyCode == Keys.Left) dx = -step;
            else if (e.KeyCode == Keys.Right) dx = step;
            else if (e.KeyCode == Keys.Up) dy = -step;
            else if (e.KeyCode == Keys.Down) dy = step;
            if (dx == 0 && dy == 0) return;
            PushUndo();
            string key = e.Control ? g.Group + ".*" : g.Key;
            PointF o;
            s.Offsets.TryGetValue(key, out o);
            s.Offsets[key] = new PointF(o.X + dx / layout.B, o.Y + dy / layout.B);
            s.Save();
            Redraw(true);
            e.Handled = true;
        }

        Glyph FindGlyph(string key)
        {
            if (key == null || layout == null) return null;
            foreach (Glyph g in layout.Glyphs) if (g.Key == key) return g;
            return null;
        }

        // ---------- Bearbeiten: Aktionen ----------

        void SetEdit(bool on)
        {
            if (edit == on) return;
            edit = on;
            hoverKey = null; selKey = null; dragging = false;
            Cursor = Cursors.Default;
            ApplyLayer();
            if (on)
            {
                Activate();
                tray.ShowBalloonTip(8000, "Bearbeiten-Modus",
                    "Zeichen ziehen = verschieben, Strg+ziehen = ganzen Teil verschieben.\n" +
                    "Entf = Zeichen entfernen, Rechtsklick = mehr. Esc = fertig.", ToolTipIcon.Info);
            }
            else
            {
                s.Save();
            }
            Redraw(true);
        }

        void PushUndo()
        {
            Snapshot sn;
            sn.Off = new Dictionary<string, PointF>(s.Offsets);
            sn.Rem = new HashSet<string>(s.Removed);
            undo.Push(sn);
            if (undo.Count > 200)
            {
                Snapshot[] arr = undo.ToArray();
                undo.Clear();
                for (int i = 99; i >= 0; i--) undo.Push(arr[i]);
            }
        }

        void Undo()
        {
            if (undo.Count == 0) return;
            Snapshot sn = undo.Pop();
            s.Offsets = sn.Off;
            s.Removed = sn.Rem;
            s.Save();
            Redraw(true);
        }

        void SetGlyphRemoved(Glyph g, bool remove)
        {
            PushUndo();
            if (remove)
            {
                s.Removed.Add(g.Key);
            }
            else
            {
                s.Removed.Remove(g.Key);
                if (s.Removed.Remove(g.Group + ".*"))
                {
                    // Ganzer Teil war entfernt: alle anderen Zeichen davon bleiben entfernt
                    foreach (Glyph o in layout.Glyphs)
                        if (o.Group == g.Group && o.Key != g.Key) s.Removed.Add(o.Key);
                }
            }
            s.Save();
            Redraw(true);
        }

        void SetGroupRemoved(Glyph g, bool remove)
        {
            PushUndo();
            string prefix = g.Group + ".";
            s.Removed.RemoveWhere(delegate (string k) { return k.StartsWith(prefix); });
            if (remove) s.Removed.Add(g.Group + ".*");
            s.Save();
            Redraw(true);
        }

        void ResetGlyphPos(Glyph g, bool wholeGroup)
        {
            PushUndo();
            if (wholeGroup)
            {
                string prefix = g.Group + ".";
                List<string> keys = new List<string>(s.Offsets.Keys);
                foreach (string k in keys) if (k.StartsWith(prefix)) s.Offsets.Remove(k);
            }
            else s.Offsets.Remove(g.Key);
            s.Save();
            Redraw(true);
        }

        void ResetLayout()
        {
            PushUndo();
            s.Offsets.Clear();
            s.Removed.Clear();
            s.Save();
            Redraw(true);
        }

        void RestoreAll()
        {
            PushUndo();
            s.Removed.Clear();
            s.Save();
            Redraw(true);
        }

        // ---------- Ebene (Desktop / Normal / Vordergrund) ----------

        void ApplyLayer()
        {
            if (edit)
            {
                SetOwner(IntPtr.Zero);
                TopMost = true;
                return;
            }
            IntPtr owner = IntPtr.Zero;
            if (s.Layer == 0) owner = FindWindow("Progman", null);
            SetOwner(owner);
            TopMost = s.Layer == 2;
            if (s.Layer == 0)
                SetWindowPos(Handle, (IntPtr)1 /*HWND_BOTTOM*/, 0, 0, 0, 0, 0x0013 /*NOSIZE|NOMOVE|NOACTIVATE*/);
        }

        void SetOwner(IntPtr owner)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr64(Handle, -8 /*GWL_HWNDPARENT*/, owner);
            else SetWindowLong32(Handle, -8, owner.ToInt32());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0046 /*WM_WINDOWPOSCHANGING*/ && ready && s.Layer == 0 && !edit)
            {
                WINDOWPOS wp = (WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(WINDOWPOS));
                wp.hwndInsertAfter = (IntPtr)1; // HWND_BOTTOM: bleibt immer hinter den Fenstern
                wp.flags &= ~0x0004u;            // SWP_NOZORDER entfernen
                Marshal.StructureToPtr(wp, m.LParam, false);
            }
            else if (m.Msg == 0x0232 /*WM_EXITSIZEMOVE*/)
            {
                SaveAnchorFromWindow();
            }
            base.WndProc(ref m);
        }

        // ---------- Menüs ----------

        void BuildGlyphMenu(Glyph g)
        {
            glyphMenu.Items.Clear();
            string part = Renderer.GroupName(g.Group);
            glyphMenu.Items.Add(new ToolStripMenuItem("Zeichen „" + g.Text + "“ (" + part + ")") { Enabled = false });
            glyphMenu.Items.Add(new ToolStripSeparator());
            if (g.Removed)
                glyphMenu.Items.Add(Item("Dieses Zeichen wiederherstellen", false, delegate { SetGlyphRemoved(g, false); }));
            else
                glyphMenu.Items.Add(Item("Dieses Zeichen entfernen\tEntf", false, delegate { SetGlyphRemoved(g, true); }));

            bool groupGone = s.Removed.Contains(g.Group + ".*");
            if (groupGone)
                glyphMenu.Items.Add(Item("Ganzen Teil wiederherstellen (" + part + ")", false, delegate { SetGroupRemoved(g, false); }));
            else
                glyphMenu.Items.Add(Item("Ganzen Teil entfernen (" + part + ")\tStrg+Entf", false, delegate { SetGroupRemoved(g, true); }));

            glyphMenu.Items.Add(new ToolStripSeparator());
            glyphMenu.Items.Add(Item("Position dieses Zeichens zurücksetzen", false, delegate { ResetGlyphPos(g, false); }));
            glyphMenu.Items.Add(Item("Position des ganzen Teils zurücksetzen", false, delegate { ResetGlyphPos(g, true); }));
            glyphMenu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem u = Item("Rückgängig\tStrg+Z", false, delegate { Undo(); });
            u.Enabled = undo.Count > 0;
            glyphMenu.Items.Add(u);
            glyphMenu.Items.Add(Item("Bearbeiten beenden\tEsc", false, delegate { SetEdit(false); }));
        }

        void BuildMenu()
        {
            menu.Items.Clear();
            menu.Items.Add(new ToolStripMenuItem("Desktop-Uhr") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem ed = Item("Bearbeiten-Modus (Zeichen verschieben/entfernen)", edit, delegate { SetEdit(!edit); });
            ed.Font = new Font(ed.Font, FontStyle.Bold);
            menu.Items.Add(ed);
            if (edit)
            {
                ToolStripMenuItem u = Item("Rückgängig\tStrg+Z", false, delegate { Undo(); });
                u.Enabled = undo.Count > 0;
                menu.Items.Add(u);
            }
            ToolStripMenuItem rest = Item("Entfernte Zeichen wiederherstellen", false, delegate { RestoreAll(); });
            rest.Enabled = s.Removed.Count > 0;
            menu.Items.Add(rest);
            ToolStripMenuItem rl = Item("Layout zurücksetzen (alles wie am Anfang)", false, delegate { ResetLayout(); });
            rl.Enabled = s.Removed.Count > 0 || s.Offsets.Count > 0;
            menu.Items.Add(rl);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem size = new ToolStripMenuItem("Größe");
            for (int i = 0; i < Renderer.SizeNames.Length; i++)
            {
                int k = i;
                size.DropDownItems.Add(Item(Renderer.SizeNames[i], s.Size == i, delegate { s.Size = k; Changed(); }));
            }
            menu.Items.Add(size);

            ToolStripMenuItem color = new ToolStripMenuItem("Farbe");
            for (int i = 0; i < Renderer.ColorNames.Length; i++)
            {
                int k = i;
                ToolStripMenuItem it = Item(Renderer.ColorNames[i], s.Color == i, delegate { s.Color = k; Changed(); });
                it.Image = Swatch(Renderer.Colors[i]);
                color.DropDownItems.Add(it);
            }
            menu.Items.Add(color);

            ToolStripMenuItem op = new ToolStripMenuItem("Deckkraft");
            foreach (int v in new int[] { 100, 80, 60, 40 })
            {
                int k = v;
                op.DropDownItems.Add(Item(v + " %", s.Opacity == v, delegate { s.Opacity = k; Changed(); }));
            }
            menu.Items.Add(op);

            ToolStripMenuItem align = new ToolStripMenuItem("Ausrichtung");
            string[] an = { "Links", "Mitte", "Rechts" };
            for (int i = 0; i < an.Length; i++)
            {
                int k = i;
                align.DropDownItems.Add(Item(an[i], s.Align == i, delegate { s.Align = k; Changed(); }));
            }
            menu.Items.Add(align);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Sekunden anzeigen", s.Seconds, delegate { s.Seconds = !s.Seconds; Changed(); }));
            menu.Items.Add(Item("Wochentag anzeigen", s.Weekday, delegate { s.Weekday = !s.Weekday; Changed(); }));
            menu.Items.Add(Item("Schatten", s.Shadow, delegate { s.Shadow = !s.Shadow; Changed(); }));

            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem layer = new ToolStripMenuItem("Ebene");
            string[] ln = { "Auf dem Desktop (hinter Fenstern)", "Normal", "Immer im Vordergrund" };
            for (int i = 0; i < ln.Length; i++)
            {
                int k = i;
                layer.DropDownItems.Add(Item(ln[i], s.Layer == i, delegate { s.Layer = k; s.Save(); ApplyLayer(); }));
            }
            menu.Items.Add(layer);
            menu.Items.Add(Item("Position sperren", s.Locked, delegate { s.Locked = !s.Locked; s.Save(); }));
            menu.Items.Add(Item("Position zurücksetzen", false, delegate { SetDefaultAnchor(R); s.Save(); Redraw(true); }));
            menu.Items.Add(Item("Mit Windows starten", IsAutostart(), delegate { SetAutostart(!IsAutostart()); }));

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Beenden", false, delegate { Close(); }));
        }

        static ToolStripMenuItem Item(string text, bool check, EventHandler click)
        {
            string shortcut = null;
            int tab = text.IndexOf('\t');
            if (tab >= 0) { shortcut = text.Substring(tab + 1); text = text.Substring(0, tab); }
            ToolStripMenuItem it = new ToolStripMenuItem(text);
            if (shortcut != null) it.ShortcutKeyDisplayString = shortcut;
            it.Checked = check;
            it.Click += click;
            return it;
        }

        static Image Swatch(Color c)
        {
            Bitmap b = new Bitmap(14, 14);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent);
                using (SolidBrush br = new SolidBrush(c)) g.FillRectangle(br, 1, 1, 12, 12);
                g.DrawRectangle(Pens.Gray, 1, 1, 11, 11);
            }
            return b;
        }

        void Changed()
        {
            s.Save();
            Redraw(true);
        }

        // ---------- Autostart ----------

        static bool IsAutostart()
        {
            try { return System.IO.File.Exists(StartupFile); } catch { return false; }
        }

        static void SetAutostart(bool on)
        {
            try
            {
                if (on && ScriptPath.Length > 0)
                {
                    System.IO.File.WriteAllText(StartupFile,
                        "@echo off\r\nchcp 65001 >nul\r\nstart \"\" /min powershell.exe -NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File \"" + ScriptPath + "\"\r\n",
                        new System.Text.UTF8Encoding(false));
                }
                else if (!on && System.IO.File.Exists(StartupFile))
                {
                    System.IO.File.Delete(StartupFile);
                }
            }
            catch { }
        }

        static Icon MakeIcon()
        {
            Bitmap b = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (SolidBrush rim = new SolidBrush(Color.FromArgb(30, 36, 48))) g.FillEllipse(rim, 1, 1, 30, 30);
                using (SolidBrush face = new SolidBrush(Color.FromArgb(245, 247, 250))) g.FillEllipse(face, 4, 4, 24, 24);
                using (Pen h = new Pen(Color.FromArgb(30, 36, 48), 2.6f)) { g.DrawLine(h, 16, 16, 11, 11); g.DrawLine(h, 16, 16, 22, 9); }
                using (SolidBrush dot = new SolidBrush(Color.FromArgb(220, 70, 60))) g.FillEllipse(dot, 14, 14, 4, 4);
            }
            return Icon.FromHandle(b.GetHicon());
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosed(e);
        }

        // ---------- Win32 ----------

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int idx, IntPtr val);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        static extern int SetWindowLong32(IntPtr hWnd, int idx, int val);
    }
}
