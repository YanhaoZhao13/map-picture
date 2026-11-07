// Dependency-free layout helper for Windows PowerShell 5.1 / .NET Framework.
// All coordinates and text box sizes are fractions of the upright photograph.
// PinU / PinV alone are fractions of the unplaced raw map.
// Scores are confidence heuristics, never semantic segmentation accuracy.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;

public class MapLayoutOptions {
    public double PinU = .5, PinV = .5;
    public double LabelWidth = .15, LabelHeight = .06;
    public double TitleWidth = .18, TitleHeight = .04;
    public double MinArea = .15, MaxArea = .25, PreferredArea = .20;
    public double MinWidth = .22, MaxWidth = .78, MaxBottom = .80;
    public string AllowedMaskPath = null, ClearMaskPath = null;
}

public class LayoutCandidate {
    public double Left, Top, Width, Height, Area;
    public double BlueSupport, SkySupport, LabelSupport, Score;
    public double TitleX, TitleY;
    public double PinX, PinY, LabelX, LabelY;
    public double LabelSkySupport, TitleSkySupport, PinSkySupport;
    public bool TitleLabelOverlap;
    public bool RequiresReview = true;
    public string Warning = "Estimated sky/clear-sky confidence only. Inspect clouds, subject edges, labels and map detail before delivery.";
}

public class SkyLayout {
    public int W, H, Examined;
    public double[] Blue, Sky, Texture;
    public LayoutCandidate Best;
    public List<LayoutCandidate> Alternatives = new List<LayoutCandidate>();
    public string LastError { get; private set; }
    public readonly string ConfidenceWarning = "Color, connectivity and texture estimate suitable sky; they do not identify sky or clouds with semantic certainty. Manual allowed/clear masks can override the estimates.";

    readonly MapLayoutOptions options;
    readonly bool manualAllowed;
    double aspect;
    readonly List<double[]> samples = new List<double[]>();
    const double Margin = .008;

    static double Clamp(double v) { return Math.Max(0, Math.Min(1, v)); }
    static double Smooth(double v) { v = Clamp(v); return v * v * (3 - 2 * v); }
    static bool Finite(double v) { return !Double.IsNaN(v) && !Double.IsInfinity(v); }
    static void Fraction(double v, string name, bool allowZero) {
        if (!Finite(v) || v > 1 || (allowZero ? v < 0 : v <= 0))
            throw new ArgumentOutOfRangeException(name, "Expected a finite fraction in " + (allowZero ? "[0,1]." : "(0,1]."));
    }
    static MapLayoutOptions CopyAndValidate(MapLayoutOptions o) {
        if (o == null) o = new MapLayoutOptions();
        Fraction(o.PinU, "PinU", true); Fraction(o.PinV, "PinV", true);
        Fraction(o.LabelWidth, "LabelWidth", false); Fraction(o.LabelHeight, "LabelHeight", false);
        Fraction(o.TitleWidth, "TitleWidth", false); Fraction(o.TitleHeight, "TitleHeight", false);
        Fraction(o.MinArea, "MinArea", false); Fraction(o.MaxArea, "MaxArea", false);
        Fraction(o.PreferredArea, "PreferredArea", false);
        Fraction(o.MinWidth, "MinWidth", false); Fraction(o.MaxWidth, "MaxWidth", false);
        Fraction(o.MaxBottom, "MaxBottom", false);
        if (o.MinArea > o.MaxArea || o.MinWidth > o.MaxWidth)
            throw new ArgumentException("Minimum area/width must not exceed its maximum.");
        return new MapLayoutOptions { PinU = o.PinU, PinV = o.PinV, LabelWidth = o.LabelWidth,
            LabelHeight = o.LabelHeight, TitleWidth = o.TitleWidth, TitleHeight = o.TitleHeight,
            MinArea = o.MinArea, MaxArea = o.MaxArea, PreferredArea = o.PreferredArea,
            MinWidth = o.MinWidth, MaxWidth = o.MaxWidth, MaxBottom = o.MaxBottom,
            AllowedMaskPath = o.AllowedMaskPath, ClearMaskPath = o.ClearMaskPath };
    }

