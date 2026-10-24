// أداة مواقيت الصلاة — Prayer Widget
// نافذة واحدة خفيفة لكل شاشة، تُرسم بـ GDI+ في ذاكرة DIB وتُعرض بـ UpdateLayeredWindow.
// لا إنترنت، لا WPF، لا رسوم متحركة: تحديث واحد في الثانية (أو في الدقيقة إذا أُخفيت الثواني).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("مواقيت الصلاة")]
[assembly: System.Reflection.AssemblyProduct("Prayer Widget")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace PrayerWidget
{
    // ───────────────────────────── الحساب الفلكي ─────────────────────────────
    // تقويم أم القرى: الفجر ١٨٫٥°، العشاء بعد المغرب بـ ٩٠ دقيقة (١٢٠ في رمضان)،
    // والعصر على قول الجمهور (ظل الشيء مثله) كما هو معتمد في السعودية.
    static class PrayerCalc
    {
        const double FajrAngle = 18.5, IshaMinutes = 90, RamadanIshaMinutes = 120, AsrFactor = 1;

        // يعيد ٦ أوقات بالتوقيت المحلي للجهاز: فجر، شروق، ظهر، عصر، مغرب، عشاء
        public static DateTime[] Compute(DateTime date, double lat, double lng)
        {
            double jd = Julian(date.Year, date.Month, date.Day) - lng / 360.0;
            double[] t = { 5, 6, 12, 13, 18, 18 };
            for (int it = 0; it < 2; it++)
            {
                double[] p = new double[6];
                for (int i = 0; i < 6; i++) p[i] = t[i] / 24.0;
                t[0] = SunAngleTime(jd, lat, FajrAngle, p[0], true);
                t[1] = SunAngleTime(jd, lat, 0.833, p[1], true);
                t[2] = MidDay(jd, p[2]);
                t[3] = AsrTime(jd, lat, AsrFactor, p[3]);
                t[4] = SunAngleTime(jd, lat, 0.833, p[4], false);
            }
            bool ramadan = new UmAlQuraCalendar().GetMonth(date) == 9;
            t[5] = t[4] + (ramadan ? RamadanIshaMinutes : IshaMinutes) / 60.0;

            // العروض العالية: إذا لم يصل الفجر لزاويته نستخدم نسبة الليل
            double night = 24 - (t[4] - t[1]);
            if (double.IsNaN(t[0]) || t[1] - t[0] > FajrAngle / 60.0 * night) t[0] = t[1] - FajrAngle / 60.0 * night;

            DateTime baseUtc = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc);
            DateTime[] r = new DateTime[6];
            for (int i = 0; i < 6; i++)
            {
                double h = t[i] - lng / 15.0;
                if (double.IsNaN(h)) h = 12;
                DateTime u = baseUtc.AddMinutes(Math.Round(h * 60.0));
                r[i] = u.ToLocalTime();
            }
            return r;
        }

        static double Rad(double d) { return d * Math.PI / 180.0; }
        static double Deg(double r) { return r * 180.0 / Math.PI; }
        static double DSin(double d) { return Math.Sin(Rad(d)); }
        static double DCos(double d) { return Math.Cos(Rad(d)); }
        static double DTan(double d) { return Math.Tan(Rad(d)); }
        static double Fix(double a, double b) { a = a - b * Math.Floor(a / b); return a < 0 ? a + b : a; }

        static void SunPos(double jd, out double decl, out double eqt)
        {
            double D = jd - 2451545.0;
            double g = Fix(357.529 + 0.98560028 * D, 360);
            double q = Fix(280.459 + 0.98564736 * D, 360);
            double L = Fix(q + 1.915 * DSin(g) + 0.020 * DSin(2 * g), 360);
            double e = 23.439 - 0.00000036 * D;
            double ra = Deg(Math.Atan2(DCos(e) * DSin(L), DCos(L))) / 15.0;
            eqt = q / 15.0 - Fix(ra, 24);
            decl = Deg(Math.Asin(DSin(e) * DSin(L)));
        }

        static double MidDay(double jd, double t)
        {
            double d, e; SunPos(jd + t, out d, out e);
            return Fix(12 - e, 24);
        }

        static double SunAngleTime(double jd, double lat, double angle, double t, bool ccw)
        {
            double decl, e; SunPos(jd + t, out decl, out e);
            double noon = MidDay(jd, t);
            double T = Deg(Math.Acos((-DSin(angle) - DSin(decl) * DSin(lat)) / (DCos(decl) * DCos(lat)))) / 15.0;
            return noon + (ccw ? -T : T);
        }

        static double AsrTime(double jd, double lat, double factor, double t)
        {
            double decl, e; SunPos(jd + t, out decl, out e);
            double angle = -Deg(Math.Atan(1.0 / (factor + DTan(Math.Abs(lat - decl)))));
            return SunAngleTime(jd, lat, angle, t, false);
        }

        static double Julian(int y, int m, int d)
        {
            if (m <= 2) { y -= 1; m += 12; }
            double A = Math.Floor(y / 100.0);
            double B = 2 - A + Math.Floor(A / 4.0);
            return Math.Floor(365.25 * (y + 4716)) + Math.Floor(30.6001 * (m + 1)) + d + B - 1524.5;
        }
    }

    // ───────────────────────────── المدن ─────────────────────────────
    class City
    {
        public string Country, Name; public double Lat, Lng;
        public City(string c, string n, double la, double lo) { Country = c; Name = n; Lat = la; Lng = lo; }

        static City K(string n, double la, double lo) { return new City("السعودية", n, la, lo); }

        public static readonly City[] All = {
            K("الرياض", 24.7136, 46.6753), K("مكة المكرمة", 21.3891, 39.8579), K("المدينة المنورة", 24.4686, 39.6142),
            K("جدة", 21.4858, 39.1925), K("الدمام", 26.4207, 50.0888), K("الخبر", 26.2172, 50.1971),
            K("الظهران", 26.2361, 50.0393), K("القطيف", 26.5652, 49.9964), K("الأحساء", 25.3833, 49.5867),
            K("الجبيل", 27.0046, 49.6460), K("حفر الباطن", 28.4328, 45.9708), K("الطائف", 21.2703, 40.4158),
            K("تبوك", 28.3835, 36.5662), K("بريدة", 26.3260, 43.9750), K("عنيزة", 26.0843, 43.9935),
            K("الرس", 25.8694, 43.4973), K("حائل", 27.5114, 41.7208), K("أبها", 18.2164, 42.5053),
            K("خميس مشيط", 18.3000, 42.7333), K("جازان", 16.8892, 42.5511), K("نجران", 17.4924, 44.1277),
            K("الباحة", 20.0129, 41.4677), K("بيشة", 19.9844, 42.6053), K("عرعر", 30.9753, 41.0381),
            K("سكاكا", 29.9697, 40.2064), K("القريات", 31.3317, 37.3428), K("طريف", 31.6725, 38.6637),
            K("ينبع", 24.0895, 38.0618), K("رابغ", 22.7986, 39.0349), K("العلا", 26.6085, 37.9232),
            K("الوجه", 26.2453, 36.4525), K("ضباء", 27.3510, 35.6901), K("الخرج", 24.1556, 47.3346),
            K("المجمعة", 25.9039, 45.3456), K("الزلفي", 26.2994, 44.8150), K("شقراء", 25.2482, 45.2530),
            K("الدوادمي", 24.5077, 44.3924), K("وادي الدواسر", 20.4607, 44.7811), K("القنفذة", 19.1281, 41.0787),

            new City("الكويت", "الكويت", 29.3759, 47.9774),
            new City("الكويت", "الجهراء", 29.3375, 47.6581),
            new City("الإمارات", "دبي", 25.2048, 55.2708),
            new City("الإمارات", "أبوظبي", 24.4539, 54.3773),
            new City("الإمارات", "الشارقة", 25.3463, 55.4209),
            new City("الإمارات", "العين", 24.2075, 55.7447),
            new City("الإمارات", "رأس الخيمة", 25.8007, 55.9762),
            new City("قطر", "الدوحة", 25.2854, 51.5310),
            new City("البحرين", "المنامة", 26.2285, 50.5860),
            new City("عُمان", "مسقط", 23.5880, 58.3829),
            new City("عُمان", "صلالة", 17.0151, 54.0924),
            new City("اليمن", "صنعاء", 15.3694, 44.1910),
            new City("اليمن", "عدن", 12.7855, 45.0187),
            new City("مصر", "القاهرة", 30.0444, 31.2357),
            new City("مصر", "الإسكندرية", 31.2001, 29.9187),
            new City("الأردن", "عمّان", 31.9454, 35.9284),
            new City("فلسطين", "القدس", 31.7683, 35.2137),
            new City("فلسطين", "غزة", 31.5017, 34.4668),
            new City("العراق", "بغداد", 33.3152, 44.3661),
            new City("العراق", "البصرة", 30.5085, 47.7804),
            new City("سوريا", "دمشق", 33.5138, 36.2765),
            new City("لبنان", "بيروت", 33.8938, 35.5018),
            new City("السودان", "الخرطوم", 15.5007, 32.5599),
            new City("ليبيا", "طرابلس", 32.8872, 13.1913),
            new City("تونس", "تونس", 36.8065, 10.1815),
            new City("الجزائر", "الجزائر", 36.7538, 3.0588),
            new City("المغرب", "الرباط", 34.0209, -6.8416),
            new City("المغرب", "الدار البيضاء", 33.5731, -7.5898),
            new City("تركيا", "إسطنبول", 41.0082, 28.9784),
            new City("تركيا", "أنقرة", 39.9334, 32.8597),
            new City("باكستان", "كراتشي", 24.8607, 67.0011),
            new City("باكستان", "إسلام آباد", 33.6844, 73.0479),
            new City("إندونيسيا", "جاكرتا", -6.2088, 106.8456),
            new City("ماليزيا", "كوالالمبور", 3.1390, 101.6869),
            new City("بريطانيا", "لندن", 51.5074, -0.1278),
            new City("ألمانيا", "برلين", 52.5200, 13.4050),
            new City("فرنسا", "باريس", 48.8566, 2.3522),
            new City("أمريكا", "نيويورك", 40.7128, -74.0060),
            new City("أمريكا", "ديترويت", 42.3314, -83.0458),
            new City("كندا", "تورونتو", 43.6532, -79.3832),
            new City("أستراليا", "سيدني", -33.8688, 151.2093),
        };
    }

    // ───────────────────────────── الإعدادات ─────────────────────────────
    class Settings
    {
        public string Layout = "square";          // square | wide | tall
        public string Country = "السعودية", CityName = "الرياض";
        public double Lat = 24.7136, Lng = 46.6753;
        public string Screens = "all";            // all | اسم الشاشة
        public string Theme = "auto";             // auto (حسب ويندوز) | dark | light
        public bool Seconds = true, H24 = false, Locked = false;
        public bool OverIcons = false;            // true: فوق الاختصارات وتغطيها، false: تحجز مكانها وتبعد الاختصارات

        public static bool SystemDark = ReadSystemDark();
        public bool Dark { get { return Theme == "auto" ? SystemDark : Theme == "dark"; } }

        public static bool ReadSystemDark()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k == null || !(k.GetValue("AppsUseLightTheme") is int) || (int)k.GetValue("AppsUseLightTheme") == 0;
            }
            catch { return true; }
        }
        public double Size = 1.0;
        public Dictionary<string, Point> Pos = new Dictionary<string, Point>();

        static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PrayerWidget"); } }
        static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                    switch (k)
                    {
                        case "layout": s.Layout = v; break;
                        case "country": s.Country = v; break;
                        case "city": s.CityName = v; break;
                        case "lat": s.Lat = double.Parse(v, Inv); break;
                        case "lng": s.Lng = double.Parse(v, Inv); break;
                        case "screens": s.Screens = v; break;
                        case "theme": s.Theme = v; break;
                        case "overicons": s.OverIcons = v == "1"; break;
                        case "seconds": s.Seconds = v == "1"; break;
                        case "h24": s.H24 = v == "1"; break;
                        case "locked": s.Locked = v == "1"; break;
                        case "size": s.Size = double.Parse(v, Inv); break;
                        default:
                            if (k.StartsWith("pos:"))
                            {
                                string[] xy = v.Split(',');
                                s.Pos[k.Substring(4)] = new Point(int.Parse(xy[0], Inv), int.Parse(xy[1], Inv));
                            }
                            break;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                StringBuilder b = new StringBuilder();
                b.AppendLine("layout=" + Layout);
                b.AppendLine("country=" + Country);
                b.AppendLine("city=" + CityName);
                b.AppendLine("lat=" + Lat.ToString(Inv));
                b.AppendLine("lng=" + Lng.ToString(Inv));
                b.AppendLine("screens=" + Screens);
                b.AppendLine("theme=" + Theme);
                b.AppendLine("overicons=" + (OverIcons ? "1" : "0"));
                b.AppendLine("seconds=" + (Seconds ? "1" : "0"));
                b.AppendLine("h24=" + (H24 ? "1" : "0"));
                b.AppendLine("locked=" + (Locked ? "1" : "0"));
                b.AppendLine("size=" + Size.ToString(Inv));
                foreach (KeyValuePair<string, Point> p in Pos)
                    b.AppendLine("pos:" + p.Key + "=" + p.Value.X.ToString(Inv) + "," + p.Value.Y.ToString(Inv));
                File.WriteAllText(FilePath, b.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }

    // ───────────────────────────── لقطة الحالة ─────────────────────────────
    class Snap
    {
        public DateTime Now;
        public DateTime[] Day;       // أوقات اليوم المعروض
        public string[] Names;
        public bool Since;           // true: أول نصف ساعة بعد الأذان ("مضى")، false: عدّ تنازلي للقادمة ("بقي")
        public int HeadIdx;          // الصلاة المعروضة في الأعلى والمميّزة في القائمة
        public string HeadName;
        public DateTime HeadTime;
        public string Prefix, CounterText, CounterUnit;   // "بقي" + "14" + "دقيقة" (الوحدة null مع h:mm:ss)
        public DateTime NextTime;    // موعد أذان الصلاة القادمة
        public bool FastTick;        // الدقيقة الأولى بعد الأذان تُحدّث كل ثانية
        public float Progress;       // ٠..١: كم مضى من الوقت بين الأذان السابق والقادم
        public string StaticKey;     // يتغير فقط عندما تحتاج الطبقة الثابتة لإعادة رسم
    }

    // ───────────────────────────── الرسم ─────────────────────────────
    // مادة الزجاج على طريقة Liquid Glass (النوع العادي): ما خلف الأداة مموّه مع رفع التشبع وضبط الإضاءة
    // ليبقى النص مقروءًا، ثم طبقة تعتيم/تفتيح خفيفة، لمعة علوية، وحافة ضوئية كالعدسة، وظل ناعم تحتها.
    static class Painter
    {
        public const float Radius = 28;     // نصف قطر الزوايا
        public const float Margin = 14;     // مساحة الظل حول الأداة
        // خط ثمانية إن كان مثبّتًا على الجهاز، وإلا Segoe UI
        static string RegFam = "Segoe UI", MedFam = "Segoe UI Semibold", BoldFam = "Segoe UI Semibold";
        static FontStyle BoldStyle = FontStyle.Regular;
        public static bool Thmanyah;
        static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();

        static Painter()
        {
            if (HasFamily("thmanyah sans"))
            {
                Thmanyah = true;
                RegFam = "thmanyah sans";
                BoldFam = "thmanyah sans"; BoldStyle = FontStyle.Bold;
                MedFam = HasFamily("thmanyah sans Med") ? "thmanyah sans Med" : RegFam;
            }
            else if (!HasFamily(BoldFam)) MedFam = BoldFam = RegFam;
        }

        static bool HasFamily(string name)
        {
            try { using (FontFamily f = new FontFamily(name)) return true; } catch { return false; }
        }

        static Font F(string fam, float px, FontStyle st)
        {
            string k = fam + "|" + px.ToString(CultureInfo.InvariantCulture) + "|" + (int)st;
            Font f;
            if (!fonts.TryGetValue(k, out f)) { f = new Font(fam, px, st, GraphicsUnit.Pixel); fonts[k] = f; }
            return f;
        }

        static Font R(float px) { return F(RegFam, px, FontStyle.Regular); }   // عادي
        static Font M(float px) { return F(MedFam, px, FontStyle.Regular); }   // متوسط
        static Font Bd(float px) { return F(BoldFam, px, BoldStyle); }         // عريض

        // حجم البطاقة نفسها (بدون الظل)
        public static Size BaseSize(string layout)
        {
            if (layout == "wide") return new Size(460, 168);
            if (layout == "tall") return new Size(220, 340);
            return new Size(184, 184);
        }

        // حجم النافذة كاملة: البطاقة + الظل
        public static Size OuterSize(string layout)
        {
            Size b = BaseSize(layout);
            return new Size(b.Width + (int)(2 * Margin), b.Height + (int)(2 * Margin));
        }

        // ألوان "حيوية" فوق المادة: أساسي، ثانوي، ثالث، لون التمييز، ومسار شريط التقدم
        struct Pal { public Color P, S, T, Acc, Track, Pill, PillRim; public bool Dark; }

        static Color W(int a) { return Color.FromArgb(a, 255, 255, 255); }
        static Color B(int a) { return Color.FromArgb(a, 0, 0, 0); }

        static Pal GetPal(Settings s)
        {
            Pal p = new Pal();
            p.Dark = s.Dark;
            if (s.Dark)
            {
                p.P = W(250); p.S = W(185); p.T = W(130);
                p.Acc = Color.FromArgb(255, 74, 222, 128);          // أخضر حيوي
                p.Track = W(46); p.Pill = W(34); p.PillRim = W(60);
            }
            else
            {
                Color ink = Color.FromArgb(14, 16, 22);
                p.P = Color.FromArgb(242, ink); p.S = Color.FromArgb(170, ink); p.T = Color.FromArgb(120, ink);
                p.Acc = Color.FromArgb(255, 21, 128, 61);            // أخضر داكن بتباين كافٍ على الفاتح
                p.Track = B(30); p.Pill = W(150); p.PillRim = W(230);
            }
            return p;
        }

        static void Setup(Graphics g, float sc)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.ScaleTransform(sc, sc);
            g.TranslateTransform(Margin, Margin);
        }

        // الطبقة الثابتة: تتغير فقط عند تغير الصلاة القادمة أو الإعدادات أو المكان
        // desk: صورة مصغّرة لخلفية الشاشة (null = مادة سميكة بلا خلفية)، src: الجزء الواقع خلف البطاقة منها
        public static void PaintStatic(Graphics g, float sc, Settings s, Snap n, Bitmap desk, RectangleF src)
        {
            Size b = BaseSize(s.Layout);
            float Wd = b.Width, H = b.Height;
            Setup(g, sc);
            Pal p = GetPal(s);
            Glass(g, Wd, H, sc, s, desk, src);

            if (s.Layout == "wide") Wide(g, Wd, n, p);
            else
            {
                Header(g, Wd, n, p, s.Layout == "square");
                if (s.Layout == "tall") List(g, Wd, n, p);
            }
        }

        // الطبقة المتغيرة: العدّاد ("بقي ..." أو "مضى ...") وشريط التقدم
        public static void PaintDynamic(Graphics g, float sc, Settings s, Snap n)
        {
            Size b = BaseSize(s.Layout);
            float Wd = b.Width;
            Setup(g, sc);
            Pal p = GetPal(s);
            if (s.Layout == "wide")
            {
                float pad = 22;
                Counter(g, n, p, new RectangleF(pad, 18, Wd / 2 - pad, 56), -1);
                Progress(g, new RectangleF(pad, 86, Wd - 2 * pad, 5), n.Progress, p, true);
            }
            else
            {
                float pad = 18, w = Wd - 2 * pad;
                Counter(g, n, p, new RectangleF(pad, 72, w, 56), 1);
                Progress(g, new RectangleF(pad, 142, w, 5), n.Progress, p, true);
            }
        }

        // سطر العدّاد من اليمين لليسار: "بقي" صغيرة، الرقم كبير، ثم الوحدة صغيرة: [دقيقة] [14] [بقي]
        // بعد الأذان يصير "مضى" بلون التمييز
        static void Counter(Graphics g, Snap n, Pal p, RectangleF r, int align)
        {
            Font big = Bd(44), small = M(17);
            Color numC = n.Since ? p.Acc : p.P, wordC = n.Since ? p.Acc : p.S;
            const float gap = 8;
            float prefW = Measure(g, n.Prefix, small, true);
            float numW = Measure(g, n.CounterText, big, false);
            float unitW = n.CounterUnit == null ? 0 : Measure(g, n.CounterUnit, small, true);
            float total = prefW + gap + numW + (unitW > 0 ? gap + unitW : 0);
            if (total > r.Width)
            {
                // رقم طويل مثل 7:25:10 لا يتسع: نصغّر خط الرقم بقدر ما يلزم (بخطوات صحيحة حتى تبقى الخطوط مخزّنة)
                float room = r.Width - (total - numW);
                big = Bd(Math.Max(22, (float)Math.Floor(44 * room / numW)));
                numW = Measure(g, n.CounterText, big, false);
                total = prefW + gap + numW + (unitW > 0 ? gap + unitW : 0);
            }
            float x = align == 1 ? r.Right - total : r.X;
            const float drop = 8;   // الكلمات الصغيرة تنزل قليلًا لتجلس على سطر الرقم
            if (unitW > 0)
            {
                Txt(g, n.CounterUnit, small, wordC, new RectangleF(x, r.Y + drop, unitW, r.Height), -1, true, p);
                x += unitW + gap;
            }
            Txt(g, n.CounterText, big, numC, new RectangleF(x, r.Y, numW, r.Height), -1, false, p);
            x += numW + gap;
            Txt(g, n.Prefix, small, wordC, new RectangleF(x, r.Y + drop, prefW, r.Height), -1, true, p);
        }

        static float Measure(Graphics g, string s, Font f, bool rtl)
        {
            return g.MeasureString(s, f, PointF.Empty, Fmt(-1, rtl)).Width;
        }

        // شريط تقدّم على شكل كبسولة: كم مضى من الوقت بين الأذان السابق والقادم
        static void Progress(Graphics g, RectangleF r, float v, Pal p, bool fromRight)
        {
            v = Math.Max(0, Math.Min(1, v));
            float rad = r.Height / 2;
            using (GraphicsPath track = RR(r.X, r.Y, r.Width, r.Height, rad))
            using (SolidBrush b = new SolidBrush(p.Track))
                g.FillPath(b, track);
            float fw = Math.Max(r.Height, r.Width * v);
            float fx = fromRight ? r.Right - fw : r.X;
            using (GraphicsPath fill = RR(fx, r.Y, fw, r.Height, rad))
            using (SolidBrush b = new SolidBrush(p.Acc))
                g.FillPath(b, fill);
        }

        static void Glass(Graphics g, float Wd, float H, float sc, Settings s, Bitmap desk, RectangleF src)
        {
            bool dark = s.Dark;

            // ظل ناعم متدرج تحت البطاقة
            for (int i = 1; i <= 10; i++)
                using (GraphicsPath sp = RR(-i * 0.9f, -i * 0.9f + 4, Wd + i * 1.8f, H + i * 1.8f, Radius + i * 0.9f))
                using (Pen pen = new Pen(B((dark ? 7 : 5) * (11 - i) / 4), 1.8f))
                    g.DrawPath(pen, sp);

            float o = 0.5f / sc;
            using (GraphicsPath path = RR(o, o, Wd - 2 * o, H - 2 * o, Radius))
            {
                bool frosted = desk != null;
                g.SetClip(path);
                if (frosted)
                {
                    // التمويه يأتي مجانًا: الخلفية محفوظة مصغّرة جدًا، وتكبيرها هو التمويه نفسه
                    InterpolationMode im = g.InterpolationMode;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    using (ImageAttributes ia = new ImageAttributes())
                    {
                        ia.SetWrapMode(WrapMode.TileFlipXY);
                        ia.SetColorMatrix(Vibrancy(dark ? 1.7f : 1.5f, dark ? 0.62f : 0.80f, dark ? 0f : 0.20f));
                        g.DrawImage(desk, new Rectangle(0, 0, (int)Math.Ceiling(Wd), (int)Math.Ceiling(H)),
                            src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                    }
                    g.InterpolationMode = im;
                }

                // طبقة التعتيم/التفتيح (أسمك عندما تكون فوق التطبيقات)
                Color top, bot;
                if (dark) { top = Color.FromArgb(frosted ? 55 : 215, 34, 34, 42); bot = Color.FromArgb(frosted ? 95 : 232, 14, 14, 18); }
                else { top = Color.FromArgb(frosted ? 70 : 228, 255, 255, 255); bot = Color.FromArgb(frosted ? 35 : 218, 238, 240, 246); }
                RectangleF all = new RectangleF(-1, -1, Wd + 2, H + 2);
                using (LinearGradientBrush br = new LinearGradientBrush(all, top, bot, 90f)) g.FillRectangle(br, all);

                // لمعة علوية
                // (مستطيل الفرشاة أطول من المساحة المرسومة حتى لا يلتف التدرج ويظهر خط في المنتصف)
                using (LinearGradientBrush br = new LinearGradientBrush(new RectangleF(0, -2, Wd, H * 0.5f + 8), W(dark ? 26 : 70), W(0), 90f))
                    g.FillRectangle(br, 0, 0, Wd, H * 0.5f);
                g.ResetClip();

                // حافة داخلية خفيفة تعطي سماكة الزجاج
                using (GraphicsPath inner = RR(1.6f, 1.6f, Wd - 3.2f, H - 3.2f, Radius - 1.6f))
                using (LinearGradientBrush br = new LinearGradientBrush(all, W(dark ? 40 : 120), W(0), 90f))
                using (Pen pen = new Pen(br, 1f))
                    g.DrawPath(pen, inner);

                // الحافة الضوئية: لامعة أعلى اليمين وأسفل اليسار كانكسار الضوء في العدسة
                using (LinearGradientBrush br = new LinearGradientBrush(all, Color.White, Color.White, 125f))
                {
                    ColorBlend cb = new ColorBlend(5);
                    cb.Colors = new Color[] { W(dark ? 190 : 255), W(dark ? 60 : 150), W(dark ? 18 : 70), W(dark ? 50 : 140), W(dark ? 150 : 240) };
                    cb.Positions = new float[] { 0f, 0.22f, 0.5f, 0.8f, 1f };
                    br.InterpolationColors = cb;
                    using (Pen pen = new Pen(br, 1.2f)) g.DrawPath(pen, path);
                }
            }
        }

        // مصفوفة ألوان: تشبّع s، ثم ضرب الإضاءة k وإضافة a (لرفع الخلفية في الوضع الفاتح)
        static ColorMatrix Vibrancy(float s, float k, float a)
        {
            float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f, t = 1 - s;
            return new ColorMatrix(new float[][] {
                new float[] { (t * lr + s) * k, t * lr * k, t * lr * k, 0, 0 },
                new float[] { t * lg * k, (t * lg + s) * k, t * lg * k, 0, 0 },
                new float[] { t * lb * k, t * lb * k, (t * lb + s) * k, 0, 0 },
                new float[] { 0, 0, 0, 1, 0 },
                new float[] { a, a, a, 0, 1 },
            });
        }

        // اسم الصلاة بخط كبير. في المربع فقط يظهر وقت أذانها تحت شريط التقدم لأنه بلا قائمة
        static void Header(Graphics g, float Wd, Snap n, Pal p, bool showTime)
        {
            float pad = 18, x = pad, w = Wd - 2 * pad;
            Txt(g, n.HeadName, Bd(34), p.P, new RectangleF(x, 14, w, 52), 1, true, p);
            if (showTime) Txt(g, "الأذان " + Fmt(n.HeadTime), R(13), p.T, new RectangleF(x, 152, w, 20), 1, true, p);
        }

        static void List(Graphics g, float Wd, Snap n, Pal p)
        {
            for (int i = 0; i < 6; i++)
            {
                RectangleF row = new RectangleF(8, 162 + i * 28, Wd - 16, 27);
                bool head = i == n.HeadIdx;
                bool past = !head && n.Day[i] <= n.Now;
                if (head) Pill(g, row, p);
                Color c = head ? p.Acc : (past || i == 1 ? p.T : p.P);
                Font f = head ? Bd(15) : R(15);
                RectangleF inner = new RectangleF(row.X + 12, row.Y, row.Width - 24, row.Height);
                Txt(g, n.Names[i], f, c, inner, 1, true, p);
                Txt(g, Fmt(n.Day[i]), f, c, inner, -1, true, p);
            }
        }

        static void Wide(Graphics g, float Wd, Snap n, Pal p)
        {
            float pad = 22, half = Wd / 2;
            Txt(g, n.HeadName, Bd(36), p.P, new RectangleF(half, 18, half - pad, 56), 1, true, p);

            float m = 10, cellW = (Wd - 2 * m) / 6f, y = 102;
            for (int i = 0; i < 6; i++)
            {
                float cx = Wd - m - (i + 1) * cellW;   // من اليمين لليسار
                bool head = i == n.HeadIdx;
                bool past = !head && n.Day[i] <= n.Now;
                if (head) Pill(g, new RectangleF(cx + 2, y, cellW - 4, 56), p);
                Txt(g, n.Names[i], head ? Bd(13) : R(13), head ? p.Acc : p.S, new RectangleF(cx, y + 6, cellW, 20), 0, true, p);
                Txt(g, Fmt(n.Day[i]), Bd(16), head ? p.Acc : (past || i == 1 ? p.T : p.P), new RectangleF(cx, y + 28, cellW, 22), 0, true, p);
            }
        }

        public static bool H24;
        static string Fmt(DateTime t)
        {
            if (H24) return t.ToString("HH:mm", CultureInfo.InvariantCulture);
            int h = t.Hour % 12; if (h == 0) h = 12;
            return h.ToString(CultureInfo.InvariantCulture) + ":" + t.Minute.ToString("00", CultureInfo.InvariantCulture) + (t.Hour < 12 ? " ص" : " م");
        }

        static void Pill(Graphics g, RectangleF r, Pal p)
        {
            using (GraphicsPath path = RR(r.X, r.Y, r.Width, r.Height, Math.Min(14, r.Height / 2)))
            {
                using (SolidBrush b = new SolidBrush(p.Pill)) g.FillPath(b, path);
                using (LinearGradientBrush br = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), p.PillRim, Color.FromArgb(p.PillRim.A / 4, p.PillRim), 90f))
                using (Pen pen = new Pen(br, 0.9f))
                    g.DrawPath(pen, path);
            }
        }

        // align: 1 يمين، 0 وسط، -1 يسار
        static readonly Dictionary<int, StringFormat> formats = new Dictionary<int, StringFormat>();

        static StringFormat Fmt(int align, bool rtl)
        {
            int k = (align + 1) * 2 + (rtl ? 1 : 0);
            StringFormat sf;
            if (formats.TryGetValue(k, out sf)) return sf;
            sf = new StringFormat(StringFormat.GenericTypographic);
            sf.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
            if (rtl) sf.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
            sf.LineAlignment = StringAlignment.Center;
            if (align == 0) sf.Alignment = StringAlignment.Center;
            else if (align == 1) sf.Alignment = rtl ? StringAlignment.Near : StringAlignment.Far;
            else sf.Alignment = rtl ? StringAlignment.Far : StringAlignment.Near;
            formats[k] = sf;
            return sf;
        }

        static void Txt(Graphics g, string s, Font f, Color c, RectangleF r, int align, bool rtl, Pal p)
        {
            StringFormat sf = Fmt(align, rtl);
            if (p.Dark)
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(c.A * 55 / 255, 0, 0, 0)))
                    g.DrawString(s, f, sb, new RectangleF(r.X, r.Y + 1f, r.Width, r.Height), sf);
            using (SolidBrush b = new SolidBrush(c)) g.DrawString(s, f, b, r, sf);
        }

        public static GraphicsPath RR(float x, float y, float w, float h, float r)
        {
            float d = Math.Min(r * 2, Math.Min(w, h));
            GraphicsPath gp = new GraphicsPath();
            gp.AddArc(x, y, d, d, 180, 90);
            gp.AddArc(x + w - d, y, d, d, 270, 90);
            gp.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            gp.AddArc(x, y + h - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }
    }

    // ───────────────────────────── Win32 ─────────────────────────────
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int W, H; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct BLEND { public byte Op, Flags, Alpha, Format; }
        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPOS { public IntPtr hwnd, after; public int x, y, cx, cy; public uint flags; }
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int key, ref BLEND blend, int flags);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] public static extern IntPtr VirtualAllocEx(IntPtr proc, IntPtr addr, IntPtr size, uint type, uint protect);
        [DllImport("kernel32.dll")] public static extern bool VirtualFreeEx(IntPtr proc, IntPtr addr, IntPtr size, uint type);
        [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr proc, IntPtr addr, ref RECT buf, IntPtr size, IntPtr read);
        [DllImport("kernel32.dll")] public static extern bool WriteProcessMemory(IntPtr proc, IntPtr addr, ref RECT buf, IntPtr size, IntPtr written);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string text);
        [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
        [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr menu);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("kernel32.dll")] public static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();

        public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

        public static void TrimMemory()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        }
    }

    // ───────────────────────────── خلفية سطح المكتب ─────────────────────────────
    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint index);
        uint GetMonitorDevicePathCount();
        Native.RECT GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId);
        void SetBackgroundColor(uint color);
        uint GetBackgroundColor();
        void SetPosition(int position);
        int GetPosition();
    }

    [ComImport, Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")] class DesktopWallpaperClass { }

    static class Wallpaper
    {
        public const int Div = 16;   // تُحفظ الخلفية بـ ١/١٦ من حجم الشاشة: صغيرة جدًا، وتكبيرها يعطي التمويه

        // يرسم خلفية الشاشة كما يعرضها ويندوز (ملء، احتواء، تمديد، توسيط، تجانب، امتداد)
        public static Bitmap Build(Screen scr, int div)
        {
            Rectangle mb = scr.Bounds;
            Bitmap outBmp = new Bitmap(Math.Max(1, mb.Width / div), Math.Max(1, mb.Height / div), PixelFormat.Format32bppPArgb);
            IDesktopWallpaper dw = null;
            try
            {
                dw = (IDesktopWallpaper)new DesktopWallpaperClass();
                string id = null;
                uint count = dw.GetMonitorDevicePathCount();
                for (uint i = 0; i < count; i++)
                {
                    string path = dw.GetMonitorDevicePathAt(i);
                    Native.RECT r = dw.GetMonitorRECT(path);
                    if (r.L == mb.Left && r.T == mb.Top && r.R == mb.Right && r.B == mb.Bottom) id = path;
                }
                uint bg = dw.GetBackgroundColor();
                string file = id != null ? dw.GetWallpaper(id) : null;
                if (string.IsNullOrEmpty(file) || !File.Exists(file))
                    file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
                int pos = dw.GetPosition();

                using (Graphics g = Graphics.FromImage(outBmp))
                {
                    g.Clear(Color.FromArgb(255, (int)(bg & 0xFF), (int)(bg >> 8 & 0xFF), (int)(bg >> 16 & 0xFF)));
                    if (!File.Exists(file)) return outBmp;
                    using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (Image img = Image.FromStream(fs, false, false))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        float k = 1f / div;
                        float W = mb.Width, H = mb.Height, iw = img.Width, ih = img.Height;
                        if (pos == 1)   // تجانب
                        {
                            using (TextureBrush tb = new TextureBrush(img))
                            {
                                tb.ScaleTransform(k, k);
                                g.FillRectangle(tb, 0, 0, outBmp.Width, outBmp.Height);
                            }
                            return outBmp;
                        }
                        RectangleF dst;
                        if (pos == 5)   // امتداد على كل الشاشات
                        {
                            Rectangle v = SystemInformation.VirtualScreen;
                            float f = Math.Max(v.Width / iw, v.Height / ih);
                            dst = new RectangleF(v.X + (v.Width - iw * f) / 2 - mb.X, v.Y + (v.Height - ih * f) / 2 - mb.Y, iw * f, ih * f);
                        }
                        else if (pos == 2) dst = new RectangleF(0, 0, W, H);                                    // تمديد
                        else if (pos == 0) dst = new RectangleF((W - iw) / 2, (H - ih) / 2, iw, ih);            // توسيط
                        else
                        {
                            float f = pos == 3 ? Math.Min(W / iw, H / ih) : Math.Max(W / iw, H / ih);         // احتواء / ملء
                            dst = new RectangleF((W - iw * f) / 2, (H - ih * f) / 2, iw * f, ih * f);
                        }
                        g.DrawImage(img, dst.X * k, dst.Y * k, dst.Width * k, dst.Height * k);
                    }
                }
            }
            catch { }
            finally { if (dw != null) Marshal.ReleaseComObject(dw); }
            return outBmp;
        }
    }

    // ───────────────────────────── أيقونات سطح المكتب ─────────────────────────────
    // الأداة تحجز مكانها: أي أيقونة تقع تحتها تُنقل لأقرب خانة فاضية في نفس الشاشة.
    static class DesktopIcons
    {
        const int LVM_GETITEMCOUNT = 0x1004, LVM_GETITEMRECT = 0x100E, LVM_SETITEMPOSITION = 0x100F,
                  LVM_GETITEMPOSITION = 0x1010, LVM_GETITEMSPACING = 0x1033, LVS_AUTOARRANGE = 0x100;

        static IntPtr FindListView()
        {
            IntPtr def = Native.FindWindowEx(Native.FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
            IntPtr w = IntPtr.Zero;
            while (def == IntPtr.Zero && (w = Native.FindWindowEx(IntPtr.Zero, w, "WorkerW", null)) != IntPtr.Zero)
                def = Native.FindWindowEx(w, IntPtr.Zero, "SHELLDLL_DefView", null);
            return def == IntPtr.Zero ? IntPtr.Zero : Native.FindWindowEx(def, IntPtr.Zero, "SysListView32", null);
        }

        // يعيد عدد الأيقونات التي نُقلت، أو -1 إذا كان الترتيب التلقائي مفعّلًا
        public static int Avoid(List<Rectangle> zones)
        {
            IntPtr lv = FindListView();
            if (lv == IntPtr.Zero || !Native.IsWindowVisible(lv)) return 0;
            if ((Native.GetWindowLong(lv, -16) & LVS_AUTOARRANGE) != 0) return -1;
            int count = Native.SendMessage(lv, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
            if (count <= 0) return 0;

            uint pid; Native.GetWindowThreadProcessId(lv, out pid);
            IntPtr proc = Native.OpenProcess(0x0008 | 0x0010 | 0x0020, false, pid);   // VM_OPERATION | VM_READ | VM_WRITE
            if (proc == IntPtr.Zero) return 0;
            IntPtr mem = Native.VirtualAllocEx(proc, IntPtr.Zero, (IntPtr)4096, 0x3000, 0x04);
            IntPtr rsz = (IntPtr)Marshal.SizeOf(typeof(Native.RECT));
            int moved = 0;
            try
            {
                if (mem == IntPtr.Zero) return 0;
                Native.POINT org = new Native.POINT();
                Native.ClientToScreen(lv, ref org);

                Rectangle[] icons = new Rectangle[count];
                for (int i = 0; i < count; i++)
                {
                    Native.RECT r = new Native.RECT();    // r.L = 0 = LVIR_BOUNDS
                    Native.WriteProcessMemory(proc, mem, ref r, rsz, IntPtr.Zero);
                    Native.SendMessage(lv, LVM_GETITEMRECT, (IntPtr)i, mem);
                    Native.ReadProcessMemory(proc, mem, ref r, rsz, IntPtr.Zero);
                    icons[i] = Rectangle.FromLTRB(r.L + org.X, r.T + org.Y, r.R + org.X, r.B + org.Y);
                }

                int sp = Native.SendMessage(lv, LVM_GETITEMSPACING, IntPtr.Zero, IntPtr.Zero).ToInt32();
                int sx = Math.Max(1, sp & 0xFFFF), sy = Math.Max(1, (sp >> 16) & 0xFFFF);

                for (int i = 0; i < count; i++)
                {
                    if (!Hits(icons[i], zones)) continue;
                    Rectangle wa = Screen.FromRectangle(icons[i]).WorkingArea;
                    int ox = Mod(icons[i].X - wa.X, sx), oy = Mod(icons[i].Y - wa.Y, sy);
                    Rectangle cell = Rectangle.Empty;
                    for (int col = 0; cell.IsEmpty && wa.X + ox + (col + 1) * sx <= wa.Right; col++)
                        for (int row = 0; wa.Y + oy + (row + 1) * sy <= wa.Bottom; row++)
                        {
                            Rectangle c = new Rectangle(wa.X + ox + col * sx, wa.Y + oy + row * sy, icons[i].Width, icons[i].Height);
                            if (Hits(c, zones) || Occupied(c, icons, i)) continue;
                            cell = c; break;
                        }
                    if (cell.IsEmpty) continue;

                    // موضع العنصر يختلف قليلًا عن حدوده، فنحافظ على نفس الفرق
                    Native.SendMessage(lv, LVM_GETITEMPOSITION, (IntPtr)i, mem);
                    Native.RECT pr = new Native.RECT();
                    Native.ReadProcessMemory(proc, mem, ref pr, rsz, IntPtr.Zero);
                    int nx = cell.X - icons[i].X + pr.L;
                    int ny = cell.Y - icons[i].Y + pr.T;
                    Native.SendMessage(lv, LVM_SETITEMPOSITION, (IntPtr)i, (IntPtr)((ny << 16) | (nx & 0xFFFF)));
                    icons[i] = cell;
                    moved++;
                }
            }
            catch { }
            finally
            {
                if (mem != IntPtr.Zero) Native.VirtualFreeEx(proc, mem, IntPtr.Zero, 0x8000);
                Native.CloseHandle(proc);
            }
            return moved;
        }

        static int Mod(int a, int b) { int m = a % b; return m < 0 ? m + b : m; }

        static bool Hits(Rectangle r, List<Rectangle> zones)
        {
            foreach (Rectangle z in zones) if (z.IntersectsWith(r)) return true;
            return false;
        }

        static bool Occupied(Rectangle c, Rectangle[] icons, int self)
        {
            Rectangle shrunk = Rectangle.Inflate(c, -4, -4);
            for (int j = 0; j < icons.Length; j++) if (j != self && icons[j].IntersectsWith(shrunk)) return true;
            return false;
        }
    }

    // ───────────────────────────── القائمة (Win32 أصلية) ─────────────────────────────
    class PopMenu
    {
        public readonly IntPtr H = Native.CreatePopupMenu();
        readonly List<Action> acts;
        public PopMenu() { acts = new List<Action>(); }
        PopMenu(List<Action> shared) { acts = shared; }

        public void Item(string text, bool check, Action a)
        {
            acts.Add(a);
            Native.AppendMenuW(H, check ? 0x8u : 0u, (UIntPtr)(uint)acts.Count, text);
        }
        public PopMenu Sub(string text)
        {
            PopMenu s = new PopMenu(acts);
            Native.AppendMenuW(H, 0x10, (UIntPtr)(ulong)s.H.ToInt64(), text);
            return s;
        }
        public void Sep() { Native.AppendMenuW(H, 0x800, UIntPtr.Zero, null); }

        public Action Show(IntPtr owner, Point pt)
        {
            Native.SetForegroundWindow(owner);
            // TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_LAYOUTRTL
            int cmd = Native.TrackPopupMenuEx(H, 0x100 | 0x2 | 0x8000, pt.X, pt.Y, owner, IntPtr.Zero);
            Native.PostMessage(owner, 0, IntPtr.Zero, IntPtr.Zero);
            Native.DestroyMenu(H);
            return cmd > 0 ? acts[cmd - 1] : null;
        }
    }

    class MenuOwner : NativeWindow
    {
        public MenuOwner()
        {
            CreateParams cp = new CreateParams();
            cp.Style = unchecked((int)0x80000000);
            cp.ExStyle = 0x80;
            CreateHandle(cp);
        }
    }

    // ───────────────────────────── نافذة الأداة ─────────────────────────────
    class Widget : NativeWindow
    {
        public Screen Scr;
        readonly App app;
        IntPtr memDc, dib;
        Bitmap bmp, layer;
        string layerKey;
        int bw, bh;
        Bitmap desk;      // خلفية هذه الشاشة مصغّرة

        public Widget(App a, Screen s)
        {
            app = a; Scr = s;
            CreateParams cp = new CreateParams();
            cp.Caption = "مواقيت الصلاة";
            cp.Style = unchecked((int)0x80000000);              // WS_POPUP
            cp.ExStyle = 0x80000 | 0x80 | 0x08000000;           // LAYERED | TOOLWINDOW | NOACTIVATE
            cp.Parent = Native.FindWindow("Progman", null);     // مملوكة لسطح المكتب: تبقى عند "إظهار سطح المكتب"
            cp.X = s.WorkingArea.X; cp.Y = s.WorkingArea.Y; cp.Width = 1; cp.Height = 1;
            CreateHandle(cp);
            memDc = Native.CreateCompatibleDC(IntPtr.Zero);
            RefreshDesk();
        }

        public void RefreshDesk()
        {
            Bitmap old = desk;
            desk = Wallpaper.Build(Scr, Wallpaper.Div);
            if (old != null) old.Dispose();
            layerKey = null;
        }

        public float Scale
        {
            get
            {
                uint dpi = 96;
                try { dpi = Native.GetDpiForWindow(Handle); } catch { }
                if (dpi == 0) dpi = 96;
                return dpi / 96f * (float)app.S.Size;
            }
        }

        public Size PixelSize
        {
            get
            {
                Size b = Painter.OuterSize(app.S.Layout);
                float sc = Scale;
                return new Size((int)Math.Ceiling(b.Width * sc), (int)Math.Ceiling(b.Height * sc));
            }
        }

        // حدود البطاقة نفسها على الشاشة (بدون مساحة الظل)
        public Rectangle CardBounds
        {
            get { int m = (int)(Painter.Margin * Scale); return Rectangle.Inflate(Bounds, -m, -m); }
        }

        public void Render(Snap n)
        {
            float sc = Scale;
            Size px = PixelSize;
            if (px.Width != bw || px.Height != bh) Alloc(px.Width, px.Height);

            Rectangle wb = Bounds, mb = Scr.Bounds;
            string key = n.StaticKey + "|" + sc.ToString(CultureInfo.InvariantCulture) + "|" + wb.X + "," + wb.Y;
            if (key != layerKey)
            {
                float d = Wallpaper.Div, m = Painter.Margin * sc;
                RectangleF src = new RectangleF((wb.X + m - mb.X) / d, (wb.Y + m - mb.Y) / d, (bw - 2 * m) / d, (bh - 2 * m) / d);
                using (Graphics g = Graphics.FromImage(layer))
                {
                    g.Clear(Color.Transparent);
                    Painter.PaintStatic(g, sc, app.S, n, desk, src);
                }
                layerKey = key;
            }
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImageUnscaled(layer, 0, 0);
                g.CompositingMode = CompositingMode.SourceOver;
                Painter.PaintDynamic(g, sc, app.S, n);
            }
            Native.SIZE sz; sz.W = bw; sz.H = bh;
            Native.POINT origin = new Native.POINT();
            Native.BLEND bl; bl.Op = 0; bl.Flags = 0; bl.Alpha = 255; bl.Format = 1;
            Native.UpdateLayeredWindow(Handle, IntPtr.Zero, IntPtr.Zero, ref sz, memDc, ref origin, 0, ref bl, 2);
        }

        void Alloc(int w, int h)
        {
            if (bmp != null) bmp.Dispose();
            if (layer != null) layer.Dispose();
            layer = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            layerKey = null;
            Native.BITMAPINFOHEADER bi = new Native.BITMAPINFOHEADER();
            bi.biSize = 40; bi.biWidth = w; bi.biHeight = -h; bi.biPlanes = 1; bi.biBitCount = 32;
            IntPtr bits;
            IntPtr nd = Native.CreateDIBSection(IntPtr.Zero, ref bi, 0, out bits, IntPtr.Zero, 0);
            Native.SelectObject(memDc, nd);
            if (dib != IntPtr.Zero) Native.DeleteObject(dib);
            dib = nd;
            bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
            bw = w; bh = h;
        }

        public void MoveTo(int x, int y)
        {
            Native.SetWindowPos(Handle, Native.HWND_BOTTOM, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        public Rectangle Bounds
        {
            get { Native.RECT r; Native.GetWindowRect(Handle, out r); return Rectangle.FromLTRB(r.L, r.T, r.R, r.B); }
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case 0x84:   // WM_NCHITTEST: السحب عبر النظام نفسه إن لم يكن المكان مثبتًا
                    m.Result = (IntPtr)(app.S.Locked ? 1 : 2);
                    return;
                case 0xA3:   // WM_NCLBUTTONDBLCLK
                case 0xA4:   // WM_NCRBUTTONDOWN
                case 0x204:  // WM_RBUTTONDOWN
                case 0x7B:   // WM_CONTEXTMENU
                    return;
                case 0xA5:   // WM_NCRBUTTONUP
                case 0x205:  // WM_RBUTTONUP
                    app.ShowMenu();
                    return;
                case 0x21:   // WM_MOUSEACTIVATE
                    m.Result = (IntPtr)3;  // MA_NOACTIVATE
                    return;
                case 0x46:   // WM_WINDOWPOSCHANGING: ابقَ دائمًا في القاع
                    Native.WINDOWPOS wp = (Native.WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(Native.WINDOWPOS));
                    if ((wp.flags & Native.SWP_NOZORDER) == 0)
                    {
                        wp.after = Native.HWND_BOTTOM;
                        Marshal.StructureToPtr(wp, m.LParam, false);
                    }
                    break;
                case 0x232:  // WM_EXITSIZEMOVE
                    app.OnMoved(this);
                    break;
                case 0x03:   // WM_MOVE: أثناء السحب يُعاد رسم الزجاج ليطابق ما خلفه
                    if (bmp != null) app.Redraw(this);
                    break;
                case 0x2E0:  // WM_DPICHANGED
                    app.RenderOne(this);
                    m.Result = IntPtr.Zero;
                    return;
            }
            base.WndProc(ref m);
        }

        public void Destroy()
        {
            DestroyHandle();
            if (bmp != null) bmp.Dispose();
            if (layer != null) layer.Dispose();
            if (desk != null) desk.Dispose();
            if (memDc != IntPtr.Zero) Native.DeleteDC(memDc);
            if (dib != IntPtr.Zero) Native.DeleteObject(dib);
        }
    }

    // ───────────────────────────── التطبيق ─────────────────────────────
    class App : ApplicationContext
    {
        public Settings S;
        readonly List<Widget> widgets = new List<Widget>();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly Dictionary<DateTime, DateTime[]> cache = new Dictionary<DateTime, DateTime[]>();
        readonly MenuOwner owner = new MenuOwner();
        Snap last;
        DateTime nextIconCheck;
        int stamp;   // يزيد مع كل تغيير في الإعدادات

        static readonly string[] BaseNames = { "الفجر", "الشروق", "الظهر", "العصر", "المغرب", "العشاء" };
        static readonly int[] Prayers = { 0, 2, 3, 4, 5 };
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        App(Settings s) { S = s; }

        // وضع المعاينة: PrayerWidget.exe --preview <مجلد> يرسم كل الأشكال فوق خلفية سطح المكتب
        public static void Preview(string dir, DateTime at)
        {
            Settings s = Settings.Load();
            App a = new App(s);
            Screen scr = Screen.PrimaryScreen;
            Bitmap sharp = Wallpaper.Build(scr, 1);                 // للخلفية حول الأداة في الصورة
            Bitmap desk = Wallpaper.Build(scr, Wallpaper.Div);      // نفس ما تستخدمه الأداة فعليًا
            float sc = 1.5f;
            foreach (string layout in new string[] { "square", "wide", "tall" })
                foreach (string variant in new string[] { "dark", "light" })
                {
                    s.Layout = layout; s.Theme = variant == "light" ? "light" : "dark";
                    Size o = Painter.OuterSize(layout);
                    int w = (int)(o.Width * sc), h = (int)(o.Height * sc), m = 30, cm = (int)(Painter.Margin * sc);
                    // الأداة في الزاوية العلوية اليمنى من الشاشة الرئيسية
                    int wx = scr.Bounds.Width - w - 2 * m, wy = 2 * m;
                    using (Bitmap outBmp = new Bitmap(w + 2 * m, h + 2 * m, PixelFormat.Format32bppPArgb))
                    using (Graphics g = Graphics.FromImage(outBmp))
                    {
                        g.DrawImage(sharp, new Rectangle(0, 0, outBmp.Width, outBmp.Height),
                            new Rectangle(wx - m, wy - m, outBmp.Width, outBmp.Height), GraphicsUnit.Pixel);
                        Snap n = a.MakeSnap(at);
                        float d = Wallpaper.Div;
                        RectangleF src = new RectangleF((wx + cm) / d, (wy + cm) / d, (w - 2 * cm) / d, (h - 2 * cm) / d);
                        using (Bitmap layerBmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
                        {
                            using (Graphics gl = Graphics.FromImage(layerBmp))
                            {
                                Painter.PaintStatic(gl, sc, s, n, desk, src);
                                gl.ResetTransform();
                                Painter.PaintDynamic(gl, sc, s, n);
                            }
                            g.DrawImageUnscaled(layerBmp, m, m);
                        }
                        outBmp.Save(Path.Combine(dir, layout + "-" + variant + ".png"), ImageFormat.Png);
                    }
                }
        }

        public App()
        {
            S = Settings.Load();
            Painter.H24 = S.H24;


            timer.Tick += delegate { Tick(); };
            Rebuild();

            SystemEvents.DisplaySettingsChanged += delegate { Rebuild(); };
            SystemEvents.UserPreferenceChanged += delegate(object o, UserPreferenceChangedEventArgs e)
            {
                if (e.Category == UserPreferenceCategory.General)              // ربما تغيّر وضع ويندوز (فاتح/داكن)
                {
                    bool d = Settings.ReadSystemDark();
                    if (d != Settings.SystemDark) { Settings.SystemDark = d; stamp++; Redraw(null); }
                    return;
                }
                if (e.Category != UserPreferenceCategory.Desktop) return;   // تغيّرت خلفية سطح المكتب
                foreach (Widget w in widgets) w.RefreshDesk();
                Native.TrimMemory();
                Redraw(null);
            };
            // ملف جديد على سطح المكتب قد يقع تحت الأداة: نفحص كل ١٠ ثوانٍ حتى لو كانت الثواني مخفية
            // (الفحص أقل من جزء من الملّي ثانية، ولا يعمل إذا كانت الأداة فوق الاختصارات)
            System.Windows.Forms.Timer iconTimer = new System.Windows.Forms.Timer();
            iconTimer.Interval = 10000;
            iconTimer.Tick += delegate { ClearIcons(); nextIconCheck = DateTime.Now.AddSeconds(10); };
            iconTimer.Start();

            SystemEvents.TimeChanged += delegate { cache.Clear(); Tick(); };
            SystemEvents.PowerModeChanged += delegate(object o, PowerModeChangedEventArgs e)
            { if (e.Mode == PowerModes.Resume) { cache.Clear(); Tick(); } };

            // تقليص الذاكرة بعد الإقلاع
            System.Windows.Forms.Timer trim = new System.Windows.Forms.Timer();
            trim.Interval = 4000;
            trim.Tick += delegate { trim.Stop(); trim.Dispose(); Native.TrimMemory(); };
            trim.Start();
        }

        // ── الأوقات ──
        DateTime[] Times(DateTime day)
        {
            DateTime[] t;
            if (cache.TryGetValue(day, out t)) return t;
            if (cache.Count > 6) cache.Clear();
            t = PrayerCalc.Compute(day, S.Lat, S.Lng);
            cache[day] = t;
            return t;
        }

        static string[] NamesFor(DateTime day)
        {
            string[] n = (string[])BaseNames.Clone();
            if (day.DayOfWeek == DayOfWeek.Friday) n[2] = "الجمعة";
            return n;
        }

        Snap MakeSnap(DateTime now)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            DateTime d = now.Date;
            DateTime[] today = Times(d);
            Snap s = new Snap();
            s.Now = now;

            // الصلاة القادمة
            int next = -1;
            foreach (int i in Prayers) if (today[i] > now) { next = i; break; }
            DateTime nextDay = d;
            if (next < 0) { nextDay = d.AddDays(1); next = 0; }
            DateTime[] nd = Times(nextDay);
            s.NextTime = nd[next];

            // الصلاة السابقة (آخر أذان)
            int prev = -1;
            for (int k = Prayers.Length - 1; k >= 0; k--) if (today[Prayers[k]] <= now) { prev = Prayers[k]; break; }
            DateTime prevDay = d;
            if (prev < 0) { prevDay = d.AddDays(-1); prev = 5; }
            DateTime prevT = Times(prevDay)[prev];

            TimeSpan left = s.NextTime - now, since = now - prevT;
            double span = (s.NextTime - prevT).TotalSeconds;
            s.Progress = span > 0 ? (float)(since.TotalSeconds / span) : 0;

            // بعد الأذان مباشرة (أول نصف ساعة) يُعرض "مضى على الأذان"، وبعدها "بقي" للصلاة القادمة
            s.Since = since.TotalMinutes < SinceWindowMinutes;
            DateTime shown = s.Since ? prevDay : nextDay;
            s.Day = Times(shown);
            s.Names = NamesFor(shown);
            s.HeadIdx = s.Since ? prev : next;
            s.HeadName = s.Names[s.HeadIdx];
            s.HeadTime = s.Day[s.HeadIdx];

            if (s.Since)
            {
                s.Prefix = "مضى";
                long sec = (long)since.TotalSeconds;
                if (sec < 60) { s.CounterText = sec.ToString(c); s.CounterUnit = SecWord(sec); s.FastTick = true; }
                else if (S.Seconds) s.CounterText = Dur(sec, true);
                else { long m = sec / 60; s.CounterText = m.ToString(c); s.CounterUnit = MinWord(m); }
            }
            else
            {
                s.Prefix = "بقي";
                long leftMin = (long)Math.Ceiling(left.TotalMinutes);
                if (S.Seconds) s.CounterText = Dur((long)Math.Ceiling(left.TotalSeconds), true);
                else if (leftMin < 60) { s.CounterText = leftMin.ToString(c); s.CounterUnit = MinWord(leftMin); }
                else s.CounterText = Dur(leftMin * 60, false);
            }
            s.StaticKey = stamp + "|" + s.HeadTime.Ticks + "|" + s.Day[0].Ticks + "|" + s.Since;
            return s;
        }

        const int SinceWindowMinutes = 30;

        // تمييز العدد: ١ دقيقة، ٢ دقيقتان، ٣–١٠ دقائق، ١١ فأكثر دقيقة
        static string MinWord(long m)
        {
            if (m == 2) return "دقيقتان";
            if (m >= 3 && m <= 10) return "دقائق";
            return "دقيقة";
        }

        static string SecWord(long s)
        {
            if (s == 2) return "ثانيتان";
            if (s >= 3 && s <= 10) return "ثوانٍ";
            return "ثانية";
        }

        static string Dur(long sec, bool withSeconds)
        {
            if (sec < 0) sec = 0;
            long h = sec / 3600, m = sec / 60 % 60, s = sec % 60;
            CultureInfo c = CultureInfo.InvariantCulture;
            if (!withSeconds) return h.ToString(c) + ":" + m.ToString("00", c);
            return h.ToString(c) + ":" + m.ToString("00", c) + ":" + s.ToString("00", c);
        }

        // ── الحلقة: تستيقظ مرة واحدة عند بداية كل ثانية (أو كل دقيقة) ──
        void Tick()
        {
            timer.Stop();
            DateTime now = DateTime.Now;
            last = MakeSnap(now);
            foreach (Widget w in widgets) w.Render(last);

            if (now >= nextIconCheck) { ClearIcons(); nextIconCheck = now.AddSeconds(10); }

            // كل ثانية إذا كانت الثواني ظاهرة أو في الدقيقة الأولى بعد الأذان، وإلا مرة كل دقيقة
            int ms = S.Seconds || last.FastTick ? 1000 - now.Millisecond : (60 - now.Second) * 1000 - now.Millisecond;
            timer.Interval = Math.Max(50, ms + 15);
            timer.Start();
        }

        public void RenderOne(Widget w)
        {
            if (last == null) last = MakeSnap(DateTime.Now);
            w.Render(last);
            Place(w);
        }

        // إعادة رسم بدون تحريك (null = كل النوافذ)
        public void Redraw(Widget only)
        {
            if (last == null) last = MakeSnap(DateTime.Now);
            foreach (Widget w in widgets) if (only == null || w == only) w.Render(last);
        }

        // إذا كان برنامج بكامل الشاشة في المقدمة، تنزل الأداة التي على شاشته خلفه، وترجع فوق بعد إغلاقه
        // يبعد أيقونات سطح المكتب عن مكان الأدوات (إلا إذا اختار المستخدم أن تكون الأداة فوقها)
        void ClearIcons()
        {
            if (S.OverIcons) return;
            List<Rectangle> zones = new List<Rectangle>();
            foreach (Widget w in widgets) zones.Add(Rectangle.Inflate(w.CardBounds, 6, 6));
            DesktopIcons.Avoid(zones);
        }

        // ── النوافذ والشاشات ──
        void Rebuild()
        {
            foreach (Widget w in widgets) w.Destroy();
            widgets.Clear();

            if (S.Screens == "all")
                foreach (Screen sc in Screen.AllScreens) widgets.Add(new Widget(this, sc));
            else
            {
                Screen target = Screen.PrimaryScreen;
                foreach (Screen sc in Screen.AllScreens) if (sc.DeviceName == S.Screens) target = sc;
                widgets.Add(new Widget(this, target));
            }

            last = MakeSnap(DateTime.Now);
            foreach (Widget w in widgets) { w.Render(last); Place(w); }
            nextIconCheck = DateTime.MinValue;
            Tick();
        }

        void Place(Widget w)
        {
            Size px = w.PixelSize;
            Rectangle wa = w.Scr.WorkingArea, b = w.Scr.Bounds;
            Point p;
            if (S.Pos.TryGetValue(w.Scr.DeviceName, out p)) p = new Point(b.X + p.X, b.Y + p.Y);
            else
            {
                int margin = (int)(24 * w.Scale);
                p = new Point(wa.Right - px.Width - margin, wa.Top + margin);
            }
            p = Clamp(p, px, wa);
            w.MoveTo(p.X, p.Y);
        }

        static Point Clamp(Point p, Size s, Rectangle wa)
        {
            int x = Math.Max(wa.Left, Math.Min(p.X, wa.Right - s.Width));
            int y = Math.Max(wa.Top, Math.Min(p.Y, wa.Bottom - s.Height));
            return new Point(x, y);
        }

        public void OnMoved(Widget w)
        {
            Rectangle r = w.Bounds;
            if (S.Screens != "all")
            {
                // في وضع الشاشة الواحدة: سحبها لشاشة أخرى ينقلها إليها
                Screen now = Screen.FromRectangle(r);
                if (now.DeviceName != w.Scr.DeviceName) { w.Scr = now; S.Screens = now.DeviceName; }
            }
            Rectangle b = w.Scr.Bounds;
            Point p = Clamp(r.Location, r.Size, w.Scr.WorkingArea);
            S.Pos[w.Scr.DeviceName] = new Point(p.X - b.X, p.Y - b.Y);
            S.Save();
            RenderOne(w);
            ClearIcons();
        }

        // ── القائمة ──
        public void ShowMenu()
        {
            Point pt = Cursor.Position;
            PopMenu m = new PopMenu();

            PopMenu lay = m.Sub("الشكل");
            lay.Item("مربع — الصلاة القادمة فقط", S.Layout == "square", delegate { Set(delegate { S.Layout = "square"; }); });
            lay.Item("مستطيل عرضي — مع صلوات اليوم", S.Layout == "wide", delegate { Set(delegate { S.Layout = "wide"; }); });
            lay.Item("مستطيل طولي — مع صلوات اليوم", S.Layout == "tall", delegate { Set(delegate { S.Layout = "tall"; }); });

            PopMenu city = m.Sub("المدينة: " + S.CityName);
            PopMenu cur = null; string curCountry = null;
            foreach (City c in City.All)
            {
                if (c.Country != curCountry) { curCountry = c.Country; cur = city.Sub(c.Country); }
                City cc = c;
                cur.Item(c.Name, S.CityName == c.Name && S.Country == c.Country, delegate
                {
                    Set(delegate { S.Country = cc.Country; S.CityName = cc.Name; S.Lat = cc.Lat; S.Lng = cc.Lng; });
                });
            }
            city.Sep();
            city.Item("موقع مخصص (إحداثيات)...", S.Country == "مخصص", CustomLocation);

            Screen[] screens = Screen.AllScreens;
            PopMenu scr = m.Sub("الشاشات");
            scr.Item("كل الشاشات (لكل شاشة مكانها الخاص)", S.Screens == "all", delegate { Set(delegate { S.Screens = "all"; }); });
            scr.Sep();
            for (int i = 0; i < screens.Length; i++)
            {
                Screen sc = screens[i];
                string label = "شاشة " + (i + 1) + (sc.Primary ? " (الرئيسية)" : "") + "  —  " + sc.Bounds.Width + "×" + sc.Bounds.Height;
                string dev = sc.DeviceName;
                scr.Item("فقط: " + label, S.Screens == dev || (S.Screens != "all" && sc.Primary && !HasScreen(S.Screens)),
                    delegate { Set(delegate { S.Screens = dev; }); });
            }

            PopMenu look = m.Sub("المظهر");
            look.Item("تلقائي (حسب وضع ويندوز)", S.Theme == "auto", delegate { Set(delegate { S.Theme = "auto"; }); });
            look.Item("زجاجي داكن", S.Theme == "dark", delegate { Set(delegate { S.Theme = "dark"; }); });
            look.Item("زجاجي فاتح", S.Theme == "light", delegate { Set(delegate { S.Theme = "light"; }); });

            PopMenu size = m.Sub("الحجم");
            double[] sizes = { 0.85, 1.0, 1.2, 1.45 };
            string[] sizeNames = { "صغير", "متوسط", "كبير", "كبير جدًا" };
            for (int i = 0; i < sizes.Length; i++)
            {
                double v = sizes[i];
                size.Item(sizeNames[i], Math.Abs(S.Size - v) < 0.01, delegate { Set(delegate { S.Size = v; }); });
            }

            m.Sep();
            m.Item("فوق اختصارات سطح المكتب (لا تحرّكها)", S.OverIcons, delegate { Set(delegate { S.OverIcons = !S.OverIcons; }); });
            m.Item("عرض الثواني", S.Seconds, delegate { Set(delegate { S.Seconds = !S.Seconds; }); });
            m.Item("نظام 24 ساعة", S.H24, delegate { Set(delegate { S.H24 = !S.H24; }); });
            m.Item("تثبيت المكان (منع السحب)", S.Locked, delegate { Set(delegate { S.Locked = !S.Locked; }); });
            m.Item("إرجاع المكان الافتراضي", false, delegate { Set(delegate { S.Pos.Clear(); }); });
            m.Sep();
            m.Item("التشغيل مع بدء ويندوز", IsStartup(), ToggleStartup);
            m.Item("خروج", false, Quit);

            Action a = m.Show(owner.Handle, pt);
            if (a != null) a();
            Native.TrimMemory();
        }

        static bool HasScreen(string dev)
        {
            foreach (Screen s in Screen.AllScreens) if (s.DeviceName == dev) return true;
            return false;
        }

        void Set(Action change)
        {
            string oldScreens = S.Screens;
            change();
            stamp++;
            nextIconCheck = DateTime.MinValue;   // الحجم أو المكان تغيّر
            Painter.H24 = S.H24;
            cache.Clear();
            S.Save();
            if (S.Screens != oldScreens || widgets.Count == 0) Rebuild();
            else
            {
                last = MakeSnap(DateTime.Now);
                foreach (Widget w in widgets) { w.Render(last); Place(w); }
                Tick();
            }
        }

        void CustomLocation()
        {
            using (LocationForm f = new LocationForm(S))
            {
                if (f.ShowDialog() != DialogResult.OK) return;
                Set(delegate
                {
                    S.Country = "مخصص"; S.CityName = f.PlaceName; S.Lat = f.Lat; S.Lng = f.Lng;
                });
            }
        }

        static bool IsStartup()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue("PrayerWidget") != null;
        }

        static void ToggleStartup()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (k.GetValue("PrayerWidget") != null) k.DeleteValue("PrayerWidget");
                else k.SetValue("PrayerWidget", "\"" + Application.ExecutablePath + "\"");
            }
        }

        void Quit()
        {
            timer.Stop();
            foreach (Widget w in widgets) w.Destroy();
            widgets.Clear();
            owner.DestroyHandle();
            ExitThread();
        }
    }

    // ───────────────────────────── نافذة الإحداثيات ─────────────────────────────
    class LocationForm : Form
    {
        readonly TextBox name = new TextBox(), lat = new TextBox(), lng = new TextBox();
        public string PlaceName; public double Lat, Lng;

        public LocationForm(Settings s)
        {
            Text = "موقع مخصص";
            RightToLeft = RightToLeft.Yes; RightToLeftLayout = true;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font(Painter.Thmanyah ? "thmanyah sans" : "Segoe UI", 10f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(340, 210);

            CultureInfo inv = CultureInfo.InvariantCulture;
            AddRow("اسم المكان", name, 16, s.CityName);
            AddRow("خط العرض", lat, 56, s.Lat.ToString(inv));
            AddRow("خط الطول", lng, 96, s.Lng.ToString(inv));

            Label hint = new Label();
            hint.Text = "مثال: الرياض 24.7136 و 46.6753 (من خرائط قوقل)";
            hint.ForeColor = SystemColors.GrayText;
            hint.SetBounds(16, 132, 308, 22);
            Controls.Add(hint);

            Button ok = new Button(); ok.Text = "حفظ"; ok.SetBounds(16, 164, 100, 32);
            Button cancel = new Button(); cancel.Text = "إلغاء"; cancel.SetBounds(124, 164, 100, 32);
            cancel.DialogResult = DialogResult.Cancel;
            ok.Click += delegate
            {
                double a, b;
                if (double.TryParse(lat.Text.Trim(), NumberStyles.Float, inv, out a) &&
                    double.TryParse(lng.Text.Trim(), NumberStyles.Float, inv, out b) &&
                    Math.Abs(a) <= 90 && Math.Abs(b) <= 180)
                {
                    Lat = a; Lng = b;
                    PlaceName = name.Text.Trim().Length > 0 ? name.Text.Trim() : "موقعي";
                    DialogResult = DialogResult.OK;
                }
                else MessageBox.Show(this, "تأكد من كتابة الإحداثيات بشكل صحيح.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            };
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
        }

        void AddRow(string label, TextBox box, int y, string value)
        {
            Label l = new Label(); l.Text = label; l.SetBounds(16, y + 3, 90, 24);
            box.SetBounds(110, y, 214, 28); box.Text = value;
            Controls.Add(l); Controls.Add(box);
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // --preview <مجلد> [HH:mm:ss] : صور للأشكال، اختياريًا في وقت معيّن من اليوم
            if (args.Length >= 2 && args[0] == "--preview")
            {
                DateTime at = DateTime.Now;
                if (args.Length >= 3) at = DateTime.Today + TimeSpan.Parse(args[2], CultureInfo.InvariantCulture);
                App.Preview(args[1], at);
                return;
            }
            bool created;
            using (Mutex mutex = new Mutex(true, "PrayerWidget_SingleInstance", out created))
            {
                if (!created) return;
                try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }   // Per-Monitor V2
                catch { try { Native.SetProcessDPIAware(); } catch { } }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new App());
            }
        }
    }
}
