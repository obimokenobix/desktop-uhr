using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;

namespace DesktopUhr
{
    class Settings
    {
        public int X = int.MinValue, Y = int.MinValue;
        public int Size = 1;
        public int Color = 0;
        public int Align = 0;      // 0 links, 1 Mitte, 2 rechts
        public int Opacity = 100;
        public int Layer = 0;      // 0 Desktop, 1 Normal, 2 Immer im Vordergrund
        public bool Seconds = false, Weekday = true, Shadow = true, Locked = false;
        public bool FirstRun = true;

        // Versatz pro Zeichen ("mon.3") oder ganzer Teil ("mon.*"), in Vielfachen der Schriftgröße
        public Dictionary<string, PointF> Offsets = new Dictionary<string, PointF>();
        // Entfernte Zeichen ("mon.3") oder ganze Teile ("mon.*")
        public HashSet<string> Removed = new HashSet<string>();

        public PointF OffsetOf(string key, string group)
        {
            PointF a, b;
            Offsets.TryGetValue(key, out a);
            Offsets.TryGetValue(group + ".*", out b);
            return new PointF(a.X + b.X, a.Y + b.Y);
        }

        public bool IsRemoved(string key, string group)
        {
            return Removed.Contains(key) || Removed.Contains(group + ".*");
        }

        static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopUhr"); }
        }
        static string FilePath { get { return Path.Combine(Dir, "einstellungen.ini"); } }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                s.FirstRun = false;
                Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
                s.X = Int(d, "X", s.X); s.Y = Int(d, "Y", s.Y);
                s.Size = Int(d, "Groesse", s.Size); s.Color = Int(d, "Farbe", s.Color);
                s.Align = Int(d, "Ausrichtung", s.Align); s.Opacity = Int(d, "Deckkraft", s.Opacity);
                s.Layer = Int(d, "Ebene", s.Layer);
                s.Seconds = Bool(d, "Sekunden", s.Seconds); s.Weekday = Bool(d, "Wochentag", s.Weekday);
                s.Shadow = Bool(d, "Schatten", s.Shadow); s.Locked = Bool(d, "Gesperrt", s.Locked);

                string v;
                if (d.TryGetValue("Versatz", out v))
                {
                    foreach (string part in v.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] p = part.Split(':');
                        float x, y;
                        if (p.Length == 3 && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                                          && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                            s.Offsets[p[0]] = new PointF(x, y);
                    }
                }
                if (d.TryGetValue("Entfernt", out v))
                    foreach (string k in v.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                        s.Removed.Add(k.Trim());
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                StringBuilder off = new StringBuilder();
                foreach (KeyValuePair<string, PointF> kv in Offsets)
                {
                    if (kv.Value.X == 0 && kv.Value.Y == 0) continue;
                    if (off.Length > 0) off.Append('|');
                    off.Append(kv.Key).Append(':')
                       .Append(kv.Value.X.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                       .Append(kv.Value.Y.ToString("R", CultureInfo.InvariantCulture));
                }
                File.WriteAllLines(FilePath, new string[] {
                    "X=" + X, "Y=" + Y, "Groesse=" + Size, "Farbe=" + Color, "Ausrichtung=" + Align,
                    "Deckkraft=" + Opacity, "Ebene=" + Layer, "Sekunden=" + Seconds, "Wochentag=" + Weekday,
                    "Schatten=" + Shadow, "Gesperrt=" + Locked,
                    "Versatz=" + off, "Entfernt=" + string.Join(",", new List<string>(Removed).ToArray())
                });
                FirstRun = false;
            }
            catch { }
        }

        static int Int(Dictionary<string, string> d, string k, int def)
        {
            string v; int r;
            return d.TryGetValue(k, out v) && int.TryParse(v, out r) ? r : def;
        }
        static bool Bool(Dictionary<string, string> d, string k, bool def)
        {
            string v; bool r;
            return d.TryGetValue(k, out v) && bool.TryParse(v, out r) ? r : def;
        }
    }
}