    double At(double[] field, double x, double y) {
        if (x < 0 || y < 0 || x >= 1 || y >= 1) return 0;
        return field[Math.Min(H - 1, (int)(y * H)) * W + Math.Min(W - 1, (int)(x * W))];
    }
    double Interpolate(double[] field, double x, double y) {
        if (x < 0 || y < 0 || x > 1 || y > 1) return 0;
        double xx = x * W - .5, yy = y * H - .5;
        int ix = (int)Math.Floor(xx), iy = (int)Math.Floor(yy);
        double fx = xx - ix, fy = yy - iy;
        int x0 = Math.Max(0, Math.Min(W - 1, ix)), x1 = Math.Max(0, Math.Min(W - 1, ix + 1));
        int y0 = Math.Max(0, Math.Min(H - 1, iy)), y1 = Math.Max(0, Math.Min(H - 1, iy + 1));
        return (field[y0 * W + x0] * (1 - fx) + field[y0 * W + x1] * fx) * (1 - fy)
             + (field[y1 * W + x0] * (1 - fx) + field[y1 * W + x1] * fx) * fy;
    }
    double Box(double[] field, double x, double y, double width, double height) {
        if (x < Margin || y < Margin || x + width > 1 - Margin || y + height > options.MaxBottom)
            return 0;
        double sum = 0;
        for (int j = 0; j < 5; j++) for (int i = 0; i < 8; i++)
            sum += At(field, x + width * (i + .5) / 8, y + height * (j + .5) / 5);
        return sum / 40;
    }

