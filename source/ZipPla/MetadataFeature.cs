// MetadataFeature.cs
// Image metadata panel inspired by Tiefsee4's MainExif/FileMetadataService.
// Supports PNG tEXt/zTXt/iTXt, JPEG XMP/COM, WebP XMP, TagLib EXIF fields,
// A1111 parameters, NovelAI JSON, and ComfyUI prompt JSON.
//
// d1 changes:
//   - Reading metadata no longer happens on the UI thread. Selecting an item used to scan the
//     whole file (up to 128MB of chunks), run TagLib and parse up to 5MB of JSON synchronously,
//     which froze the window on large files and network shares.
//   - Read results are cached per path (invalidated by LastWriteTimeUtc).
//   - zTXt/iTXt decompression is bounded, so a small compressed chunk can no longer expand to
//     gigabytes (decompression bomb).
//   - The stored value is kept complete for copying; only the grid display is truncated.
//   - Copying uses Clipboard.SetDataObject(value, copy: true) so the text survives process exit.
//   - The duplicated Add/AddUnique/Truncate helpers are now shared through MetadataRows.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ZipPla
{
    public struct MetadataRow
    {
        public string Name;

        /// <summary>표시용으로 잘리지 않은 원본 값. 잘라내는 처리는 UI 쪽에서 한다.</summary>
        public string Value;

        public MetadataRow(string name, string value)
        {
            Name = name ?? "";
            Value = value ?? "";
        }
    }

    /// <summary>각 파서가 공유하는 행 추가/중복 제거/길이 제한 헬퍼.</summary>
    internal static class MetadataRows
    {
        private const int Latin1CodePage = 28591;

        /// <summary>한 값을 무한정 메모리에 담지 않기 위한 안전 상한(표시용 잘라내기는 UI 가 따로 한다).</summary>
        public const int MaxStoredValueChars = 200000;

        public static readonly Encoding Latin1 = Encoding.GetEncoding(Latin1CodePage);

        public static void Add(List<MetadataRow> rows, string name, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) rows.Add(new MetadataRow(name, value));
        }

        public static void AddUnique(List<MetadataRow> rows, string name, string value)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value)) return;
            if (rows.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase) && string.Equals(r.Value, value, StringComparison.Ordinal))) return;
            rows.Add(new MetadataRow(name, value));
        }

        public static string Truncate(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= MaxStoredValueChars) return value;
            // 결과 길이가 상한을 넘지 않도록 "..." 자리까지 포함해서 자른다.
            return value.Substring(0, MaxStoredValueChars - 3) + "...";
        }
    }

    internal static class A1111ParameterParser
    {
        public static List<MetadataRow> Parse(string text)
        {
            var result = new List<MetadataRow>();
            if (string.IsNullOrWhiteSpace(text)) return result;

            text = text.Trim();
            if (!text.StartsWith("Prompt:", StringComparison.OrdinalIgnoreCase) &&
                !text.StartsWith("Negative prompt:", StringComparison.OrdinalIgnoreCase) &&
                !text.StartsWith("Steps:", StringComparison.OrdinalIgnoreCase))
            {
                text = "Prompt: " + text;
            }

            var prompt = ExtractBlock(text, "Prompt:", new[] { "Negative prompt:", "Steps:" });
            var negative = ExtractBlock(text, "Negative prompt:", new[] { "Steps:" });
            var steps = ExtractBlock(text, "Steps:", new string[0]);

            MetadataRows.Add(result, "Prompt", prompt);
            MetadataRows.Add(result, "Negative prompt", negative);

            // d1 fix: ExtractBlock 이 "Steps:" 라벨을 제거한 뒤의 문자열을 돌려주므로, 그대로
            // ParseKeyValues 에 넘기면 첫 번째 값(예: "28")에 키가 없어서 통째로 버려졌다.
            // 실제 A1111 출력에서 Steps 가 사라지는 원인이었다.
            if (!string.IsNullOrWhiteSpace(steps))
            {
                foreach (var kv in ParseKeyValues("Steps: " + steps.Trim())) MetadataRows.Add(result, kv.Key, kv.Value);
            }
            return result;
        }

        private static string ExtractBlock(string text, string start, string[] ends)
        {
            var i = text.IndexOf(start, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i += start.Length;
            var end = text.Length;
            foreach (var marker in ends)
            {
                var j = text.IndexOf(marker, i, StringComparison.OrdinalIgnoreCase);
                if (j >= 0 && j < end) end = j;
            }
            return text.Substring(i, end - i).Trim();
        }

        private static IEnumerable<KeyValuePair<string, string>> ParseKeyValues(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;
            var parts = new List<string>();
            var start = 0;
            var quote = false;
            var square = 0;
            var curly = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '"') quote = !quote;
                else if (!quote)
                {
                    if (c == '[') square++;
                    else if (c == ']') square = Math.Max(0, square - 1);
                    else if (c == '{') curly++;
                    else if (c == '}') curly = Math.Max(0, curly - 1);
                    else if (c == ',' && square == 0 && curly == 0)
                    {
                        parts.Add(text.Substring(start, i - start));
                        start = i + 1;
                    }
                }
            }
            parts.Add(text.Substring(start));

            foreach (var p in parts)
            {
                var colon = p.IndexOf(':');
                if (colon < 0) continue;
                var key = p.Substring(0, colon).Trim();
                var value = p.Substring(colon + 1).Trim().Trim('"');
                if (key.Length > 0 && value.Length > 0)
                    yield return new KeyValuePair<string, string>(key, value.Replace("\\n", "\n"));
            }
        }
    }

    internal static class AiMetadataParser
    {
        private const int MaxJsonChars = 5 * 1024 * 1024;

        // d1: "any"/"file_path" 는 특정 노드 종류를 가리키지 않는 일반 키라서 프롬프트로 오인되기 쉽고,
        //     positive/negative 목록에 "CLIPTextEncode"/"populated_text"/"text" 가 함께 들어 있으면
        //     KSampler 의 negative 링크를 따라가도 긍정 프롬프트가 잡힐 수 있었다.
        //     여기서는 실제 프롬프트 텍스트를 담는 입력 이름만 남긴다.
        private static readonly string[] PromptKeys = { "positive", "text_positive", "populated_text", "text_g", "text_b", "base_ctx", "text_pos_g", "prompt", "text", "string", "t5xxl", "Prompt T5 XXL" };
        private static readonly string[] NegativeKeys = { "negative", "text_negative", "n_prompt", "text_neg_g", "text", "string", "prompt" };
        private static readonly string[] ModelKeys = { "ckpt_name", "model", "base_ckpt_name", "unet_name" };
        private static readonly string[] VaeKeys = { "vae_name", "vae" };

        public static List<MetadataRow> Parse(string text)
        {
            var rows = new List<MetadataRow>();
            if (string.IsNullOrWhiteSpace(text)) return rows;

            var json = TryDeserialize(text);
            if (json == null) return rows;

            // NovelAI/normal JSON is intentionally handled before ComfyUI so simple JSON remains readable.
            var root = json as Dictionary<string, object>;
            if (root == null) return rows;

            if (root.ContainsKey("extraMetadata"))
            {
                var extra = root["extraMetadata"] as string;
                var extraObj = TryDeserialize(extra) as Dictionary<string, object>;
                if (extraObj != null) AddKnownJsonFields(rows, extraObj);
            }

            if (HasAny(root, "prompt", "uc", "cfg_rescale", "request_type"))
                AddNovelAi(rows, root);

            // ComfyUI prompt graph.
            var nodes = root;
            var comfyRows = ParseComfy(nodes);
            foreach (var row in comfyRows) MetadataRows.AddUnique(rows, row.Name, row.Value);

            // If it was just ordinary JSON, show its top-level keys as Tiefsee's getNormalJson does.
            if (comfyRows.Count == 0)
            {
                foreach (var kv in root)
                {
                    if (kv.Key == "extraMetadata") continue;
                    var value = FormatObject(kv.Value);
                    if (!string.IsNullOrWhiteSpace(value)) MetadataRows.AddUnique(rows, kv.Key, value);
                }
            }
            return rows;
        }

        private static object TryDeserialize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (text.Length > MaxJsonChars) text = text.Substring(0, MaxJsonChars);
            try
            {
                var js = new JavaScriptSerializer { MaxJsonLength = MaxJsonChars, RecursionLimit = 100 };
                return js.DeserializeObject(text);
            }
            catch
            {
                // Some PNG metadata contains a JSON object prefixed with a label.
                var first = text.IndexOf('{');
                var last = text.LastIndexOf('}');
                if (first >= 0 && last > first && last - first + 1 <= MaxJsonChars)
                {
                    try
                    {
                        var js = new JavaScriptSerializer { MaxJsonLength = MaxJsonChars, RecursionLimit = 100 };
                        return js.DeserializeObject(text.Substring(first, last - first + 1));
                    }
                    catch { }
                }
                return null;
            }
        }

        private static void AddNovelAi(List<MetadataRow> rows, Dictionary<string, object> root)
        {
            var order = new[] { "prompt", "uc", "steps", "sampler", "cfg_rescale", "seed", "width", "height", "request_type" };
            foreach (var key in order)
            {
                object value;
                if (root.TryGetValue(key, out value)) MetadataRows.AddUnique(rows, key, FormatObject(value));
            }
        }

        private static void AddKnownJsonFields(List<MetadataRow> rows, Dictionary<string, object> obj)
        {
            var map = new[]
            {
                new[] { "prompt", "Prompt" }, new[] { "negativePrompt", "Negative prompt" },
                new[] { "steps", "Steps" }, new[] { "sampler", "Sampler" },
                new[] { "cfgScale", "CFG" }, new[] { "seed", "Seed" }
            };
            foreach (var pair in map)
            {
                object v;
                if (obj.TryGetValue(pair[0], out v)) MetadataRows.AddUnique(rows, pair[1], FormatObject(v));
            }
        }

        private static List<MetadataRow> ParseComfy(Dictionary<string, object> root)
        {
            var rows = new List<MetadataRow>();
            // ComfyUI prompt JSON is a dictionary of numeric node ids plus optional extraMetadata.
            var nodes = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in root)
            {
                var node = kv.Value as Dictionary<string, object>;
                if (node == null || !node.ContainsKey("inputs")) continue;
                nodes[kv.Key] = node;
            }
            if (nodes.Count == 0) return rows;

            var candidates = new List<Dictionary<string, object>>();
            foreach (var node in nodes.Values)
            {
                var classType = GetString(node, "class_type");
                var inputs = GetDict(node, "inputs");
                if (inputs == null) continue;
                if (!string.IsNullOrEmpty(classType) &&
                    (classType.IndexOf("KSampler", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     classType.IndexOf("Sampler", StringComparison.OrdinalIgnoreCase) >= 0) &&
                    (inputs.ContainsKey("steps") || inputs.ContainsKey("seed") || inputs.ContainsKey("noise_seed") || inputs.ContainsKey("cfg")))
                {
                    candidates.Add(node);
                }
            }

            // Prefer the last sampler in JSON order, which is normally the final generation sampler.
            if (candidates.Count > 0)
            {
                var node = candidates[candidates.Count - 1];
                var inputs = GetDict(node, "inputs");
                AddResolved(rows, "Seed", ResolveInput(inputs, nodes, new[] { "seed", "noise_seed" }, true));
                AddResolved(rows, "Steps", ResolveInput(inputs, nodes, new[] { "steps" }, true));
                AddResolved(rows, "CFG", ResolveInput(inputs, nodes, new[] { "cfg" }, true));
                AddResolved(rows, "Sampler", ResolveInput(inputs, nodes, new[] { "sampler_name", "sampler" }, false));
                AddResolved(rows, "Scheduler", ResolveInput(inputs, nodes, new[] { "scheduler" }, false));
                AddResolved(rows, "Denoise", ResolveInput(inputs, nodes, new[] { "denoise", "denoise_strength" }, true));

                AddResolved(rows, "Model", ResolveInput(inputs, nodes, ModelKeys, false));
                AddResolved(rows, "VAE", ResolveInput(inputs, nodes, VaeKeys, false));

                var positiveLink = GetLinkedId(GetValue(inputs, "positive")) ?? GetLinkedId(GetValue(inputs, "sdxl_tuple"));
                var negativeLink = GetLinkedId(GetValue(inputs, "negative")) ?? GetLinkedId(GetValue(inputs, "sdxl_tuple"));
                AddResolved(rows, "Prompt", ResolvePrompt(positiveLink, nodes, PromptKeys, new HashSet<string>()));
                AddResolved(rows, "Negative prompt", ResolvePrompt(negativeLink, nodes, NegativeKeys, new HashSet<string>()));

                var latent = GetLinkedId(GetValue(inputs, "latent_image"));
                var size = ResolveSize(latent, nodes, new HashSet<string>());
                AddResolved(rows, "Size", size);
            }

            // A few ComfyUI exports put generation_data/extraMetadata alongside the prompt graph.
            object generation;
            if (root.TryGetValue("generation_data", out generation))
            {
                var gen = generation as string;
                var genObj = TryDeserialize(gen) as Dictionary<string, object>;
                if (genObj != null) AddKnownJsonFields(rows, genObj);
            }
            object extra;
            if (root.TryGetValue("extraMetadata", out extra))
            {
                var extraObj = TryDeserialize(extra as string) as Dictionary<string, object>;
                if (extraObj != null) AddKnownJsonFields(rows, extraObj);
            }
            return rows;
        }

        private static string ResolveInput(Dictionary<string, object> inputs, Dictionary<string, Dictionary<string, object>> nodes, string[] keys, bool allowNumber)
        {
            if (inputs == null) return null;
            foreach (var key in keys)
            {
                object value;
                if (!TryGetIgnoreCase(inputs, key, out value)) continue;
                var scalar = Scalar(value, allowNumber);
                if (scalar != null) return scalar;
                var id = GetLinkedId(value);
                if (id != null)
                {
                    var result = ResolvePrompt(id, nodes, keys, new HashSet<string>());
                    if (!string.IsNullOrWhiteSpace(result)) return result;
                }
            }
            return null;
        }

        private static string ResolvePrompt(string id, Dictionary<string, Dictionary<string, object>> nodes, string[] searchKeys, HashSet<string> visited)
        {
            if (id == null || visited.Contains(id)) return null;
            Dictionary<string, object> node;
            if (!nodes.TryGetValue(id, out node)) return null;
            visited.Add(id);
            var inputs = GetDict(node, "inputs");
            if (inputs == null) return null;

            var classType = GetString(node, "class_type");
            if (string.Equals(classType, "PrimitiveStringMultiline", StringComparison.OrdinalIgnoreCase))
            {
                object v;
                if (TryGetIgnoreCase(inputs, "value", out v))
                {
                    var s = Scalar(v, false);
                    if (s != null) return s;
                    var link = GetLinkedId(v);
                    if (link != null) return ResolvePrompt(link, nodes, searchKeys, visited);
                }
            }

            // Exact input key first.
            foreach (var key in searchKeys)
            {
                object v;
                if (!TryGetIgnoreCase(inputs, key, out v)) continue;
                var s = Scalar(v, true);
                if (s != null && (key.IndexOf("prompt", StringComparison.OrdinalIgnoreCase) >= 0 || key == "text" || key == "string" || key == "positive" || key == "negative" || key == "n_prompt")) return s;
                var link = GetLinkedId(v);
                if (link != null)
                {
                    var r = ResolvePrompt(link, nodes, searchKeys, visited);
                    if (!string.IsNullOrWhiteSpace(r)) return r;
                }
            }

            // Generic linked-input fallback, matching Tiefsee's getUnknownKey behavior.
            foreach (var v in inputs.Values)
            {
                var link = GetLinkedId(v);
                if (link != null)
                {
                    var r = ResolvePrompt(link, nodes, searchKeys, visited);
                    if (!string.IsNullOrWhiteSpace(r)) return r;
                }
            }
            foreach (var kv in inputs)
            {
                if (searchKeys.Any(k => kv.Key.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    var s = Scalar(kv.Value, true);
                    if (s != null) return s;
                }
            }
            return null;
        }

        private static string ResolveSize(string id, Dictionary<string, Dictionary<string, object>> nodes, HashSet<string> visited)
        {
            if (id == null || visited.Contains(id)) return null;
            Dictionary<string, object> node;
            if (!nodes.TryGetValue(id, out node)) return null;
            visited.Add(id);
            var inputs = GetDict(node, "inputs");
            if (inputs == null) return null;
            object w, h;
            if (TryGetIgnoreCase(inputs, "width", out w) && TryGetIgnoreCase(inputs, "height", out h))
            {
                var ws = Scalar(w, true);
                var hs = Scalar(h, true);
                if (ws != null && hs != null) return ws + " x " + hs;
            }
            foreach (var v in inputs.Values)
            {
                var next = GetLinkedId(v);
                if (next != null)
                {
                    var result = ResolveSize(next, nodes, visited);
                    if (result != null) return result;
                }
            }
            return null;
        }

        private static object GetValue(Dictionary<string, object> dict, string key)
        {
            object v;
            return TryGetIgnoreCase(dict, key, out v) ? v : null;
        }

        private static bool TryGetIgnoreCase(Dictionary<string, object> dict, string key, out object value)
        {
            foreach (var kv in dict)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = kv.Value;
                    return true;
                }
            }
            value = null;
            return false;
        }

        private static Dictionary<string, object> GetDict(Dictionary<string, object> dict, string key)
        {
            object v;
            return TryGetIgnoreCase(dict, key, out v) ? v as Dictionary<string, object> : null;
        }

        private static string GetString(Dictionary<string, object> dict, string key)
        {
            object v;
            return TryGetIgnoreCase(dict, key, out v) ? Scalar(v, false) : null;
        }

        private static string GetLinkedId(object value)
        {
            var list = value as ArrayList;
            if (list != null && list.Count > 0) return list[0] == null ? null : list[0].ToString();
            var arr = value as object[];
            if (arr != null && arr.Length > 0) return arr[0] == null ? null : arr[0].ToString();
            return null;
        }

        private static string Scalar(object value, bool allowNumber)
        {
            if (value == null) return null;
            if (value is string) return (string)value;
            if (!allowNumber) return null;
            if (value is int || value is long || value is double || value is decimal || value is float)
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            return null;
        }

        private static string FormatObject(object value)
        {
            if (value == null) return null;
            var s = value as string;
            if (s != null) return s.Trim();
            if (value is bool) return ((bool)value) ? "true" : "false";
            if (value is int || value is long || value is double || value is decimal || value is float)
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            try
            {
                var js = new JavaScriptSerializer { MaxJsonLength = MaxJsonChars, RecursionLimit = 100 };
                return js.Serialize(value);
            }
            catch { return value.ToString(); }
        }

        private static bool HasAny(Dictionary<string, object> root, params string[] keys)
        {
            foreach (var key in keys) if (root.Keys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase))) return true;
            return false;
        }

        private static void AddResolved(List<MetadataRow> rows, string name, string value)
        {
            MetadataRows.Add(rows, name, value);
        }
    }

    public static class ImageMetadataReader
    {
        private const long MaxScanBytes = 128L * 1024 * 1024;
        private const int MaxChunkBytes = 16 * 1024 * 1024;
        private const int MaxJsonChars = 5 * 1024 * 1024;
        private const int MaxDecompressedBytes = 256 * 1024;

        public static List<MetadataRow> Read(string path)
        {
            var rows = new List<MetadataRow>();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                if (!string.IsNullOrWhiteSpace(path)) rows.Add(new MetadataRow(Message.Metadata_NotAvailable, Message.Metadata_NotAvailableForArchiveItem));
                return rows;
            }

            FileInfo fi = null;
            try { fi = new FileInfo(path); } catch { }
            if (fi != null)
            {
                MetadataRows.Add(rows, Message.Metadata_FileName, fi.Name);
                MetadataRows.Add(rows, Message.Metadata_Folder, fi.DirectoryName);
                MetadataRows.Add(rows, Message.Metadata_FileSize, FormatBytes(fi.Length));
                MetadataRows.Add(rows, Message.Metadata_Modified, fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
                if (fi.Length > MaxScanBytes)
                {
                    MetadataRows.Add(rows, Message.Metadata_NotAvailable, Message.Metadata_FileTooLarge);
                    return rows;
                }
            }

            var textual = new List<KeyValuePair<string, string>>();
            try { ReadPngText(path, textual); } catch (Exception ex) { Program.LogException(ex, "ImageMetadataReader.ReadPngText"); }
            try { ReadJpegXmp(path, textual); } catch (Exception ex) { Program.LogException(ex, "ImageMetadataReader.ReadJpegXmp"); }
            try { ReadWebpText(path, textual); } catch (Exception ex) { Program.LogException(ex, "ImageMetadataReader.ReadWebpText"); }

            foreach (var item in textual)
            {
                if (string.IsNullOrWhiteSpace(item.Value)) continue;
                var key = item.Key ?? "Textual Data";
                var value = item.Value.Trim();

                // Tiefsee behavior: parameters / metadata / prompt / workflow are parsed first.
                if (string.Equals(key, "parameters", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "Comment", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "User Comment", StringComparison.OrdinalIgnoreCase))
                {
                    if (value.IndexOf("Steps:", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        foreach (var r in A1111ParameterParser.Parse(value)) MetadataRows.AddUnique(rows, r.Name, r.Value);
                        continue;
                    }
                    var ai = AiMetadataParser.Parse(value);
                    if (ai.Count > 0)
                    {
                        foreach (var r in ai) MetadataRows.AddUnique(rows, r.Name, r.Value);
                        continue;
                    }
                }

                if (string.Equals(key, "prompt", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "workflow", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "generation_data", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "metadata", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "ComfyScript", StringComparison.OrdinalIgnoreCase))
                {
                    var ai = AiMetadataParser.Parse(value);
                    if (ai.Count > 0)
                    {
                        foreach (var r in ai) MetadataRows.AddUnique(rows, r.Name, r.Value);
                    }
                    else
                    {
                        MetadataRows.AddUnique(rows, key, value);
                    }
                    continue;
                }

                MetadataRows.AddUnique(rows, key, value);
            }

            // TagLib EXIF. This follows Tiefsee's FileMetadataService concept while using the
            // TagLib copy already bundled with ZipPla.
            try
            {
                var tagFile = TagLib.File.Create(path);
                using (tagFile as IDisposable)
                {
                    var imageFile = tagFile as TagLib.Image.File;
                    if (imageFile != null)
                    {
                        var tag = imageFile.ImageTag;
                        if (tag != null)
                        {
                            MetadataRows.AddUnique(rows, Message.Metadata_CameraMake, tag.Make);
                            MetadataRows.AddUnique(rows, Message.Metadata_CameraModel, tag.Model);
                            MetadataRows.AddUnique(rows, Message.Metadata_Software, tag.Software);
                            MetadataRows.AddUnique(rows, Message.Metadata_Creator, tag.Creator);
                            MetadataRows.AddUnique(rows, Message.Metadata_Keywords, tag.Keywords != null ? string.Join(", ", tag.Keywords) : null);
                            MetadataRows.AddUnique(rows, Message.Metadata_DateTaken, tag.DateTime.HasValue ? tag.DateTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : null);
                            MetadataRows.AddUnique(rows, Message.Metadata_Orientation, tag.Orientation == TagLib.Image.ImageOrientation.None ? null : tag.Orientation.ToString());
                            MetadataRows.AddUnique(rows, Message.Metadata_ExposureTime, tag.ExposureTime.HasValue ? FormatExposure(tag.ExposureTime.Value) : null);
                            MetadataRows.AddUnique(rows, Message.Metadata_FNumber, tag.FNumber.HasValue ? "f/" + tag.FNumber.Value.ToString("0.#", CultureInfo.InvariantCulture) : null);
                            MetadataRows.AddUnique(rows, Message.Metadata_Iso, tag.ISOSpeedRatings.HasValue ? tag.ISOSpeedRatings.Value.ToString(CultureInfo.InvariantCulture) : null);
                            MetadataRows.AddUnique(rows, Message.Metadata_FocalLength, tag.FocalLength.HasValue ? tag.FocalLength.Value.ToString("0.#", CultureInfo.InvariantCulture) + " mm" : null);
                            MetadataRows.AddUnique(rows, Message.Metadata_Rating, tag.Rating.HasValue ? tag.Rating.Value.ToString(CultureInfo.InvariantCulture) : null);
                            if (tag.Latitude.HasValue && tag.Longitude.HasValue)
                                MetadataRows.AddUnique(rows, Message.Metadata_GpsLocation, tag.Latitude.Value.ToString("0.000000", CultureInfo.InvariantCulture) + ", " + tag.Longitude.Value.ToString("0.000000", CultureInfo.InvariantCulture));
                        }
                        var props = imageFile.Properties;
                        if (props != null && props.PhotoWidth > 0 && props.PhotoHeight > 0)
                            MetadataRows.AddUnique(rows, Message.Metadata_Dimensions, props.PhotoWidth + " x " + props.PhotoHeight);
                    }
                }
            }
            catch (Exception ex)
            {
                Program.LogException(ex, "ImageMetadataReader.TagLib");
            }

            return rows;
        }

        // SelfTest 에서 직접 호출한다.
        internal static void ReadPngText(string path, List<KeyValuePair<string, string>> output)
        {
            using (var fs = File.OpenRead(path))
            using (var br = new BinaryReader(fs))
            {
                if (fs.Length < 24) return;
                var sig = br.ReadBytes(8);
                if (sig.Length != 8 || sig[0] != 137 || sig[1] != 80 || sig[2] != 78 || sig[3] != 71) return;
                while (fs.Position + 12 <= fs.Length)
                {
                    var len = ReadUInt32BE(br);
                    var typeBytes = br.ReadBytes(4);
                    if (typeBytes.Length != 4) break;
                    var type = Encoding.ASCII.GetString(typeBytes);
                    if (len > MaxChunkBytes || fs.Position + len + 4 > fs.Length) break;
                    var data = br.ReadBytes((int)len);
                    br.ReadBytes(4); // CRC
                    if (type == "IEND") break;
                    if (type == "tEXt") ParsePngTextChunk(data, output);
                    else if (type == "zTXt") ParsePngZtxtChunk(data, output);
                    else if (type == "iTXt") ParsePngItxtChunk(data, output);
                }
            }
        }

        private static void ParsePngTextChunk(byte[] data, List<KeyValuePair<string, string>> output)
        {
            var zero = Array.IndexOf(data, (byte)0);
            if (zero <= 0) return;
            var key = MetadataRows.Latin1.GetString(data, 0, zero);
            var value = MetadataRows.Latin1.GetString(data, zero + 1, data.Length - zero - 1);
            output.Add(new KeyValuePair<string, string>(key, MetadataRows.Truncate(value)));
        }

        private static void ParsePngZtxtChunk(byte[] data, List<KeyValuePair<string, string>> output)
        {
            var zero = Array.IndexOf(data, (byte)0);
            if (zero <= 0 || zero + 2 > data.Length) return;
            var key = MetadataRows.Latin1.GetString(data, 0, zero);
            if (data[zero + 1] != 0) return;
            var compressed = new byte[data.Length - zero - 2];
            Buffer.BlockCopy(data, zero + 2, compressed, 0, compressed.Length);
            var text = InflateText(compressed, MetadataRows.Latin1);
            if (text == null) return;
            output.Add(new KeyValuePair<string, string>(key, MetadataRows.Truncate(text)));
        }

        private static void ParsePngItxtChunk(byte[] data, List<KeyValuePair<string, string>> output)
        {
            var p = 0;
            var keyEnd = IndexOfZero(data, p); if (keyEnd < 0) return;
            var key = Encoding.UTF8.GetString(data, p, keyEnd - p); p = keyEnd + 1;
            if (p + 2 > data.Length) return;
            var compressed = data[p++] != 0;
            var compressionMethod = data[p++];
            var langEnd = IndexOfZero(data, p); if (langEnd < 0) return; p = langEnd + 1;
            var translatedEnd = IndexOfZero(data, p); if (translatedEnd < 0) return; p = translatedEnd + 1;
            string text;
            if (compressed)
            {
                if (compressionMethod != 0) return;
                var comp = new byte[data.Length - p];
                Buffer.BlockCopy(data, p, comp, 0, comp.Length);
                text = InflateText(comp, Encoding.UTF8);
                if (text == null) return;
            }
            else
            {
                text = Encoding.UTF8.GetString(data, p, data.Length - p);
            }
            output.Add(new KeyValuePair<string, string>(key, MetadataRows.Truncate(text)));
        }

        /// <summary>
        /// zlib 스트림을 최대 MaxDecompressedBytes 까지만 해제한다.
        /// 청크 크기만 제한하면 작은 zTXt/iTXt 가 수 GB 로 팽창하는 압축 폭탄에 그대로 노출된다.
        /// </summary>
        private static string InflateText(byte[] zlib, Encoding encoding)
        {
            var raw = ZlibToRaw(zlib);
            if (raw == null || raw.Length == 0) return null;

            using (var input = new MemoryStream(raw))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var ms = new MemoryStream())
            {
                var buffer = new byte[8192];
                var total = 0;
                while (total < MaxDecompressedBytes)
                {
                    var read = deflate.Read(buffer, 0, Math.Min(buffer.Length, MaxDecompressedBytes - total));
                    if (read <= 0) break;
                    ms.Write(buffer, 0, read);
                    total += read;
                }
                return encoding.GetString(ms.ToArray());
            }
        }

        internal static void ReadJpegXmp(string path, List<KeyValuePair<string, string>> output)
        {
            using (var fs = File.OpenRead(path))
            using (var br = new BinaryReader(fs))
            {
                if (br.ReadByte() != 0xFF || br.ReadByte() != 0xD8) return;
                while (fs.Position + 4 <= fs.Length)
                {
                    // Skip fill bytes (0xFF padding per JPEG spec).
                    int prefix = br.ReadByte();
                    if (prefix != 0xFF) break;
                    int marker = br.ReadByte();
                    while (marker == 0xFF && fs.Position < fs.Length)
                        marker = br.ReadByte();
                    if (marker < 0) break;
                    if (marker == 0xDA || marker == 0xD9) break;
                    if (marker == 0xD8 || marker == 0x01) continue;
                    if (fs.Position + 2 > fs.Length) break;
                    var hi = br.ReadByte(); var lo = br.ReadByte();
                    var len = (hi << 8) | lo;
                    if (len < 2 || len - 2 > MaxChunkBytes || fs.Position + len - 2 > fs.Length) break;
                    var data = br.ReadBytes(len - 2);
                    if (data.Length != len - 2) break;
                    if (marker == 0xE1)
                    {
                        var xmpSig = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
                        if (StartsWith(data, xmpSig))
                            output.Add(new KeyValuePair<string, string>("XMP", MetadataRows.Truncate(Encoding.UTF8.GetString(data, xmpSig.Length, data.Length - xmpSig.Length))));
                    }
                    else if (marker == 0xFE)
                    {
                        output.Add(new KeyValuePair<string, string>("Comment", MetadataRows.Truncate(Encoding.UTF8.GetString(data))));
                    }
                }
            }
        }

        internal static void ReadWebpText(string path, List<KeyValuePair<string, string>> output)
        {
            using (var fs = File.OpenRead(path))
            using (var br = new BinaryReader(fs))
            {
                if (fs.Length < 12) return;
                var riff = br.ReadBytes(4);
                var sizeSkip = br.ReadBytes(4);
                var webp = br.ReadBytes(4);
                if (riff.Length != 4 || webp.Length != 4) return;
                if (Encoding.ASCII.GetString(riff) != "RIFF" || Encoding.ASCII.GetString(webp) != "WEBP") return;
                while (fs.Position + 8 <= fs.Length)
                {
                    var typeBytes = br.ReadBytes(4);
                    if (typeBytes.Length != 4) break;
                    var type = Encoding.ASCII.GetString(typeBytes);
                    if (fs.Position + 4 > fs.Length) break;
                    var len = br.ReadUInt32();
                    if (len > MaxChunkBytes || fs.Position + len > fs.Length) break;
                    var data = br.ReadBytes((int)len);
                    if (data.Length != (int)len) break;
                    if (type == "XMP ") output.Add(new KeyValuePair<string, string>("XMP", MetadataRows.Truncate(Encoding.UTF8.GetString(data))));
                    else if (type == "EXIF") { /* TagLib reads EXIF below. */ }
                    if ((len & 1) != 0 && fs.Position < fs.Length) fs.ReadByte();
                }
            }
        }

        private static int IndexOfZero(byte[] data, int start)
        {
            for (var i = start; i < data.Length; i++) if (data[i] == 0) return i;
            return -1;
        }

        private static uint ReadUInt32BE(BinaryReader br)
        {
            var b = br.ReadBytes(4);
            if (b.Length != 4) throw new EndOfStreamException();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        private static bool StartsWith(byte[] data, byte[] prefix)
        {
            if (data.Length < prefix.Length) return false;
            for (var i = 0; i < prefix.Length; i++) if (data[i] != prefix[i]) return false;
            return true;
        }

        private static byte[] ZlibToRaw(byte[] zlib)
        {
            if (zlib == null || zlib.Length < 2) return zlib;
            // zlib (RFC1950): 2-byte header + deflate + 4-byte Adler32.
            // DeflateStream handles raw deflate only, so strip wrapper when present.
            if ((zlib[0] & 0x0F) == 8 && (((zlib[0] << 8) | zlib[1]) % 31 == 0) && zlib.Length > 6)
            {
                var raw = new byte[zlib.Length - 6];
                Buffer.BlockCopy(zlib, 2, raw, 0, raw.Length);
                return raw;
            }
            return zlib;
        }

        private static string FormatExposure(double seconds)
        {
            if (seconds <= 0) return seconds.ToString(CultureInfo.InvariantCulture);
            if (seconds >= 1) return seconds.ToString("0.#", CultureInfo.InvariantCulture) + " s";
            return "1/" + Math.Round(1.0 / seconds).ToString("0", CultureInfo.InvariantCulture) + " s";
        }

        private static string FormatBytes(long bytes)
        {
            var units = new[] { "B", "KB", "MB", "GB", "TB" };
            double n = bytes; var i = 0;
            while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; }
            return n.ToString(i == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[i];
        }
    }

    /// <summary>
    /// Metadata UI displays Name | Value. Clicking a metadata row copies its value.
    /// It is an independent dockable panel, so Details can remain visible at the same time.
    ///
    /// d1: the file is read on a background thread with cancellation, results are cached by path,
    /// the grid shows a truncated value while the clipboard gets the complete one, and copying
    /// uses copy:true so the text survives process exit.
    /// </summary>
    public class MetadataPanel : Panel
    {
        private const int DisplayValueMaxChars = 240;
        private const int MaxCacheEntries = 32;

        private readonly DataGridView dgv;
        private readonly DataGridViewTextBoxColumn colName;
        private readonly DataGridViewTextBoxColumn colValue;
        private readonly Dictionary<string, CachedMetadata> cache = new Dictionary<string, CachedMetadata>(StringComparer.OrdinalIgnoreCase);
        private string currentPath;
        private CancellationTokenSource loadCancellation;

        private sealed class CachedMetadata
        {
            public DateTime LastWriteTimeUtc;
            public List<MetadataRow> Rows;
        }

        public MetadataPanel()
        {
            dgv = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AllowUserToResizeColumns = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCellsExceptHeaders,
                BorderStyle = BorderStyle.None,
                // Excel-style grid: keep clear horizontal/vertical separators between
                // metadata name and value so long AI metadata remains easy to scan.
                // Clicking any metadata row copies its value. The outer panel frame is still borderless.
                CellBorderStyle = DataGridViewCellBorderStyle.Single,
                GridColor = SystemColors.ControlLight,
                BackgroundColor = SystemColors.Window,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText,
            };
            colName = new DataGridViewTextBoxColumn { Name = "MetadataName", HeaderText = Message.Metadata_ColumnName, FillWeight = 27, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true };
            colValue = new DataGridViewTextBoxColumn { Name = "MetadataValue", HeaderText = Message.Metadata_ColumnValue, FillWeight = 73, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true };
            colName.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            colValue.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            dgv.Columns.AddRange(colName, colValue);
            dgv.CellClick += Dgv_CellClick;
            dgv.CellMouseDown += Dgv_CellMouseDown;
            dgv.KeyDown += Dgv_KeyDown;
            var copyMenu = new ContextMenuStrip();
            var copyItem = new ToolStripMenuItem(Message.Metadata_CopyButton ?? "Copy");
            copyItem.Click += (s, e) => CopySelectedValue();
            copyMenu.Items.Add(copyItem);
            dgv.ContextMenuStrip = copyMenu;
            Controls.Add(dgv);
            try { Program.SetDoubleBuffered(dgv); } catch { }
            ShowMessage(Message.Metadata_NoSelection);
        }

        /// <summary>표시할 항목을 지정한다. 읽기는 백그라운드에서 수행된다.</summary>
        public void SetPath(string path)
        {
            path = string.IsNullOrWhiteSpace(path) ? null : path;
            if (string.Equals(currentPath, path, StringComparison.OrdinalIgnoreCase)) return;
            currentPath = path;
            if (Visible) StartLoad();
        }

        /// <summary>현재 항목을 다시 읽는다(캐시 무효화).</summary>
        public void Refresh_Metadata()
        {
            InvalidateCache(currentPath);
            if (Visible) StartLoad();
        }

        private void StartLoad()
        {
            CancelLoad();

            var path = currentPath;
            if (path == null)
            {
                ShowMessage(Message.Metadata_NoSelection);
                return;
            }

            List<MetadataRow> cachedRows;
            if (TryGetCached(path, out cachedRows))
            {
                ShowRows(cachedRows);
                return;
            }

            ShowMessage(Message.Metadata_Loading);

            var cancellation = new CancellationTokenSource();
            loadCancellation = cancellation;
            var token = cancellation.Token;

            Task.Run(() =>
            {
                List<MetadataRow> rows;
                try
                {
                    rows = ImageMetadataReader.Read(path);
                }
                catch (Exception ex)
                {
                    Program.LogException(ex, "MetadataPanel load");
                    rows = new List<MetadataRow> { new MetadataRow(Message.Metadata_NotAvailable, Message.Metadata_ReadError) };
                }

                if (token.IsCancellationRequested) return;

                try
                {
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        if (!string.Equals(currentPath, path, StringComparison.OrdinalIgnoreCase)) return;
                        StoreCache(path, rows);
                        ShowRows(rows);
                    }));
                }
                catch (Exception ex)
                {
                    // 창이 닫히는 중이면 BeginInvoke 가 실패한다.
                    Program.LogException(ex, "MetadataPanel load (marshal)");
                }
            });
        }

        private void CancelLoad()
        {
            var cancellation = loadCancellation;
            loadCancellation = null;
            if (cancellation == null) return;
            // Dispose 하지 않는다. 작업 스레드가 토큰을 조회하는 중이면 ObjectDisposedException 이 난다.
            try { cancellation.Cancel(); } catch (Exception ex) { Program.LogException(ex, "MetadataPanel cancel"); }
        }

        private void ShowRows(List<MetadataRow> rows)
        {
            dgv.Rows.Clear();
            if (rows == null || rows.Count == 0)
            {
                ShowMessage(Message.Metadata_NoMetadataFound);
                return;
            }

            dgv.SuspendLayout();
            try
            {
                foreach (var row in rows)
                {
                    // Hide implementation/workflow metadata that is not useful in the user-facing
                    // Metadata grid. Keep the parser intact so useful AI metadata can still be
                    // extracted from the same source data.
                    if (IsHiddenMetadataName(row.Name, row.Value)) continue;
                    AddRow(row.Name, row.Value);
                }
            }
            finally
            {
                dgv.ResumeLayout();
            }

            if (dgv.Rows.Count == 0) ShowMessage(Message.Metadata_NoMetadataFound);
        }

        private void AddRow(string name, string value)
        {
            var index = dgv.Rows.Add(name, TruncateForDisplay(value));
            // 표시용 값은 줄여도 복사는 원본 전체를 한다.
            dgv.Rows[index].Tag = value;
        }

        private static string TruncateForDisplay(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= DisplayValueMaxChars) return value;
            return value.Substring(0, DisplayValueMaxChars) + " ...";
        }

        private static bool IsHiddenMetadataName(string name, string value = null)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                switch (name.Trim().ToLowerInvariant())
                {
                    case "xml:com.ado":
                    case "nodes":
                    case "links":
                    case "groups":
                    case "config":
                    case "extra":
                    case "extrametadata":
                    case "floatinglinks":
                    case "definitions":
                        return true;
                }
            }

            // Hide raw XMP/XML packets. AI metadata parsed from the packet is still
            // displayed as normal fields (prompt, seed, steps, cfg, etc.).
            string v = value ?? string.Empty;
            if (v.IndexOf("<?xpacket", StringComparison.OrdinalIgnoreCase) >= 0 ||
                v.IndexOf("<x:xmpmeta", StringComparison.OrdinalIgnoreCase) >= 0 ||
                v.IndexOf("<rdf:RDF", StringComparison.OrdinalIgnoreCase) >= 0 ||
                v.IndexOf("http://ns.adobe.com/xap/", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return string.Equals((name ?? string.Empty).Trim(), "XMP", StringComparison.OrdinalIgnoreCase);
        }

        private void ShowMessage(string text)
        {
            dgv.Rows.Clear();
            var index = dgv.Rows.Add(text, "");
            dgv.Rows[index].Tag = null; // 안내 행은 복사 대상이 아니다
        }

        private void Dgv_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            CopyRowValue(e.RowIndex);
        }

        private void Dgv_CellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.RowIndex >= 0 && e.Button == MouseButtons.Right)
            {
                try
                {
                    dgv.ClearSelection();
                    dgv.Rows[e.RowIndex].Selected = true;
                    if (e.ColumnIndex >= 0) dgv.CurrentCell = dgv.Rows[e.RowIndex].Cells[e.ColumnIndex];
                }
                catch (Exception ex) { Program.LogException(ex, "MetadataPanel.CellMouseDown"); }
            }
        }

        private void Dgv_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C)
            {
                CopySelectedValue();
                e.Handled = true;
            }
        }

        private void CopySelectedValue()
        {
            try
            {
                if (dgv.SelectedRows.Count > 0) CopyRowValue(dgv.SelectedRows[0].Index);
                else if (dgv.CurrentRow != null) CopyRowValue(dgv.CurrentRow.Index);
            }
            catch (Exception ex) { Program.LogException(ex, "MetadataPanel.CopySelectedValue"); }
        }

        private void CopyRowValue(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= dgv.Rows.Count) return;

            var row = dgv.Rows[rowIndex];
            var value = row.Tag as string; // 안내 행(Tag == null)은 복사하지 않는다
            if (string.IsNullOrEmpty(value)) return;

            try
            {
                // copy: true 로 설정해야 ZipPla 를 종료한 뒤에도 클립보드 내용이 유지된다.
                Clipboard.SetDataObject(value, true);
            }
            catch (Exception ex)
            {
                Program.LogException(ex, "MetadataPanel.CopyRowValue");
            }
        }

        private bool TryGetCached(string path, out List<MetadataRow> rows)
        {
            rows = null;
            CachedMetadata entry;
            if (!cache.TryGetValue(path, out entry)) return false;
            try
            {
                if (new FileInfo(path).LastWriteTimeUtc != entry.LastWriteTimeUtc)
                {
                    cache.Remove(path);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Program.LogException(ex, "MetadataPanel cache");
                cache.Remove(path);
                return false;
            }
            rows = entry.Rows;
            return true;
        }

        private void StoreCache(string path, List<MetadataRow> rows)
        {
            if (string.IsNullOrEmpty(path) || rows == null) return;
            try
            {
                if (cache.Count >= MaxCacheEntries) cache.Clear();
                cache[path] = new CachedMetadata
                {
                    LastWriteTimeUtc = new FileInfo(path).LastWriteTimeUtc,
                    Rows = rows,
                };
            }
            catch (Exception ex)
            {
                Program.LogException(ex, "MetadataPanel cache");
            }
        }

        private void InvalidateCache(string path)
        {
            if (string.IsNullOrEmpty(path)) cache.Clear();
            else cache.Remove(path);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) StartLoad();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) CancelLoad();
            base.Dispose(disposing);
        }
    }
}
