// SelfTest.cs
// d1: 이 프로젝트에는 자동화된 테스트가 전혀 없었다. 새로 추가한 파서/정규화/직렬화 코드는
//     순수 함수라서 외부 의존성 없이 그대로 검증할 수 있으므로, 최소한의 자체 검사기를 둔다.
//
// 사용법:
//   ZipPla.exe -selftest [결과파일경로]
// 종료 코드 0 = 전부 통과, 1 = 실패 있음. 결과는 파일로도 남는다(WinExe 라서 표준 출력이 없다).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZipPla
{
    internal static class SelfTest
    {
        private const int AttachParentProcess = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        private static readonly StringBuilder Report = new StringBuilder();
        private static int passed;
        private static int failed;

        public static bool IsRequested(string[] commandLine)
        {
            if (commandLine == null) return false;
            return commandLine.Any(a => string.Equals(a, "-selftest", StringComparison.OrdinalIgnoreCase));
        }

        public static int Run(string[] commandLine = null)
        {
            var outputPath = GetOutputPath(commandLine);

            try
            {
                TestA1111Parameters();
                TestNovelAiJson();
                TestComfyUiJson();
                TestPngTextChunks();
                TestPngDecompressionBombIsBounded();
                TestSingleInstancePayload();
                TestArchiveExtensionList();
                TestMessages();
                TestThumbnailLimiter();
                TestScaledThumbnailSource();
            }
            catch (Exception ex)
            {
                failed++;
                Report.AppendLine("EXCEPTION  " + ex);
            }

            Report.AppendLine();
            Report.AppendLine(string.Format(CultureInfo.InvariantCulture, "ZipPla self test: {0} passed, {1} failed", passed, failed));
            var text = Report.ToString();

            try
            {
                if (AttachConsole(AttachParentProcess))
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
                if (!string.IsNullOrEmpty(outputPath)) File.WriteAllText(outputPath, text, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }

            return failed == 0 ? 0 : 1;
        }

        private static string GetOutputPath(string[] commandLine)
        {
            try
            {
                if (commandLine != null)
                {
                    for (var i = 0; i < commandLine.Length - 1; i++)
                    {
                        if (string.Equals(commandLine[i], "-selftest", StringComparison.OrdinalIgnoreCase) &&
                            !commandLine[i + 1].StartsWith("-"))
                        {
                            return commandLine[i + 1];
                        }
                    }
                }
                return Path.Combine(Path.GetTempPath(), "ZipPlaSelfTest.log");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return null;
            }
        }

        #region assertions

        private static void Check(string name, bool condition, string detail = null)
        {
            if (condition)
            {
                passed++;
                Report.AppendLine("PASS  " + name);
            }
            else
            {
                failed++;
                Report.AppendLine("FAIL  " + name + (detail == null ? "" : "   [" + detail + "]"));
            }
        }

        private static void CheckEqual(string name, string expected, string actual)
        {
            Check(name, string.Equals(expected, actual, StringComparison.Ordinal),
                "expected <" + (expected ?? "<null>") + "> actual <" + (actual ?? "<null>") + ">");
        }

        private static string Find(List<MetadataRow> rows, string name)
        {
            if (rows == null) return null;
            foreach (var row in rows)
            {
                if (string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase)) return row.Value;
            }
            return null;
        }

        #endregion

        #region tests

        private static void TestA1111Parameters()
        {
            var text = "masterpiece, best quality\n" +
                       "Negative prompt: lowres, bad anatomy\n" +
                       "Steps: 28, Sampler: DPM++ 2M Karras, CFG scale: 7.5, Seed: 1234567890, Size: 512x768, Model hash: abc123";

            var rows = A1111ParameterParser.Parse(text);
            CheckEqual("a1111 prompt", "masterpiece, best quality", Find(rows, "Prompt"));
            CheckEqual("a1111 negative prompt", "lowres, bad anatomy", Find(rows, "Negative prompt"));
            CheckEqual("a1111 steps", "28", Find(rows, "Steps"));
            CheckEqual("a1111 sampler", "DPM++ 2M Karras", Find(rows, "Sampler"));
            CheckEqual("a1111 cfg scale", "7.5", Find(rows, "CFG scale"));
            CheckEqual("a1111 seed", "1234567890", Find(rows, "Seed"));
            CheckEqual("a1111 size", "512x768", Find(rows, "Size"));

            // 괄호 안의 쉼표는 분리하면 안 된다.
            var brackets = A1111ParameterParser.Parse("Steps: 20, Schedule type: [a, b], Seed: 5");
            CheckEqual("a1111 bracket comma", "[a, b]", Find(brackets, "Schedule type"));

            // 프롬프트만 있는 경우에도 읽을 수 있어야 한다.
            var promptOnly = A1111ParameterParser.Parse("just a prompt");
            CheckEqual("a1111 prompt only", "just a prompt", Find(promptOnly, "Prompt"));
        }

        private static void TestNovelAiJson()
        {
            var json = "{\"prompt\":\"1girl, solo\",\"uc\":\"bad hands\",\"steps\":28,\"sampler\":\"k_euler\"," +
                       "\"cfg_rescale\":0,\"seed\":42,\"width\":832,\"height\":1216}";

            var rows = AiMetadataParser.Parse(json);
            CheckEqual("novelai prompt", "1girl, solo", Find(rows, "prompt"));
            CheckEqual("novelai uc", "bad hands", Find(rows, "uc"));
            CheckEqual("novelai steps", "28", Find(rows, "steps"));
            CheckEqual("novelai seed", "42", Find(rows, "seed"));
            CheckEqual("novelai width", "832", Find(rows, "width"));
        }

        private static void TestComfyUiJson()
        {
            var json = "{" +
                "\"3\":{\"class_type\":\"KSampler\",\"inputs\":{\"seed\":123,\"steps\":20,\"cfg\":7," +
                "\"sampler_name\":\"euler\",\"scheduler\":\"normal\",\"denoise\":1," +
                "\"model\":[\"4\",0],\"positive\":[\"6\",0],\"negative\":[\"7\",0],\"latent_image\":[\"5\",0]}}," +
                "\"4\":{\"class_type\":\"CheckpointLoaderSimple\",\"inputs\":{\"ckpt_name\":\"sd_xl.safetensors\"}}," +
                "\"5\":{\"class_type\":\"EmptyLatentImage\",\"inputs\":{\"width\":1024,\"height\":1024}}," +
                "\"6\":{\"class_type\":\"CLIPTextEncode\",\"inputs\":{\"text\":\"a cat\"}}," +
                "\"7\":{\"class_type\":\"CLIPTextEncode\",\"inputs\":{\"text\":\"blurry\"}}}";

            var rows = AiMetadataParser.Parse(json);
            CheckEqual("comfy seed", "123", Find(rows, "Seed"));
            CheckEqual("comfy steps", "20", Find(rows, "Steps"));
            CheckEqual("comfy cfg", "7", Find(rows, "CFG"));
            CheckEqual("comfy sampler", "euler", Find(rows, "Sampler"));
            CheckEqual("comfy scheduler", "normal", Find(rows, "Scheduler"));
            CheckEqual("comfy model via link", "sd_xl.safetensors", Find(rows, "Model"));
            CheckEqual("comfy positive via link", "a cat", Find(rows, "Prompt"));
            CheckEqual("comfy negative via link", "blurry", Find(rows, "Negative prompt"));
            CheckEqual("comfy size via latent link", "1024 x 1024", Find(rows, "Size"));

            // 프롬프트용 키가 아닌 일반 입력을 프롬프트로 오인하지 않아야 한다.
            var unrelated = "{\"1\":{\"class_type\":\"KSampler\",\"inputs\":{\"seed\":1,\"steps\":1,\"cfg\":1," +
                "\"positive\":[\"2\",0],\"negative\":[\"2\",0]}}," +
                "\"2\":{\"class_type\":\"LoadImage\",\"inputs\":{\"file_path\":\"in.png\",\"any\":\"not a prompt\"}}}";
            var unrelatedRows = AiMetadataParser.Parse(unrelated);
            Check("comfy does not treat file_path as prompt", Find(unrelatedRows, "Prompt") == null,
                "Prompt=" + (Find(unrelatedRows, "Prompt") ?? "<null>"));
        }

        private static void TestPngTextChunks()
        {
            var path = Path.Combine(Path.GetTempPath(), "ZipPlaSelfTestText.png");
            var latin1 = Encoding.GetEncoding(28591);
            var builder = new List<byte>();
            builder.AddRange(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

            AddPngChunk(builder, "tEXt", Concat(latin1.GetBytes("parameters"), new byte[] { 0 },
                latin1.GetBytes("Steps: 20, Seed: 7")));

            AddPngChunk(builder, "zTXt", Concat(latin1.GetBytes("comment"), new byte[] { 0, 0 },
                ZlibCompress(latin1.GetBytes("compressed value"))));

            // iTXt: keyword + NUL + 압축 플래그 + 압축 방식 + 언어 태그 + NUL + 번역 키워드 + NUL + 본문
            AddPngChunk(builder, "iTXt", Concat(Encoding.UTF8.GetBytes("prompt"), new byte[] { 0, 0, 0, 0, 0 },
                Encoding.UTF8.GetBytes("a cat")));

            AddPngChunk(builder, "iTXt", Concat(Encoding.UTF8.GetBytes("workflow"), new byte[] { 0, 1, 0, 0, 0 },
                ZlibCompress(Encoding.UTF8.GetBytes("compressed workflow"))));

            AddPngChunk(builder, "IEND", new byte[0]);
            File.WriteAllBytes(path, builder.ToArray());

            var output = new List<KeyValuePair<string, string>>();
            ImageMetadataReader.ReadPngText(path, output);

            CheckEqual("png tEXt value", "Steps: 20, Seed: 7", FindText(output, "parameters"));
            CheckEqual("png zTXt value", "compressed value", FindText(output, "comment"));
            CheckEqual("png iTXt value", "a cat", FindText(output, "prompt"));
            CheckEqual("png compressed iTXt value", "compressed workflow", FindText(output, "workflow"));

            // PNG 전체 경로(TagLib/Message 포함)도 깨지지 않아야 한다.
            var rows = ImageMetadataReader.Read(path);
            Check("read() finds the parsed A1111 rows", Find(rows, "Steps") == "20", "Steps=" + (Find(rows, "Steps") ?? "<null>"));
            Check("read() reports the file name", Find(rows, Message.Metadata_FileName) == Path.GetFileName(path));

            TryDelete(path);
        }

        private static void TestPngDecompressionBombIsBounded()
        {
            var path = Path.Combine(Path.GetTempPath(), "ZipPlaSelfTestBomb.png");
            var latin1 = Encoding.GetEncoding(28591);
            var bomb = new byte[8 * 1024 * 1024];
            for (var i = 0; i < bomb.Length; i++) bomb[i] = (byte)'A';

            var builder = new List<byte>();
            builder.AddRange(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            AddPngChunk(builder, "zTXt", Concat(latin1.GetBytes("parameters"), new byte[] { 0, 0 }, ZlibCompress(bomb)));
            AddPngChunk(builder, "IEND", new byte[0]);
            File.WriteAllBytes(path, builder.ToArray());

            var output = new List<KeyValuePair<string, string>>();
            ImageMetadataReader.ReadPngText(path, output);

            var value = FindText(output, "parameters");
            Check("zTXt bomb is bounded", value != null && value.Length <= MetadataRows.MaxStoredValueChars,
                "length=" + (value == null ? "<null>" : value.Length.ToString(CultureInfo.InvariantCulture)));

            TryDelete(path);
        }

        private static void TestSingleInstancePayload()
        {
            var arguments = new[] { @"C:\ZipPla\ZipPla.exe", @"some\relative\file.zip", "-c", @"C:\abs\x.png" };
            var payload = SingleInstanceManager.JoinPayload(arguments);
            var split = SingleInstanceManager.SplitPayload(payload);

            Check("payload keeps argument count", split.Length == arguments.Length, "count=" + split.Length);
            CheckEqual("payload keeps argv0", arguments[0], split[0]);
            Check("payload absolutizes relative paths", split[1] != null && Path.IsPathRooted(split[1]), split[1]);
            CheckEqual("payload keeps switches", "-c", split[2]);
            CheckEqual("payload keeps absolute paths", arguments[3], split[3]);
            Check("payload contains no NUL from input", split[1].IndexOf('\0') < 0);

            // 구버전 클라이언트는 개행으로 구분해 보냈다.
            Check("legacy newline payload", SingleInstanceManager.SplitPayload("a\nb").Length == 2);
            Check("empty payload", SingleInstanceManager.SplitPayload("").Length == 0);

            // 경로에 개행이 있어도 NUL 구분이면 쪼개지지 않는다.
            var withNewline = new[] { "exe", "C:\\a\nb.png" };
            var roundTrip = SingleInstanceManager.SplitPayload(SingleInstanceManager.JoinPayload(withNewline));
            CheckEqual("payload survives newline in path", withNewline[1], roundTrip.Length > 1 ? roundTrip[1] : null);
        }

        // d2: 썸네일 동시 실행 제한기. 메모리 기준으로 동시 작업 수를 제한하는 부분은
        //     조용히 고장나면 "가끔 메모리 폭발" 로만 드러나므로 여기서 검증한다.
        private static void TestThumbnailLimiter()
        {
            const long capacity = 100;
            var throttle = new WeightedThrottle(capacity);

            CheckEqual("throttle clamp over capacity", "100", throttle.Clamp(1000).ToString(CultureInfo.InvariantCulture));
            CheckEqual("throttle clamp under one", "1", throttle.Clamp(0).ToString(CultureInfo.InvariantCulture));

            // 예산을 넘지 않으면 즉시 통과한다
            var first = throttle.AcquireAsync(60).GetAwaiter().GetResult();
            CheckEqual("throttle used after first", "60", throttle.Used.ToString(CultureInfo.InvariantCulture));

            var second = throttle.AcquireAsync(40).GetAwaiter().GetResult();
            CheckEqual("throttle used after fill", "100", throttle.Used.ToString(CultureInfo.InvariantCulture));

            // 넘치면 대기한다
            var pending = throttle.AcquireAsync(10);
            Check("throttle blocks over capacity", !pending.IsCompleted, "pending state " + pending.Status);
            CheckEqual("throttle waiting count", "1", throttle.WaitingCount.ToString(CultureInfo.InvariantCulture));

            // 하나를 놓으면 대기하던 작업이 들어온다
            first.Dispose();
            Check("throttle releases waiter", pending.Wait(5000), "waiter did not complete");
            CheckEqual("throttle used after release", "50", throttle.Used.ToString(CultureInfo.InvariantCulture));
            CheckEqual("throttle waiting count after release", "0", throttle.WaitingCount.ToString(CultureInfo.InvariantCulture));

            // 리스를 두 번 놓아도 예산이 망가지지 않아야 한다
            second.Dispose();
            second.Dispose();
            pending.GetAwaiter().GetResult().Dispose();
            CheckEqual("throttle used after all released", "0", throttle.Used.ToString(CultureInfo.InvariantCulture));

            // FIFO 순서를 지켜야 한다(새치기하면 큰 작업이 굶는다).
            // 두 작업이 동시에 들어가지 못하는 크기로 만들어야 실행 순서가 확정된다.
            // (둘 다 들어가면 완료 통지 순서가 스레드풀 사정에 따라 뒤바뀐다)
            var order = new List<int>();
            var orderLocker = new object();
            var blocker = throttle.AcquireAsync(capacity).GetAwaiter().GetResult();
            var big = throttle.AcquireAsync(80).ContinueWith(t => { lock (orderLocker) order.Add(1); t.Result.Dispose(); });
            var small = throttle.AcquireAsync(30).ContinueWith(t => { lock (orderLocker) order.Add(2); t.Result.Dispose(); });
            System.Threading.Thread.Sleep(200);
            Check("throttle keeps both waiting", !big.IsCompleted && !small.IsCompleted && throttle.WaitingCount == 2,
                "waiting " + throttle.WaitingCount);
            blocker.Dispose();
            Task.WaitAll(big, small);
            CheckEqual("throttle fifo order", "1,2", string.Join(",", order));

            Check("thumb limiter capacity positive", ThumbnailLimiter.TotalCapacity > 0);
            Check("thumb limiter parallelism bounded", ThumbnailLimiter.MaxParallelism >= 2 && ThumbnailLimiter.MaxParallelism <= 16);

            // 작은 작업들이라도 max parallelism 개를 넘길 수 없어야 한다
            var floor = ThumbnailLimiter.WeighUnits(1);
            var maxConcurrent = (int)(ThumbnailLimiter.TotalCapacity / floor);
            Check("thumb limiter concurrency bounded", maxConcurrent <= ThumbnailLimiter.MaxParallelism,
                "floor " + floor + " => " + maxConcurrent + " > " + ThumbnailLimiter.MaxParallelism);

            // 큰 이미지 하나는 혼자서도 통과해야 한다(영원히 대기하지 않도록)
            var huge = ThumbnailLimiter.AcquireAsync(long.MaxValue / 2);
            Check("thumb limiter huge job proceeds", huge.Wait(5000), "huge job blocked");
            huge.GetAwaiter().GetResult().Dispose();

            // m1: 큐에서 대기 중인 작업은 취소되면 조용히 빠지고 슬롯을 비운다.
            //     (빠른 스크롤 시 화면 밖 항목이 보이는 항목을 막지 않도록)
            var cts = new CancellationTokenSource();
            var cblocker = throttle.AcquireAsync(capacity).GetAwaiter().GetResult();
            var cpending = throttle.AcquireAsync(10, cts.Token);
            Check("cancel waits while queued", !cpending.IsCompleted, "pending state " + cpending.Status);
            cts.Cancel();
            try { cpending.Wait(5000); } catch (AggregateException) { }
            CheckEqual("cancel completes task", TaskStatus.Canceled.ToString(), cpending.Status.ToString());
            CheckEqual("cancel frees queue slot", "0", throttle.WaitingCount.ToString(CultureInfo.InvariantCulture));
            CheckEqual("cancel keeps used", capacity.ToString(CultureInfo.InvariantCulture), throttle.Used.ToString(CultureInfo.InvariantCulture));
            cblocker.Dispose();
            var afterCancel = throttle.AcquireAsync(10);
            Check("acquire works after cancel", afterCancel.Wait(5000), "blocked after cancel");
            afterCancel.GetAwaiter().GetResult().Dispose();
            CheckEqual("used clean after cancel test", "0", throttle.Used.ToString(CultureInfo.InvariantCulture));

            // m1: 이미 취소된 토큰으로는 대기열에 들어가지도 않는다.
            var preCanceled = new CancellationTokenSource();
            preCanceled.Cancel();
            var preTask = throttle.AcquireAsync(10, preCanceled.Token);
            CheckEqual("pre-canceled task state", TaskStatus.Canceled.ToString(), preTask.Status.ToString());
            CheckEqual("pre-canceled leaves no waiter", "0", throttle.WaitingCount.ToString(CultureInfo.InvariantCulture));
        }

        private static void TestScaledThumbnailSource()
        {
            Check("scaled source rejects non image", !ImageLoader.SupportsScaledThumbnailSource("a.txt"));
            Check("scaled source rejects tiff", !ImageLoader.SupportsScaledThumbnailSource("a.tif"));
            Check("scaled source accepts png", ImageLoader.SupportsScaledThumbnailSource("a.png"));
            Check("scaled source accepts jpeg", ImageLoader.SupportsScaledThumbnailSource("a.JPEG"));

            // 준비한 이미지가 있으면 실제 축소 디코딩이 요청 크기의 3.5배 이상을 유지하는지 확인한다
            var temp = Path.Combine(Path.GetTempPath(), "ZipPlaSelfTestScaled.png");
            try
            {
                using (var bmp = new System.Drawing.Bitmap(1200, 900))
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.Clear(System.Drawing.Color.White);
                    bmp.Save(temp, System.Drawing.Imaging.ImageFormat.Png);
                }

                ImageInfo info;
                var scaled = ImageLoader.GetScaledThumbnailSource(temp, new System.Drawing.Size(160, 120), out info);
                Check("scaled source returns bitmap", scaled != null);
                if (scaled != null)
                {
                    using (scaled)
                    {
                        // 160*3.5 = 560, 120*3.5 = 420 이상이어야 잘라내기 모드에서도 화질이 유지된다
                        Check("scaled source keeps density", scaled.Width >= 560 && scaled.Height >= 420,
                            scaled.Width + "x" + scaled.Height);
                        Check("scaled source keeps aspect", Math.Abs((double)scaled.Width / scaled.Height - 1200.0 / 900.0) < 0.02,
                            scaled.Width + "x" + scaled.Height);
                    }
                    CheckEqual("scaled source keeps original size info", "1200x900", info.Size.Width + "x" + info.Size.Height);
                }

                // 이미 작은 이미지는 기존 경로를 쓴다(캐시 크기 조건을 만족시킬 수 없다)
                ImageInfo smallInfo;
                var small = ImageLoader.GetScaledThumbnailSource(temp, new System.Drawing.Size(1000, 800), out smallInfo);
                Check("scaled source skipped when original is small", small == null);
                if (small != null) small.Dispose();
                // m1: 건너뛸 때도 파싱된 헤더는 돌려줘서 폴백이 ImageInfo 를 두 번 만들지 않는다
                Check("small image keeps header info", smallInfo != null && smallInfo.Size.Width == 1200 && smallInfo.Size.Height == 900,
                    smallInfo == null ? "null" : smallInfo.Size.Width + "x" + smallInfo.Size.Height);
            }
            catch (Exception ex)
            {
                Check("scaled source test", false, ex.ToString());
            }
            finally
            {
                TryDelete(temp);
            }
        }

        private static void TestArchiveExtensionList()
        {
            Check("archive .zip", ContextMenuRegistrationManager.IsArchiveExtension(".zip"));
            Check("archive .ZIP (case insensitive)", ContextMenuRegistrationManager.IsArchiveExtension(".ZIP"));
            Check("archive .rar", ContextMenuRegistrationManager.IsArchiveExtension(".rar"));
            Check("not an archive: .png", !ContextMenuRegistrationManager.IsArchiveExtension(".png"));
            Check("not an archive: null", !ContextMenuRegistrationManager.IsArchiveExtension(null));
        }

        private static void TestMessages()
        {
            Check("message: metadata loading", !string.IsNullOrEmpty(Message.Metadata_Loading));
            Check("message: context menu registration", !string.IsNullOrEmpty(Message.ContextMenuRegistration));
            Check("message: context menu registration failure", !string.IsNullOrEmpty(Message.ContextMenuRegistrationFailed));
            Check("message: open with ZipPla", !string.IsNullOrEmpty(Message.ContextMenuOpenWithZipPla));
            Check("message: only one window", !string.IsNullOrEmpty(Message.OnlyOneWindow));
        }

        #endregion

        #region helpers

        private static void AddPngChunk(List<byte> output, string type, byte[] data)
        {
            output.AddRange(new byte[]
            {
                (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length,
            });
            output.AddRange(Encoding.ASCII.GetBytes(type));
            output.AddRange(data);
            output.AddRange(new byte[] { 0, 0, 0, 0 }); // CRC is not validated by the reader
        }

        private static byte[] Concat(params byte[][] parts)
        {
            var result = new byte[parts.Sum(p => p.Length)];
            var offset = 0;
            foreach (var part in parts)
            {
                Buffer.BlockCopy(part, 0, result, offset, part.Length);
                offset += part.Length;
            }
            return result;
        }

        /// <summary>테스트용 zlib 스트림(헤더 + raw deflate + Adler32 자리). 판독기는 Adler32 를 검사하지 않는다.</summary>
        private static byte[] ZlibCompress(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78);
                ms.WriteByte(0x9C);
                using (var deflate = new DeflateStream(ms, CompressionMode.Compress, true))
                {
                    deflate.Write(data, 0, data.Length);
                }
                ms.Write(new byte[] { 0, 0, 0, 0 }, 0, 4);
                return ms.ToArray();
            }
        }

        private static string FindText(List<KeyValuePair<string, string>> items, string key)
        {
            foreach (var item in items)
            {
                if (string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)) return item.Value;
            }
            return null;
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (Exception ex) { Debug.WriteLine(ex); }
        }

        #endregion
    }
}
