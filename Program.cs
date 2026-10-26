using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ConvertFolderToJPG
{
    class Program
    {

        static string EncoderArgs = "-quality 70 -sample 1x1";
        //static string EncoderArgs = "-quality 70";
        static string[] Extensions = { ".png", ".bmp", ".dib", ".gif", ".tif", ".tiff", ".ico", ".webp", ".heic", ".heif", ".avif", ".jxr", ".wdp", ".hdp", ".dds",
            ".dng", ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".sr2", ".srf", ".orf", ".rw2", ".raf", ".pef", ".srw", ".raw", ".3fr", ".erf", ".kdc", ".mrw", ".x3f", ".iiq" };

        static object locker = new object();
        static long sizeBefore = 0, sizeAfter = 0;
        static int converted = 0;
        static List<string> failed = new List<string>();

        [STAThread]
        static void Main(string[] args)
        {
            List<string> files = new List<string>();
            string[] sources = args.Length > 0 ? args : new string[]{AppDomain.CurrentDomain.BaseDirectory};
            foreach(string s in sources)
            {
                if (Directory.Exists(s)) files.AddRange(Directory.GetFiles(s));
                else if (File.Exists(s)) files.Add(Path.GetFullPath(s));
            }
            files = files.Where(f => Extensions.Where(x => x==Path.GetExtension(f).ToLower()).Any()).Distinct().ToList();
            if (files.Count == 0)
            {
                Show("No images found.");
                return;
            }

            Dictionary<string, string> outputs = new Dictionary<string, string>();
            HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> ordered = files.Where(f => Path.GetExtension(f).ToLower()==".png").ToList();
            ordered.AddRange(files.Where(f => Path.GetExtension(f).ToLower()!=".png"));
            foreach(string f in ordered)
            {
                string dir = Path.Combine(Path.GetDirectoryName(f), "JPG");
                string name = Path.GetFileNameWithoutExtension(f);
                string jpg = Path.Combine(dir, name + ".jpg");
                if (!taken.Add(jpg))
                {
                    jpg = Path.Combine(dir, name + "_" + Path.GetExtension(f).TrimStart('.').ToLower() + ".jpg");
                    taken.Add(jpg);
                }
                outputs[f] = jpg;
            }

            string cjpeg = ExtractEncoder();

            Parallel.ForEach(files, f =>
            {
                string ppm = Path.Combine(Path.GetTempPath(), "ConvertFolderToJPG_" + Guid.NewGuid().ToString("N") + ".ppm");
                string jpg = outputs[f];
                string part = jpg + ".part";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(jpg));
                    WritePPM(f, ppm);

                    ProcessStartInfo proc = new ProcessStartInfo();
                    proc.FileName = cjpeg;
                    proc.Arguments = EncoderArgs + " -outfile \"" + part + "\" \"" + ppm + "\"";
                    proc.UseShellExecute = false;
                    proc.CreateNoWindow = true;
                    Process p = Process.Start(proc);
                    p.WaitForExit();
                    if (p.ExitCode != 0 || !File.Exists(part)) throw new Exception("encoder failed");

                    RemoveJFIF(part);
                    if (File.Exists(jpg)) File.Delete(jpg);
                    File.Move(part, jpg);
                    lock(locker)
                    {
                        sizeBefore += new FileInfo(f).Length;
                        sizeAfter += new FileInfo(jpg).Length;
                        converted++;
                    }
                }
                catch (Exception e)
                {
                    string reason = e is NotSupportedException || e is System.Runtime.InteropServices.COMException || e is FileFormatException ? "no decoder" : e.Message;
                    lock(locker) failed.Add(Path.GetFileName(f) + " (" + reason + ")");
                }
                if (File.Exists(ppm)) File.Delete(ppm);
                if (File.Exists(part)) File.Delete(part);
            });

            failed.Sort();
            string msg = converted + " converted. " + MB(sizeBefore) + " -> " + MB(sizeAfter);
            if (failed.Count > 0) msg += "\n\nFailed:\n" + string.Join("\n", failed.Take(15)) + (failed.Count > 15 ? "\n..." : "");
            Show(msg);
        }

        static void Show(string msg)
        {
            MessageBox.Show(msg, "ConvertFolderToJPG");
        }

        // cjpeg.exe from mozjpeg 4.1.5, embedded as resource
        static string ExtractEncoder()
        {
            Stream res = typeof(Program).Assembly.GetManifestResourceStream("cjpeg.exe");
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConvertFolderToJPG");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "cjpeg_" + res.Length + ".exe");
            if (!File.Exists(path) || new FileInfo(path).Length != res.Length)
            {
                string tmp = path + "." + Guid.NewGuid().ToString("N");
                using (FileStream f = File.Create(tmp)) res.CopyTo(f);
                try { File.Move(tmp, path); } catch { File.Delete(tmp); }
            }
            res.Close();
            return path;
        }

        static BitmapSource Load(string file)
        {
            BitmapDecoder dec = BitmapDecoder.Create(new MemoryStream(File.ReadAllBytes(file)), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource img = dec.Frames[0];
            if (Path.GetExtension(file).ToLower()==".ico") img = dec.Frames.Where(fr => fr.PixelWidth == dec.Frames.Max(x => x.PixelWidth)).First();

            int orientation = 1;
            try { orientation = Convert.ToInt32(((BitmapMetadata)img.Metadata).GetQuery("System.Photo.Orientation")); } catch { }
            TransformGroup t = new TransformGroup();
            if (orientation == 2 || orientation == 5 || orientation == 7) t.Children.Add(new ScaleTransform(-1, 1));
            if (orientation == 4) t.Children.Add(new ScaleTransform(1, -1));
            if (orientation == 3) t.Children.Add(new RotateTransform(180));
            if (orientation == 6 || orientation == 7) t.Children.Add(new RotateTransform(90));
            if (orientation == 5 || orientation == 8) t.Children.Add(new RotateTransform(270));
            if (t.Children.Count > 0) img = new TransformedBitmap(img, t);
            return img;
        }

        static void WritePPM(string file, string ppm)
        {
            FormatConvertedBitmap img = new FormatConvertedBitmap(Load(file), PixelFormats.Bgra32, null, 0);
            int w = img.PixelWidth, h = img.PixelHeight;
            byte[] bgra = new byte[w * h * 4];
            img.CopyPixels(bgra, w * 4, 0);
            byte[] rgb = new byte[w * h * 3];
            for(int i = 0; i < w * h; i++)
            {
                int a = bgra[i*4 + 3];
                rgb[i*3] = (byte)((bgra[i*4 + 2] * a + 255 * (255 - a) + 127) / 255);
                rgb[i*3 + 1] = (byte)((bgra[i*4 + 1] * a + 255 * (255 - a) + 127) / 255);
                rgb[i*3 + 2] = (byte)((bgra[i*4] * a + 255 * (255 - a) + 127) / 255);
            }
            using (FileStream f = new FileStream(ppm, FileMode.Create))
            {
                byte[] header = System.Text.Encoding.ASCII.GetBytes("P6\n" + w + " " + h + "\n255\n");
                f.Write(header, 0, header.Length);
                f.Write(rgb, 0, rgb.Length);
            }
        }

        static void RemoveJFIF(string jpg)
        {
            byte[] b = File.ReadAllBytes(jpg);
            if (b.Length < 20 || b[2] != 0xFF || b[3] != 0xE0 || b[6] != 'J' || b[7] != 'F' || b[8] != 'I' || b[9] != 'F') return;
            int len = b[4]*256 + b[5];
            byte[] o = new byte[b.Length - 2 - len];
            o[0] = 0xFF; o[1] = 0xD8;
            Array.Copy(b, 4 + len, o, 2, b.Length - 4 - len);
            File.WriteAllBytes(jpg, o);
        }

        static string MB(long bytes)
        {
            if (bytes < 1048576) return (bytes / 1024) + " KB";
            return (bytes / 1048576.0).ToString("0.0") + " MB";
        }
    }
}
