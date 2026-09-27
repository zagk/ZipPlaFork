// ThumbnailBench.cs
// d2: 썸네일 속도 개선을 "느낌" 이 아니라 숫자로 확인하기 위한 계측용 스위치.
//
// 사용법:
//   ZipPla.exe -benchthumb "폴더경로" [-selftest 결과파일]
// 폴더 안의 이미지에 대해 예전 경로(원본 전체 디코딩)와 새 경로(썸네일용 축소 디코딩)의
// 시간을 재고, 동시 실행 제한기를 썼을 때 폴더 전체 시간이 어떻게 달라지는지 보여준다.
// 제한기는 프로세스 전역이라 -selftest 와 같은 프로세스에서 실행된다.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ZipPla
{
    internal static class ThumbnailBench
    {
        public static bool IsRequested(string[] commandLine)
        {
            if (commandLine == null) return false;
            return commandLine.Any(a => string.Equals(a, "-benchthumb", StringComparison.OrdinalIgnoreCase));
        }

        public static int Run(string[] commandLine)
        {
            var folder = GetFolder(commandLine);
            var report = new StringBuilder();

            report.AppendLine("ZipPla thumbnail benchmark");
            report.AppendLine("folder  : " + (folder ?? "<null>"));
            report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "cores   : {0}   limiter parallelism: {1}   capacity: {2:F1} MB",
                Environment.ProcessorCount, ThumbnailLimiter.MaxParallelism, ThumbnailLimiter.TotalCapacity / 1048576.0));
            report.AppendLine();

            var target = new Size(160, 120);

            if (folder == null || !Directory.Exists(folder))
            {
                report.AppendLine("folder not found");
                Write(report);
                return 1;
            }

            var files = Directory.GetFiles(folder)
                .Where(IsImage)
                .OrderByDescending(f => new FileInfo(f).Length)
                .Take(200)
                .ToArray();

            if (files.Length == 0)
            {
                report.AppendLine("no image found");
                Write(report);
                return 1;
            }

            report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0,-40} {1,7} {2,12} {3,9} {4,9} {5,9} {6,9}",
                "file", "MB", "pixels", "full", "scaled", "old+scl", "new+scl"));

            foreach (var file in files)
            {
                var pixels = "?";
                try
                {
                    var info = new ImageInfo(file);
                    pixels = info.Size.Width + "x" + info.Size.Height;
                }
                catch { }

                report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,-40} {1,7:F1} {2,12} {3,9} {4,9} {5,9} {6,9}",
                    Cut(Path.GetFileName(file), 40),
                    new FileInfo(file).Length / 1048576.0,
                    pixels,
                    Ms(() => FullDecode(file)),
                    Ms(() => ScaledDecode(file, target)),
                    Ms(() => FullDecodeAndScale(file, target)),
                    Ms(() => ScaledDecodeAndScale(file, target, limit: false))));
            }

            // 기본 잘라내기 모드(ClipMode.PlaClip)는 소스에서 초점점을 찾아 그 주변을 잘라낸다.
            // 소스가 작아지면 초점점이 달라질 수 있으므로, 실제로 보이는 "잘라내기 구간" 이
            // 어떻게 변하는지 확인한다. 캐시(ADS) 를 쓰면 예전에도 약 226px 로 줄어든 비트맵에서
            // 초점을 계산했으므로, 캐시 경로와 비교하면 기존 동작과 일관된지 알 수 있다.
            report.AppendLine();
            report.AppendLine("== visible crop band in PlaClip mode (how much of the image is shown) ==");
            report.AppendLine("note: the app only uses the scaled source when ClipMode is Letterbox or PanAndScan,");
            report.AppendLine("      because in PlaClip the focus point (and so the crop) is sensitive to the source size.");
            report.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,-40} {1,26} {2,26} {3,26}",
                "file", "full source (old, 1st visit)", "cached ~226px (old, 2nd visit)", "scaled source (new)"));
            foreach (var file in Enumerable.Take(files, 3))
            {
                try
                {
                    using (var full = ImageLoader.GetFullBitmap(file))
                    {
                        var scaled = ImageLoader.GetScaledThumbnailSource(file, target, out _);
                        if (scaled == null)
                        {
                            report.AppendLine(Cut(Path.GetFileName(file), 40) + " : scaled decode not applicable");
                            continue;
                        }
                        // 캐시를 쓰는 설정에서 두 번째 방문 때와 같은 상태를 만든다(예전 경로로 캐시 생성)
                        var fromCache = BuildOldCacheAndLoad(file, target);

                        var bandFull = CropBand(full, target);
                        var bandCached = fromCache == null ? "no cache" : CropBand(fromCache, target);
                        var bandScaled = CropBand(scaled, target);
                        if (fromCache != null) fromCache.Dispose();
                        scaled.Dispose();

                        report.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,-40} {1,26} {2,26} {3,26}",
                            Cut(Path.GetFileName(file), 40), bandFull, bandCached, bandScaled));
                    }
                }
                catch (Exception ex)
                {
                    report.AppendLine(Cut(Path.GetFileName(file), 40) + " : " + ex.Message);
                }
            }

            // 캐시(ADS)에 저장되는 내용이 축소 디코딩 후에도 같은지 확인한다.
            // GetImageThumbnail 은 원본 크기 대신 축소된 비트맵을 TrySet 에 넘기게 되므로,
            // 여기서 저장 크기/히트 여부가 달라지면 폴더를 다시 열 때 느려진다.
            report.AppendLine();
            report.AppendLine("== ADS cache comparison (on a temp copy) ==");
            try
            {
                var source = files[0];
                var copy = Path.Combine(Path.GetTempPath(), "ZipPlaCacheCheck" + Path.GetExtension(source));
                File.Copy(source, copy, true);

                report.AppendLine("old source (full decode)   : " + DescribeCache(copy, target, useScaled: false));
                report.AppendLine("new source (scaled decode) : " + DescribeCache(copy, target, useScaled: true));

                TryDelete(copy);
            }
            catch (Exception ex)
            {
                report.AppendLine("cache comparison failed: " + ex.Message);
            }

            report.AppendLine();
            report.AppendLine("== whole folder (wall clock, ms) ==");
            report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "old pipeline (full decode + scale)"
                + "  1thread: {0,6:F0}   {1}threads: {2,6:F0}",
                Whole(files, target, useScaled: false, applyLimiter: false, parallel: false),
                ThumbnailLimiter.MaxParallelism,
                Whole(files, target, useScaled: false, applyLimiter: false, parallel: true)));
            report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "new pipeline (scaled decode + scale)"
                + "  1thread: {0,6:F0}   {1}threads(no limit): {2,6:F0}   {1}threads(limiter): {3,6:F0}",
                Whole(files, target, useScaled: true, applyLimiter: false, parallel: false),
                ThumbnailLimiter.MaxParallelism,
                Whole(files, target, useScaled: true, applyLimiter: false, parallel: true),
                Whole(files, target, useScaled: true, applyLimiter: true, parallel: true)));

            Write(report);
            return 0;
        }

        private static bool IsImage(string path)
        {
            var ext = Path.GetExtension(path);
            if (ext == null) return false;
            var lower = ext.ToLower();
            return lower == ".jpg" || lower == ".jpeg" || lower == ".png" || lower == ".bmp";
        }

        private static string Cut(string s, int length)
        {
            return s.Length <= length ? s : s.Substring(0, length - 1) + "~";
        }

        private static string GetFolder(string[] commandLine)
        {
            if (commandLine != null)
            {
                for (var i = 0; i < commandLine.Length - 1; i++)
                {
                    if (string.Equals(commandLine[i], "-benchthumb", StringComparison.OrdinalIgnoreCase) &&
                        !commandLine[i + 1].StartsWith("-"))
                    {
                        return commandLine[i + 1];
                    }
                }
            }
            return null;
        }

        private static string Ms(Func<object> action)
        {
            try
            {
                action();
                var sw = Stopwatch.StartNew();
                var result = action();
                sw.Stop();
                (result as IDisposable)?.Dispose();
                return sw.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                return "err:" + ex.GetType().Name;
            }
        }

        private static object FullDecode(string path)
        {
            return ImageLoader.GetFullBitmap(path);
        }

        private static object ScaledDecode(string path, Size target)
        {
            ImageInfo imageInfo;
            var bitmap = ImageLoader.GetScaledThumbnailSource(path, target, out imageInfo);
            if (bitmap == null) throw new NotSupportedException();
            return bitmap;
        }

        private static object FullDecodeAndScale(string path, Size target)
        {
            using (var full = ImageLoader.GetFullBitmap(path)) return Scale(full, target);
        }

        private static object ScaledDecodeAndScale(string path, Size target, bool limit)
        {
            System.IDisposable lease = null;
            try
            {
                if (limit) lease = ThumbnailLimiter.AcquireAsync(path).GetAwaiter().GetResult();
                using (var scaled = (Bitmap)ScaledDecode(path, target)) return Scale(scaled, target);
            }
            finally
            {
                if (lease != null) lease.Dispose();
            }
        }

        private static Bitmap Scale(Bitmap source, Size target)
        {
            var result = new Bitmap(target.Width, target.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(result))
            {
                g.DrawImage(source, new Rectangle(0, 0, target.Width, target.Height),
                    new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
            }
            return result;
        }

        private static double Whole(string[] files, Size target, bool useScaled, bool applyLimiter, bool parallel)
        {
            var sw = Stopwatch.StartNew();
            if (parallel)
            {
                // 제한기를 끄는 경우에는 제한기 자체가 병렬 수를 제한하지 않도록 코어 수만큼만 돌린다
                Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = ThumbnailLimiter.MaxParallelism },
                    file => RunOne(file, target, useScaled, applyLimiter));
            }
            else
            {
                foreach (var file in files) RunOne(file, target, useScaled, applyLimiter);
            }
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }

        private static void RunOne(string file, Size target, bool useScaled, bool applyLimiter)
        {
            System.IDisposable lease = null;
            try
            {
                if (applyLimiter) lease = ThumbnailLimiter.AcquireAsync(file).GetAwaiter().GetResult();
                if (useScaled)
                {
                    ImageInfo imageInfo;
                    using (var scaled = ImageLoader.GetScaledThumbnailSource(file, target, out imageInfo))
                    {
                        if (scaled != null) Scale(scaled, target).Dispose();
                    }
                }
                else
                {
                    using (var full = ImageLoader.GetFullBitmap(file)) Scale(full, target).Dispose();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
            finally
            {
                if (lease != null) lease.Dispose();
            }
        }

        /// <summary>예전(전체 디코딩) 경로로 ADS 캐시를 만들고, 두 번째 방문처럼 읽어온다.</summary>
        private static Bitmap BuildOldCacheAndLoad(string path, Size target)
        {
            var copy = Path.Combine(Path.GetTempPath(), "ZipPlaCacheCheckOld" + Path.GetExtension(path));
            try
            {
                File.Copy(path, copy, true);
                GPSizeThumbnail.TryDeleteAlternateDataStream(copy);
                using (var full = ImageLoader.GetFullBitmap(copy))
                {
                    var info = ImageInfo.Supports(copy) ? new ImageInfo(copy) : ImageLoader.GetImageInfo(full);
                    Bitmap biggest;
                    GPSizeThumbnail.TrySet(GPSizeThumbnail.AlternateDataStream, copy, -1, target, false,
                        !ImageLoader.IsLowLoad(copy), full, info.ToData(), out biggest);
                    if (biggest != null) biggest.Dispose();
                }

                Bitmap cached;
                byte[] data;
                if (!GPSizeThumbnail.TryGet(GPSizeThumbnail.AlternateDataStream, copy, -1, target, false, out cached, out data)) return null;
                return cached;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return null;
            }
            finally
            {
                TryDelete(copy);
            }
        }

        /// <summary>DoJustClipping のデフォルト分岐(PlaClip) と同じ計算で、縦方向にどの割合が見えるかを出す。</summary>
        private static string CropBand(Bitmap source, Size target)
        {
            Fraction one = new Fraction(1, 1);
            var focus = BitmapAnalyzer.GetFocus(source, one);
            var zoom = Math.Max(target.Width / (double)source.Width, target.Height / (double)source.Height);
            var height = target.Height / zoom;
            var top = focus.Y - height / 2;
            if (top < 0) top = 0;
            else if (top + height > source.Height) top = source.Height - height;
            var from = top / source.Height * 100;
            var to = (top + height) / source.Height * 100;
            return string.Format(CultureInfo.InvariantCulture, "y {0,5:F1}%~{1,5:F1}% (src {2}x{3})", from, to, source.Width, source.Height);
        }

        private static string DescribeCache(string path, Size target, bool useScaled)
        {
            var cacheRoot = GPSizeThumbnail.AlternateDataStream;
            GPSizeThumbnail.TryDeleteAlternateDataStream(path);

            ImageInfo imageInfo;
            Bitmap source;
            if (useScaled)
            {
                source = ImageLoader.GetScaledThumbnailSource(path, target, out imageInfo);
                if (source == null) return "scaled decode not applicable";
            }
            else
            {
                source = ImageLoader.GetFullBitmap(path);
                imageInfo = ImageInfo.Supports(path) ? new ImageInfo(path) : ImageLoader.GetImageInfo(source);
            }

            var setOk = false;
            Bitmap fromCache;
            long adsLength = -1;
            using (source)
            {
                Bitmap biggest;
                setOk = GPSizeThumbnail.TrySet(cacheRoot, path, -1, target, false, !ImageLoader.IsLowLoad(path),
                    source, imageInfo.ToData(), out biggest);
                if (biggest != null) biggest.Dispose();
            }

            try
            {
                using (var stream = new NTFSMultiStream(path, GPSizeThumbnail.STREAM_NAME, FileAccess.Read))
                    adsLength = stream.Length;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }

            byte[] data;
            var hit = GPSizeThumbnail.TryGet(cacheRoot, path, -1, target, false, out fromCache, out data);
            var cachedSize = fromCache == null ? "-" : fromCache.Width + "x" + fromCache.Height;
            if (fromCache != null) fromCache.Dispose();

            return string.Format(CultureInfo.InvariantCulture,
                "source {0,5}x{1,-5} TrySet={2} ads={3,7} bytes  TryGet={4} cached={5}",
                useScaled ? "scaled" : "full", "", setOk ? "ok" : "no", adsLength, hit ? "hit" : "miss", cachedSize);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }

        private static void Write(StringBuilder report)
        {
            var text = report.ToString();
            try
            {
                if (TryAttachConsole())
                {
                    Console.Out.Write(text);
                    Console.Out.Flush();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "ZipPlaThumbnailBench.log"), text, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        private const int AttachParentProcess = -1;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        private static bool TryAttachConsole()
        {
            return AttachConsole(AttachParentProcess);
        }
    }
}
