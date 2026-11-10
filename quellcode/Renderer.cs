using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace DesktopUhr
{
    class Glyph
    {
        public string Key, Group, Text;
        public RectangleF Cell;
        public GraphicsPath Path;
        public bool Removed;
    }

    class Layout : IDisposable
    {
        public List<Glyph> Glyphs = new List<Glyph>();
        public float B, Pad;
        public void Dispose()
        {
            foreach (Glyph g in Glyphs) if (g.Path != null) g.Path.Dispose();
            Glyphs.Clear();
        }
    }

    static class Renderer
    {
        public static readonly float[] Sizes = { 56f, 84f, 120f, 170f };
        public static readonly string[] SizeNames = { "Klein", "Mittel", "Groß", "Sehr groß" };

        public static readonly Color[] Colors = {
            Color.FromArgb(255, 255, 255),
            Color.FromArgb(215, 220, 228),
            Color.FromArgb(150, 205, 255),
            Color.FromArgb(240, 200, 110),
            Color.FromArgb(255, 140, 120),
            Color.FromArgb(25, 25, 28)
        };
        public static readonly string[] ColorNames = { "Weiß", "Silber", "Eisblau", "Gold", "Koralle", "Schwarz" };

        static readonly string[] Days = { "Sonntag", "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag" };
        static readonly string[] Months = { "Jänner", "Februar", "März", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember" };

        static readonly FontFamily TimeFamily;
        static readonly FontFamily TextFamily;
        static readonly StringFormat Fmt;
        static readonly Graphics MeasureG;

        static Renderer()
        {
            TimeFamily = Find("Segoe UI Light", "Segoe UI", "Arial", "DejaVu Sans");
            TextFamily = Find("Segoe UI Semilight", "Segoe UI", "Arial", "DejaVu Sans");
            Fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
            Fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
            MeasureG = Graphics.FromImage(new Bitmap(1, 1));
            MeasureG.PageUnit = GraphicsUnit.Pixel;
            MeasureG.TextRenderingHint = TextRenderingHint.AntiAlias;
        }

        static FontFamily Find(params string[] names)
        {
            foreach (string n in names)
            {
                try { return new FontFamily(n); } catch { }
            }
            return FontFamily.GenericSansSerif;
        }

        public static string GroupName(string group)
        {
            switch (group)
            {
                case "wt": return "Wochentag";
                case "zeit": return "Uhrzeit";
                case "tag": return "Tag";
                case "mon": return "Monat";
                case "jahr": return "Jahr";
            }
            return group;
        }

        public static string TimeText(DateTime t, bool seconds)
        {
            return seconds
                ? string.Format("{0:00}:{1:00}:{2:00}", t.Hour, t.Minute, t.Second)
                : string.Format("{0:00}:{1:00}", t.Hour, t.Minute);
        }

        static string DateText(DateTime t, string month)
        {
            return t.Day + ". " + month.ToUpper() + " " + t.Year;
        }

        static float Adv(string ch, Font f)
        {
            return MeasureG.MeasureString(ch, f, int.MaxValue, Fmt).Width;
        }

        static float SpacedWidth(string text, FontFamily fam, float em, float spacing)
        {
            float w = 0;
            using (Font f = new Font(fam, em, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                for (int i = 0; i < text.Length; i++)
                {
                    w += Adv(text[i].ToString(), f);
                    if (i < text.Length - 1) w += spacing;
                }
            }
            return w;
        }

        static RectangleF Ink(string text, FontFamily fam, float em)
        {
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddString(text, fam, (int)FontStyle.Regular, em, new PointF(0, 0), Fmt);
                return p.GetBounds();
            }
        }

        struct Seg
        {
            public string Ch, Key, Group;
            public Seg(string ch, string key, string group) { Ch = ch; Key = key; Group = group; }
        }

        static void AddLine(Layout L, Settings s, List<Seg> segs, FontFamily fam, float em, float spacing,
            float y, float inkTop, float lineH, int W)
        {
            float b = L.B;
            using (Font f = new Font(fam, em, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                float[] adv = new float[segs.Count];
                float total = 0;
                for (int i = 0; i < segs.Count; i++)
                {
                    adv[i] = Adv(segs[i].Ch, f);
                    total += adv[i] + (i < segs.Count - 1 ? spacing : 0);
                }
                float x = AlignX(s.Align, W, L.Pad, total);
                for (int i = 0; i < segs.Count; i++)
                {
                    Seg sg = segs[i];
                    if (sg.Ch != " ")
                    {
                        PointF o = s.OffsetOf(sg.Key, sg.Group);
                        float gx = x + o.X * b, gy = y + o.Y * b;
                        GraphicsPath p = new GraphicsPath();
                        p.AddString(sg.Ch, fam, (int)FontStyle.Regular, em, new PointF(gx, gy - inkTop), Fmt);
                        Glyph g = new Glyph();
                        g.Key = sg.Key; g.Group = sg.Group; g.Text = sg.Ch; g.Path = p;
                        g.Cell = new RectangleF(gx, gy, Math.Max(adv[i], em * 0.25f), lineH);
                        g.Removed = s.IsRemoved(sg.Key, sg.Group);
                        L.Glyphs.Add(g);
                    }
                    x += adv[i] + spacing;
                }
            }
        }

        public static Layout Build(Settings s, DateTime now, float scale)
        {
            Layout L = new Layout();
            float b = Sizes[Clamp(s.Size, 0, Sizes.Length - 1)] * scale;
            float small = b * 0.25f;
            float spacing = small * 0.32f;
            L.B = b;
            L.Pad = Math.Max(6f, b * 0.12f);

            // Feste Breite, damit die Ausrichtung nicht springt
            float maxW = SpacedWidth(s.Seconds ? "00:00:00" : "00:00", TimeFamily, b, 0);
            if (s.Weekday)
                foreach (string d in Days) maxW = Math.Max(maxW, SpacedWidth(d.ToUpper(), TextFamily, small, spacing));
            foreach (string m in Months)
                maxW = Math.Max(maxW, SpacedWidth(DateText(new DateTime(2026, 1, 28), m), TextFamily, small, spacing));
            int W = (int)Math.Ceiling(maxW + 2 * L.Pad);

            RectangleF timeInk = Ink("00:00", TimeFamily, b);
            RectangleF capsTopInk = Ink("ÄH", TextFamily, small);
            RectangleF capsBaseInk = Ink("H", TextFamily, small);
            float capsH = capsBaseInk.Bottom - capsTopInk.Top;

            float y = L.Pad;
            if (s.Weekday)
            {
                string wd = Days[(int)now.DayOfWeek].ToUpper();
                List<Seg> segs = new List<Seg>();
                for (int i = 0; i < wd.Length; i++) segs.Add(new Seg(wd[i].ToString(), "wt." + i, "wt"));
                AddLine(L, s, segs, TextFamily, small, spacing, y, capsTopInk.Top, capsH, W);
                y += capsH + b * 0.17f;
            }
            {
                string t = TimeText(now, s.Seconds);
                List<Seg> segs = new List<Seg>();
                for (int i = 0; i < t.Length; i++) segs.Add(new Seg(t[i].ToString(), "zeit." + i, "zeit"));
                AddLine(L, s, segs, TimeFamily, b, 0, y, timeInk.Top, timeInk.Height, W);
                y += timeInk.Height + b * 0.16f;
            }
            {
                List<Seg> segs = new List<Seg>();
                string day = now.Day.ToString();
                for (int i = 0; i < day.Length; i++) segs.Add(new Seg(day[i].ToString(), "tag." + (day.Length - i), "tag"));
                segs.Add(new Seg(".", "tag.0", "tag"));
                segs.Add(new Seg(" ", "", ""));
                string mon = Months[now.Month - 1].ToUpper();
                for (int i = 0; i < mon.Length; i++) segs.Add(new Seg(mon[i].ToString(), "mon." + i, "mon"));
                segs.Add(new Seg(" ", "", ""));
                string yr = now.Year.ToString();
                for (int i = 0; i < yr.Length; i++) segs.Add(new Seg(yr[i].ToString(), "jahr." + i, "jahr"));
                AddLine(L, s, segs, TextFamily, small, spacing, y, capsTopInk.Top, capsH, W);
            }
            return L;
        }

        public static Bitmap Draw(Layout L, Settings s, bool edit, string hoverKey, string selKey, out Rectangle R)
        {
            RectangleF u = RectangleF.Empty;
            bool any = false;
            foreach (Glyph g in L.Glyphs)
            {
                if (g.Removed && !edit) continue;
                u = any ? RectangleF.Union(u, g.Cell) : g.Cell;
                any = true;
            }
            if (!any) u = new RectangleF(L.Pad, L.Pad, L.B * 0.4f, L.B * 0.4f);
            float pad = L.Pad;
            R = Rectangle.FromLTRB((int)Math.Floor(u.Left - pad), (int)Math.Floor(u.Top - pad),
                                   (int)Math.Ceiling(u.Right + pad), (int)Math.Ceiling(u.Bottom + pad));

            Color col = Colors[Clamp(s.Color, 0, Colors.Length - 1)];
            float op = Clamp(s.Opacity, 10, 100) / 100f;
            float b = L.B;

            Bitmap bmp = new Bitmap(Math.Max(1, R.Width), Math.Max(1, R.Height), PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(1, 0, 0, 0)); // fast unsichtbar, damit man die Uhr greifen kann
                g.TranslateTransform(-R.X, -R.Y);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;

                if (edit)
                {
                    using (SolidBrush tint = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                        g.FillRectangle(tint, R);
                    using (Pen frame = new Pen(Color.FromArgb(160, 255, 255, 255), 1f))
                    {
                        frame.DashStyle = DashStyle.Dash;
                        g.DrawRectangle(frame, R.X + 0.5f, R.Y + 0.5f, R.Width - 2, R.Height - 2);
                    }
                }

                using (GraphicsPath all = new GraphicsPath())
                {
                    foreach (Glyph gl in L.Glyphs) if (!gl.Removed) all.AddPath(gl.Path, false);

                    if (s.Shadow)
                    {
                        bool dark = col.GetBrightness() < 0.4f;
                        Color sc = dark ? Color.White : Color.Black;
                        float off = Math.Max(1f, b * 0.018f);
                        using (GraphicsPath sp = (GraphicsPath)all.Clone())
                        using (Matrix m = new Matrix())
                        {
                            m.Translate(off, off);
                            sp.Transform(m);
                            using (Pen soft = new Pen(Color.FromArgb((int)(40 * op), sc), Math.Max(2f, b * 0.045f)))
                            {
                                soft.LineJoin = LineJoin.Round;
                                g.DrawPath(soft, sp);
                            }
                            using (SolidBrush sb = new SolidBrush(Color.FromArgb((int)((dark ? 90 : 120) * op), sc)))
                                g.FillPath(sb, sp);
                        }
                    }
                    using (SolidBrush br = new SolidBrush(Color.FromArgb((int)(255 * op), col)))
                        g.FillPath(br, all);
                }

                if (edit)
                {
                    Color accent = Color.FromArgb(80, 170, 255);
                    using (SolidBrush ghost = new SolidBrush(Color.FromArgb(60, col)))
                    using (Pen ghostPen = new Pen(Color.FromArgb(90, 255, 255, 255), 1f))
                    {
                        ghostPen.DashStyle = DashStyle.Dot;
                        foreach (Glyph gl in L.Glyphs)
                        {
                            if (!gl.Removed) continue;
                            g.FillPath(ghost, gl.Path);
                            g.DrawRectangle(ghostPen, gl.Cell.X, gl.Cell.Y, gl.Cell.Width, gl.Cell.Height);
                        }
                    }
                    foreach (Glyph gl in L.Glyphs)
                    {
                        if (gl.Key == selKey)
                        {
                            using (SolidBrush sb = new SolidBrush(Color.FromArgb(60, accent)))
                                g.FillRectangle(sb, gl.Cell);
                            using (Pen p = new Pen(accent, 1.5f))
                                g.DrawRectangle(p, gl.Cell.X, gl.Cell.Y, gl.Cell.Width, gl.Cell.Height);
                        }
                        else if (gl.Key == hoverKey)
                        {
                            using (Pen p = new Pen(Color.FromArgb(200, accent), 1f))
                                g.DrawRectangle(p, gl.Cell.X, gl.Cell.Y, gl.Cell.Width, gl.Cell.Height);
                        }
                    }
                }
            }
            return bmp;
        }

        static float AlignX(int align, int W, float pad, float w)
        {
            if (align == 1) return (W - w) / 2f;
            if (align == 2) return W - pad - w;
            return pad;
        }

        public static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }
}