    double[] ReadMask(string path, double photoAspect) {
        using (Bitmap source = new Bitmap(path)) {
            if (Math.Abs((double)source.Width / source.Height / photoAspect - 1) > .01)
                throw new ArgumentException("Mask aspect ratio must match the upright photograph: " + path);
            using (Bitmap resized = new Bitmap(W, H)) {
                using (Graphics g = Graphics.FromImage(resized)) {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(source, 0, 0, W, H);
                }
                double[] result = new double[W * H];
                for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) {
                    Color p = resized.GetPixel(x, y);
                    result[y * W + x] = (p.R * .299 + p.G * .587 + p.B * .114) / 255 * p.A / 255;
                }
                return result;
            }
        }
    }

    public SkyLayout(string uprightPhoto, string rawMap, MapLayoutOptions configuration) {
        options = CopyAndValidate(configuration);
        manualAllowed = !String.IsNullOrWhiteSpace(options.AllowedMaskPath);
        using (Bitmap source = new Bitmap(uprightPhoto)) {
            if (source.Width < 8 || source.Height < 8 || source.Width / (double)source.Height > 12 || source.Height / (double)source.Width > 12)
                throw new ArgumentException("Photo dimensions/aspect are unsuitable for reliable sky analysis. Use an explicit reviewed placement instead.");
            double sourceAspect = source.Width / (double)source.Height;
            W = sourceAspect >= 1 ? 360 : Math.Max(1, (int)Math.Round(360 * sourceAspect));
            H = sourceAspect >= 1 ? Math.Max(1, (int)Math.Round(360 / sourceAspect)) : 360;
            using (Bitmap small = new Bitmap(W, H)) {
                using (Graphics g = Graphics.FromImage(small)) {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(source, 0, 0, W, H);
                }
                AnalyseSky(small);
            }
            if (manualAllowed) Sky = ReadMask(options.AllowedMaskPath, sourceAspect);
            if (!String.IsNullOrWhiteSpace(options.ClearMaskPath)) Blue = ReadMask(options.ClearMaskPath, sourceAspect);
            for (int i = 0; i < Blue.Length; i++) Blue[i] = Clamp(Blue[i]) * Sky[i];
            ReadMap(rawMap, sourceAspect);
        }
    }

    void AnalyseSky(Bitmap small) {
        Blue = new double[W * H]; Sky = new double[W * H]; Texture = new double[W * H];
        double[] luminance = new double[W * H]; bool[] potential = new bool[W * H];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) {
            int i = y * W + x; Color p = small.GetPixel(x, y);
            luminance[i] = p.R * .299 + p.G * .587 + p.B * .114;
            double br = (p.B - p.R) / (double)Math.Max(1, (int)p.B);
            double bg = (p.B - p.G) / (double)Math.Max(1, (int)p.B);
            Blue[i] = Smooth((br - .075) / .23) * Smooth((bg - .005) / .13);
            potential[i] = p.B > p.R * 1.025 && p.B >= p.G * .99 && luminance[i] > 72;
        }
        Queue<int> queue = new Queue<int>();
        for (int x = 0; x < W; x++) if (potential[x]) { Sky[x] = 1; queue.Enqueue(x); }
        int[] dx = { -1, 1, 0, 0 }, dy = { 0, 0, -1, 1 };
        while (queue.Count > 0) {
            int i = queue.Dequeue(), x = i % W, y = i / W;
            for (int k = 0; k < 4; k++) {
                int xx = x + dx[k], yy = y + dy[k];
                if (xx < 0 || yy < 0 || xx >= W || yy >= H) continue;
                int ni = yy * W + xx;
                if (potential[ni] && Sky[ni] == 0) { Sky[ni] = 1; queue.Enqueue(ni); }
            }
        }
        int radius = Math.Max(1, (int)Math.Round(Math.Min(W, H) * .011));
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) {
            double sum = 0, square = 0, n = 0;
            for (int yy = Math.Max(0, y - radius); yy <= Math.Min(H - 1, y + radius); yy++)
                for (int xx = Math.Max(0, x - radius); xx <= Math.Min(W - 1, x + radius); xx++) {
                    double value = luminance[yy * W + xx]; sum += value; square += value * value; n++;
                }
            int i = y * W + x;
            Texture[i] = Clamp(Math.Sqrt(Math.Max(0, square / n - sum * sum / (n * n))) / 24);
            Blue[i] *= (1 - .38 * Texture[i]) * Sky[i];
        }
        int band = Math.Max(1, (int)Math.Round(H * .018));
        for (int y = 0; y < H; y++) {
            List<double> reference = new List<double>();
            for (int yy = Math.Max(0, y - band); yy <= Math.Min(H - 1, y + band); yy++)
                for (int x = 0; x < W; x++) if (Blue[yy * W + x] > .65) reference.Add(luminance[yy * W + x]);
            if (reference.Count < 20) continue;
            reference.Sort(); double refLum = reference[(int)(reference.Count * .80)];
            for (int x = 0; x < W; x++) {
                int i = y * W + x;
                double shade = Smooth((luminance[i] / Math.Max(1, refLum) - .57) / .36);
                Blue[i] *= .22 + .78 * shade;
            }
        }
        double[] safe = new double[W * H];
        int margin = Math.Max(1, (int)Math.Round(Math.Min(W, H) * .008));
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) {
            double sum = 0, n = 0;
            for (int yy = Math.Max(0, y - margin); yy <= Math.Min(H - 1, y + margin); yy++)
                for (int xx = Math.Max(0, x - margin); xx <= Math.Min(W - 1, x + margin); xx++) { sum += Sky[yy * W + xx]; n++; }
            safe[y * W + x] = Sky[y * W + x] * Math.Pow(sum / n, 3);
        }
        Sky = safe;
    }

    void ReadMap(string path, double sourceAspect) {
        using (Bitmap raw = new Bitmap(path)) {
            aspect = (double)raw.Height / raw.Width * sourceAspect;
            bool visible = false, transparent = false;
            // Scan original alpha; do not silently accept an opaque black/white map card.
            for (int y = 0; y < raw.Height; y++) for (int x = 0; x < raw.Width; x++) {
                int alpha = raw.GetPixel(x, y).A;
                if (alpha > 0) visible = true;
                if (alpha == 0) transparent = true;
            }
            if (!visible) throw new ArgumentException("Raw map contains no visible geometry; a completely transparent map cannot be laid out.");
            if (!transparent) throw new ArgumentException("Raw map must contain a transparent background, not an opaque map card.");
            double ratio = raw.Width / (double)raw.Height;
            int dw = ratio >= 1 ? 96 : Math.Max(1, (int)Math.Round(96 * ratio));
            int dh = ratio >= 1 ? Math.Max(1, (int)Math.Round(96 / ratio)) : 96;
            using (Bitmap density = new Bitmap(dw, dh)) {
                using (Graphics g = Graphics.FromImage(density)) {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(raw, 0, 0, dw, dh);
                }
                for (int y = 0; y < dh; y++) for (int x = 0; x < dw; x++) {
                    double u = (x + .5) / dw, v = (y + .5) / dh;
                    double weight = density.GetPixel(x, y).A / 255.0;
                    weight *= EdgeWeight(u, v, weight < .4 ? 1.4 : 1);
                    if (weight > .001) samples.Add(new double[] { u, v, weight });
                }
            }
            if (samples.Count == 0) throw new ArgumentException("Raw map geometry is too sparse at analysis resolution; inspect the map or use a reviewed manual placement.");
        }
    }

    // A continuous taper varies with side, path position and line class. It never
    // draws an enclosing shape, and does not use a single circular/rectangular cutout.
    static double EdgeWeight(double u, double v, double feature) {
        if (u <= 0 || v <= 0 || u >= 1 || v >= 1) return 0;
        double left = Smooth((u - .012 - .018 * Math.Sin(v * 17)) / .20);
        double right = Smooth((.988 - u + .014 * Math.Sin(v * 23)) / .18);
        double top = Smooth((v - .008 + .010 * Math.Sin(u * 19)) / .13);
        double bottom = Smooth((.992 - v + .020 * Math.Sin(u * 15)) / .22);
        double core = Math.Exp(-Math.Pow((u - .52) / .80, 4) - Math.Pow((v - .45) / .90, 4));
        return Math.Pow(left * right * top * bottom * core, feature);
    }

    public LayoutCandidate Evaluate(double x, double y, double width) {
        if (!Finite(x) || !Finite(y) || !Finite(width) || width <= 0)
            throw new ArgumentOutOfRangeException("placement", "Placement must contain finite coordinates and positive width.");
        double height = width * aspect, area = width * height;
        LayoutCandidate c = new LayoutCandidate { Left = x, Top = y, Width = width, Height = height,
            Area = area, PinX = x + options.PinU * width, PinY = y + options.PinV * height };
        double total = 0, blue = 0, safe = 0, texture = 0;
        foreach (double[] p in samples) {
            double xx = x + p[0] * width, yy = y + p[1] * height, a = p[2];
            total += a; blue += a * At(Blue, xx, yy); safe += a * At(Sky, xx, yy); texture += a * At(Texture, xx, yy);
        }
        c.BlueSupport = blue / total; c.SkySupport = safe / total; texture /= total;
        c.PinSkySupport = Box(Sky, c.PinX - .004, c.PinY - .004, .008, .008);
        // Text can go right, left, above or below the configured map-relative pin.
        double[][] positions = {
            new double[] { c.PinX + .010, c.PinY - options.LabelHeight * .35 },
            new double[] { c.PinX - .010 - options.LabelWidth, c.PinY - options.LabelHeight * .35 },
            new double[] { c.PinX - options.LabelWidth * .5, c.PinY - .010 - options.LabelHeight },
            new double[] { c.PinX - options.LabelWidth * .5, c.PinY + .010 }
        };
        double labelBest = Double.NegativeInfinity;
        foreach (double[] p in positions) {
            double b = Box(Blue, p[0], p[1], options.LabelWidth, options.LabelHeight);
            double s = Box(Sky, p[0], p[1], options.LabelWidth, options.LabelHeight);
            double value = .7 * b + .3 * s - 6 * (1 - s);
            if (value > labelBest) { labelBest = value; c.LabelX = p[0]; c.LabelY = p[1]; c.LabelSupport = b; c.LabelSkySupport = s; }
        }
        double titleBest = Double.NegativeInfinity;
        foreach (double u in new double[] { .08, .28, .48 }) foreach (double v in new double[] { .04, .10, .16 }) {
            double xx = x + u * width, yy = y + v * height;
            bool collision = xx < c.LabelX + options.LabelWidth + .008 && xx + options.TitleWidth > c.LabelX - .008
                && yy < c.LabelY + options.LabelHeight + .008 && yy + options.TitleHeight > c.LabelY - .008;
            double b = Box(Blue, xx, yy, options.TitleWidth, options.TitleHeight);
            double s = Box(Sky, xx, yy, options.TitleWidth, options.TitleHeight);
            double value = b - 6 * (1 - s) - (collision ? 2 : 0);
            if (value > titleBest) { titleBest = value; c.TitleX = xx; c.TitleY = yy; c.TitleSkySupport = s; c.TitleLabelOverlap = collision; }
        }
        double size = Clamp(area / options.PreferredArea);
        c.Score = .49 * c.BlueSupport + .17 * c.LabelSupport + .09 * titleBest + .20 * size + .05 * (1 - texture)
            - 6 * (1 - c.SkySupport) - 3 * (1 - c.LabelSkySupport) - 2 * (1 - c.TitleSkySupport) - 2 * (1 - c.PinSkySupport);
        if (x < Margin || y < Margin || x + width > 1 - Margin || y + height > options.MaxBottom)
            c.Score = Double.NegativeInfinity;
        return c;
    }

    bool Feasible(LayoutCandidate c) {
        return Finite(c.Score) && c.Area >= options.MinArea - 1e-9 && c.Area <= options.MaxArea + 1e-9
            && c.SkySupport >= .995 && c.LabelSkySupport >= .99 && c.TitleSkySupport >= .99
            && c.PinSkySupport >= .99 && c.BlueSupport >= .12 && !c.TitleLabelOverlap;
    }

    public void Search() {
        Best = null; Alternatives.Clear(); Examined = 0; LastError = null;
        double min = Math.Max(options.MinWidth, Math.Sqrt(options.MinArea / aspect));
        double max = Math.Min(options.MaxWidth, Math.Min(Math.Sqrt(options.MaxArea / aspect), Math.Min(1 - Margin * 2, (options.MaxBottom - Margin) / aspect)));
        if (!Finite(min) || !Finite(max) || max < min) {
            LastError = "No candidate can satisfy the map aspect ratio, minimum area, width limits and maximum bottom. Use a different geographic extent or reviewed manual placement.";
            return;
        }
        if (Sky.Sum() < W * H * .015 || Blue.Sum() < W * H * .005) {
            LastError = "No sufficiently usable sky was detected. Provide reviewed allowed/clear masks or choose a different negative-space mode; no automatic placement was selected.";
            return;
        }
        List<LayoutCandidate> feasible = new List<LayoutCandidate>();
        int steps = Math.Max(1, (int)Math.Ceiling((max - min) / .015));
        for (int wi = 0; wi <= steps; wi++) {
            double width = min + (max - min) * wi / steps, height = width * aspect;
            for (double y = Margin; y + height <= options.MaxBottom + 1e-9; y += .025)
                for (double x = Margin; x + width <= 1 - Margin + 1e-9; x += .025) {
                    LayoutCandidate c = Evaluate(x, y, width); Examined++;
                    if (Feasible(c)) feasible.Add(c);
                }
        }
        if (feasible.Count == 0) {
            LastError = "No safe candidate met the area, point, label and title constraints. The algorithm did not fall back to an unsafe or undersized map. Review the masks, text sizes, geographic extent or use manual placement.";
            return;
        }
        feasible.Sort(delegate(LayoutCandidate a, LayoutCandidate b) { return b.Score.CompareTo(a.Score); });
        LayoutCandidate peak = feasible[0];
        Best = feasible.Where(c => c.Score >= peak.Score - .015 && c.BlueSupport >= peak.BlueSupport - .08)
            .OrderByDescending(c => c.Area).ThenByDescending(c => c.Score).First();
        Alternatives.Add(Best);
        foreach (LayoutCandidate c in feasible) {
            if (Alternatives.All(a => Math.Abs(a.Left + a.Width / 2 - c.Left - c.Width / 2) > .17 || Math.Abs(a.Top - c.Top) > .12)) {
                Alternatives.Add(c); if (Alternatives.Count >= 3) break;
            }
        }
    }

    public void SaveHeatmap(string source, string output) {
        using (Bitmap original = new Bitmap(source))
        using (Bitmap preview = new Bitmap(1200, Math.Max(1, (int)Math.Round(1200.0 * original.Height / original.Width)))) {
            using (Graphics g = Graphics.FromImage(preview)) {
                g.DrawImage(original, 0, 0, preview.Width, preview.Height);
                using (Bitmap field = new Bitmap(W, H)) {
                    for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) {
                        int i = y * W + x;
                        field.SetPixel(x, y, Sky[i] > .5 ? Color.FromArgb(110, (int)(220 * (1 - Blue[i])), (int)(120 + 100 * Blue[i]), (int)(50 + 190 * Blue[i])) : Color.FromArgb(105, 20, 20, 20));
                    }
                    g.DrawImage(field, 0, 0, preview.Width, preview.Height);
                }
                using (Font font = new Font("Arial", 13)) {
                    int rank = 0;
                    foreach (LayoutCandidate c in Alternatives) {
                        using (Pen pen = new Pen(rank == 0 ? Color.Lime : Color.Orange, rank == 0 ? 3 : 1.5f)) {
                            g.DrawRectangle(pen, (float)(c.Left * preview.Width), (float)(c.Top * preview.Height), (float)(c.Width * preview.Width), (float)(c.Height * preview.Height));
                            g.DrawString((++rank).ToString(), font, Brushes.White, (float)(c.Left * preview.Width + 5), (float)(c.Top * preview.Height + 5));
                        }
                    }
                    if (Best == null) {
                        g.FillRectangle(Brushes.Black, 12, 12, preview.Width - 24, 90);
                        g.DrawString("NO SAFE AUTOMATIC PLACEMENT\n" + (LastError ?? "Run Search() first."), font, Brushes.White, new RectangleF(20, 20, preview.Width - 40, 80));
                    }
                }
                using (Font font = new Font("Arial", 11))
                    g.DrawString("Estimated confidence: cyan clearer / amber uncertain / dark excluded. Green: selected. Visual review required.", font, Brushes.White, 15, preview.Height - 28);
            }
            preview.Save(output, ImageFormat.Png);
        }
    }

    double Protection(double x, double y) {
        double safe = Interpolate(Sky, x, y);
        return manualAllowed ? Smooth((safe - .02) / .98) : Smooth((safe - .95) / .0499);
    }

    public void BlendLayer(Bitmap layer, LayoutCandidate c, double pinX, double pinY) {
        if (layer == null || c == null) throw new ArgumentNullException("layer/candidate");
        if (!Finite(c.Width) || !Finite(c.Height) || c.Width <= 0 || c.Height <= 0)
            throw new ArgumentException("Candidate has invalid dimensions.");
        Rectangle rect = new Rectangle(0, 0, layer.Width, layer.Height);
        BitmapData d = layer.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try {
            byte[] data = new byte[d.Stride * layer.Height]; Marshal.Copy(d.Scan0, data, 0, data.Length);
            for (int y = 0; y < layer.Height; y++) for (int x = 0; x < layer.Width; x++) {
                int i = y * d.Stride + x * 4; if (data[i + 3] == 0) continue;
                double xn = (x + .5) / layer.Width, yn = (y + .5) / layer.Height;
                double u = (xn - c.Left) / c.Width, v = (yn - c.Top) / c.Height;
                double feature = data[i + 3] < 100 ? 1.45 : data[i + 3] > 220 ? .85 : 1.05;
                double clear = Interpolate(Blue, xn, yn);
                double opacity = EdgeWeight(u, v, feature) * (.23 + .77 * Math.Pow(clear, .8)) * Protection(xn, yn);
                double titleClear = Math.Exp(-Math.Pow((xn - c.TitleX - options.TitleWidth / 2) / (options.TitleWidth / 2 + .018), 4)
                    - Math.Pow((yn - c.TitleY - options.TitleHeight / 2) / (options.TitleHeight / 2 + .012), 4));
                double labelX = Finite(c.LabelX) ? c.LabelX : pinX + .01;
                double labelY = Finite(c.LabelY) ? c.LabelY : pinY - options.LabelHeight * .35;
                double labelClear = Math.Exp(-Math.Pow((xn - labelX - options.LabelWidth / 2) / (options.LabelWidth / 2 + .012), 4)
                    - Math.Pow((yn - labelY - options.LabelHeight / 2) / (options.LabelHeight / 2 + .01), 4));
                opacity *= 1 - .80 * Math.Max(titleClear, labelClear);
                // Grain frequency is normalized to the photograph rather than native pixel size.
                opacity *= .97 + .03 * Math.Sin(xn * 1800 * 1.93 + yn * 1350 * 2.77);
                data[i + 3] = (byte)(data[i + 3] * Clamp(opacity));
            }
            Marshal.Copy(data, 0, d.Scan0, data.Length);
        } finally { layer.UnlockBits(d); }
    }

    byte[] ReadExactAllowedMask(Bitmap layer) {
        if (!manualAllowed) return null;
        using (Bitmap original = new Bitmap(options.AllowedMaskPath)) {
            if (original.Width != layer.Width || original.Height != layer.Height)
                throw new ArgumentException("Allowed mask must match the upright photograph pixel dimensions for final protection.");
            using (Bitmap mask = new Bitmap(layer.Width, layer.Height, PixelFormat.Format32bppArgb)) {
                using (Graphics g = Graphics.FromImage(mask)) {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(original, new Rectangle(0, 0, mask.Width, mask.Height), 0, 0, original.Width, original.Height, GraphicsUnit.Pixel);
                }
                BitmapData md = mask.LockBits(new Rectangle(0, 0, mask.Width, mask.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try {
                    byte[] values = new byte[mask.Width * mask.Height * 4];
                    for (int y = 0; y < mask.Height; y++)
                        Marshal.Copy(IntPtr.Add(md.Scan0, y * md.Stride), values, y * mask.Width * 4, mask.Width * 4);
                    return values;
                } finally { mask.UnlockBits(md); }
            }
        }
    }

    // Call AFTER adding every text label, title, point and north marker. This
    // protects all elements, not just geometry. Explicit mask black is absolute.
    public void ProtectLayer(Bitmap layer) {
        if (layer == null) throw new ArgumentNullException("layer");
        byte[] exactMask = ReadExactAllowedMask(layer);
        Rectangle rect = new Rectangle(0, 0, layer.Width, layer.Height);
        BitmapData d = layer.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try {
            byte[] data = new byte[d.Stride * layer.Height]; Marshal.Copy(d.Scan0, data, 0, data.Length);
            for (int y = 0; y < layer.Height; y++) for (int x = 0; x < layer.Width; x++) {
                int i = y * d.Stride + x * 4;
                int m = (y * layer.Width + x) * 4;
                // The analysis field is resized. Enforce the original mask again
                // so interpolation cannot leak ink onto protected edge pixels.
                if (exactMask != null && (exactMask[m] + exactMask[m + 1] + exactMask[m + 2]) * exactMask[m + 3] < 3 * 255 * 16) {
                    data[i + 3] = 0;
                    continue;
                }
                if (data[i + 3] > 0) data[i + 3] = (byte)(data[i + 3] * Protection((x + .5) / layer.Width, (y + .5) / layer.Height));
            }
            Marshal.Copy(data, 0, d.Scan0, data.Length);
        } finally { layer.UnlockBits(d); }
    }
}
