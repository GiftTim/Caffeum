using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CoffeeViewer;

/// <summary>
/// 한글 문서(.hwp · .hwpx)에서 본문 글만 뽑아 낸다 — 커피뷰어는 이 글을 txt와 같은 읽기 화면으로 보여주고, 고치는 건 한컴 한글에 맡긴다.
/// 한컴오피스가 없어도 읽을 수 있도록 형식을 직접 푼다:
/// - HWPX: zip 안의 Contents/section0.xml … (OWPML). 문단(hp:p)마다 한 줄, 글자(hp:t) · 탭 · 줄바꿈만 살린다.
/// - HWP 5.x: OLE 복합 문서(CFB) 안의 BodyText/Section0 … 스트림(보통 deflate 압축). 레코드 중 문단 글(HWPTAG_PARA_TEXT)만 읽는다.
/// 표 안의 글도 문단으로 이어서 나오고, 그림 · 수식 · 글상자 같은 개체는 건너뛴다. 암호가 걸렸거나 배포용 문서는 읽을 수 없다.
/// </summary>
public static class HwpDocument
{
    public static bool IsHwpExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".hwp" or ".hwpx";

    /// <summary>문단을 \n으로 이은 본문 글.</summary>
    public static string ExtractText(string path) =>
        Path.GetExtension(path).Equals(".hwpx", StringComparison.OrdinalIgnoreCase) ? ExtractHwpx(path) : ExtractHwp(path);

    // ───────────────────────── HWPX ─────────────────────────

    private static string ExtractHwpx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        // 구역(섹션) 파일을 순서대로: section0.xml, section1.xml, … (이름의 숫자 순)
        var sections = zip.Entries
            .Where(e => Regex.IsMatch(e.FullName, @"^Contents[/\\]section\d+\.xml$", RegexOptions.IgnoreCase)) // 일부 압축 도구는 경로를 \로 적는다
            .OrderBy(e => int.Parse(Regex.Match(e.Name, @"\d+").Value))
            .ToList();
        if (sections.Count == 0) throw new InvalidDataException("HWPX 안에서 본문(Contents/section*.xml)을 찾지 못했습니다.");

        var lines = new List<string>();
        foreach (var entry in sections)
        {
            using var s = entry.Open();
            var doc = XDocument.Load(s);
            if (doc.Root != null) CollectHwpxParagraphs(doc.Root, lines);
        }
        return string.Join("\n", lines);
    }

    /// <summary>문단(p)을 만나면 그 문단에 직접 딸린 글(t)만 한 줄로 모은다. 표 칸처럼 문단 안에 든 다른 문단은 따로 한 줄씩.</summary>
    private static void CollectHwpxParagraphs(XElement node, List<string> lines)
    {
        foreach (var child in node.Elements())
        {
            if (child.Name.LocalName == "p")
            {
                var sb = new StringBuilder();
                var nested = new List<string>();
                AppendHwpxRuns(child, sb, nested);
                lines.Add(sb.ToString());
                lines.AddRange(nested);
            }
            else CollectHwpxParagraphs(child, lines);
        }
    }

    private static void AppendHwpxRuns(XElement p, StringBuilder sb, List<string> nested)
    {
        foreach (var child in p.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "t":
                    foreach (var n in child.Nodes())
                    {
                        if (n is XText text) sb.Append(text.Value);
                        else if (n is XElement e)
                        {
                            if (e.Name.LocalName == "tab") sb.Append('\t');
                            else if (e.Name.LocalName == "lineBreak") sb.Append('\n');
                            else if (e.Name.LocalName is "nbSpace" or "fwSpace") sb.Append(' ');
                        }
                    }
                    break;
                case "p": // 표 칸 · 글상자 안의 문단
                    CollectHwpxParagraphs(new XElement("wrap", child), nested);
                    break;
                default:
                    AppendHwpxRuns(child, sb, nested); // run · ctrl · tbl · tc · subList 같은 감싸개는 안으로 들어간다
                    break;
            }
        }
    }

    // ───────────────────────── HWP 5.x ─────────────────────────

    private const int TagParaHeader = 0x10 + 50; // HWPTAG_PARA_HEADER
    private const int TagParaText = 0x10 + 51;   // HWPTAG_PARA_TEXT

    private static string ExtractHwp(string path)
    {
        var cfb = new CompoundFile(File.ReadAllBytes(path));
        byte[] header = cfb.ReadStream("FileHeader") ?? throw new InvalidDataException("한글(HWP) 문서가 아닌 것 같습니다(FileHeader 없음).");
        if (header.Length < 40 || !Encoding.ASCII.GetString(header, 0, 17).StartsWith("HWP Document File"))
            throw new InvalidDataException("HWP 5.0 형식이 아닙니다. 아주 예전(3.0 이하) 한글 문서는 한컴 한글에서 새 형식으로 저장한 뒤 열어 주세요.");
        uint flags = BitConverter.ToUInt32(header, 36);
        bool compressed = (flags & 1) != 0;
        if ((flags & 2) != 0) throw new InvalidDataException("암호가 걸린 한글 문서는 읽을 수 없습니다.");
        if ((flags & 4) != 0) throw new InvalidDataException("배포용 한글 문서는 내용이 잠겨 있어 읽을 수 없습니다.");

        var lines = new List<string>();
        for (int i = 0; ; i++)
        {
            byte[]? raw = cfb.ReadStream($"BodyText/Section{i}");
            if (raw == null) break;
            byte[] data = compressed ? Inflate(raw) : raw;
            ReadHwpSection(data, lines);
        }
        if (lines.Count == 0) throw new InvalidDataException("한글 문서에서 본문(BodyText)을 찾지 못했습니다.");
        return string.Join("\n", lines);
    }

    private static byte[] Inflate(byte[] raw)
    {
        using var input = new MemoryStream(raw);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress); // 한글은 zlib 머리 없는 raw deflate
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>레코드: 머리 4바이트(태그 10비트 · 깊이 10비트 · 크기 12비트, 크기가 0xFFF면 뒤 4바이트가 진짜 크기) + 내용.
    /// 문단 머리(PARA_HEADER)마다 한 줄을 만들고, 바로 뒤의 PARA_TEXT가 있으면 그 글로 채운다(없으면 빈 줄).</summary>
    private static void ReadHwpSection(byte[] data, List<string> lines)
    {
        int pos = 0;
        int? pendingLine = null;
        while (pos + 4 <= data.Length)
        {
            uint h = BitConverter.ToUInt32(data, pos);
            pos += 4;
            int tag = (int)(h & 0x3FF);
            int size = (int)(h >> 20);
            if (size == 0xFFF)
            {
                if (pos + 4 > data.Length) break;
                size = BitConverter.ToInt32(data, pos);
                pos += 4;
            }
            if (size < 0 || pos + size > data.Length) break;

            if (tag == TagParaHeader)
            {
                lines.Add("");
                pendingLine = lines.Count - 1;
            }
            else if (tag == TagParaText && pendingLine is int line)
            {
                lines[line] = DecodeParaText(data, pos, size);
                pendingLine = null;
            }
            pos += size;
        }
    }

    /// <summary>
    /// 문단 글은 UTF-16. 32 미만의 글자는 조판 부호다: 0 · 10(줄바꿈) · 13(문단 끝) · 24~31(하이픈 · 묶음 빈칸 등)은 한 글자짜리,
    /// 나머지(탭 9 포함 — 표 · 그림 · 각주 같은 개체 자리)는 8글자(16바이트)를 차지하므로 통째로 건너뛴다.
    /// </summary>
    private static string DecodeParaText(byte[] data, int start, int size)
    {
        var sb = new StringBuilder(size / 2);
        int end = start + size;
        int i = start;
        while (i + 1 < end)
        {
            char c = (char)BitConverter.ToUInt16(data, i);
            if (c >= 32) { sb.Append(c); i += 2; continue; }
            switch (c)
            {
                case (char)10: sb.Append('\n'); i += 2; break;
                case (char)13: case (char)0: i += 2; break;
                case (char)24: sb.Append('-'); i += 2; break;
                case >= (char)25 and <= (char)31: sb.Append(' '); i += 2; break;
                case (char)9: sb.Append('\t'); i += 16; break;
                default: i += 16; break; // 개체 · 필드 · 구역 정의 같은 확장 조판 부호
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 아주 작은 OLE 복합 문서(Compound File Binary) 읽개. HWP는 이 형식 안에 FileHeader · BodyText/SectionN 같은 스트림을 담는다.
    /// 읽기만 하고, 경로("BodyText/Section0")로 스트림 내용을 돌려준다.
    /// </summary>
    private sealed class CompoundFile
    {
        private readonly byte[] file;
        private readonly int sectorSize, miniSectorSize;
        private readonly uint miniCutoff;
        private readonly List<uint> fat = new();
        private readonly List<uint> miniFat = new();
        private readonly List<Entry> entries = new();
        private byte[]? miniStream;

        private sealed record Entry(string Name, int Type, int Left, int Right, int Child, uint Start, long Size);

        private const uint EndOfChain = 0xFFFFFFFE, FreeSect = 0xFFFFFFFF;

        public CompoundFile(byte[] bytes)
        {
            file = bytes;
            if (bytes.Length < 512 || BitConverter.ToUInt64(bytes, 0) != 0xE11AB1A1E011CFD0)
                throw new InvalidDataException("한글(HWP) 문서가 아닌 것 같습니다(복합 문서 머리가 없음).");
            sectorSize = 1 << BitConverter.ToUInt16(bytes, 0x1E);
            miniSectorSize = 1 << BitConverter.ToUInt16(bytes, 0x20);
            uint firstDir = BitConverter.ToUInt32(bytes, 0x30);
            miniCutoff = BitConverter.ToUInt32(bytes, 0x38);
            uint firstMiniFat = BitConverter.ToUInt32(bytes, 0x3C);
            uint firstDifat = BitConverter.ToUInt32(bytes, 0x44);
            uint difatCount = BitConverter.ToUInt32(bytes, 0x48);

            // FAT 섹터 번호들: 머리의 109칸 + DIFAT 섹터 사슬
            var fatSectors = new List<uint>();
            for (int i = 0; i < 109; i++)
            {
                uint s = BitConverter.ToUInt32(bytes, 0x4C + i * 4);
                if (s != FreeSect) fatSectors.Add(s);
            }
            uint d = firstDifat;
            for (int n = 0; n < difatCount && d < EndOfChain; n++)
            {
                int off = SectorOffset(d);
                int per = sectorSize / 4 - 1;
                for (int i = 0; i < per; i++)
                {
                    uint s = BitConverter.ToUInt32(bytes, off + i * 4);
                    if (s != FreeSect) fatSectors.Add(s);
                }
                d = BitConverter.ToUInt32(bytes, off + per * 4);
            }
            foreach (uint s in fatSectors)
            {
                int off = SectorOffset(s);
                for (int i = 0; i < sectorSize / 4 && off + i * 4 + 4 <= bytes.Length; i++) fat.Add(BitConverter.ToUInt32(bytes, off + i * 4));
            }

            byte[] dir = ReadChain(firstDir, long.MaxValue);
            for (int off = 0; off + 128 <= dir.Length; off += 128)
            {
                int nameLen = BitConverter.ToUInt16(dir, off + 64);
                string name = nameLen >= 2 ? Encoding.Unicode.GetString(dir, off, Math.Min(64, nameLen) - 2) : "";
                entries.Add(new Entry(name, dir[off + 66], BitConverter.ToInt32(dir, off + 68), BitConverter.ToInt32(dir, off + 72),
                    BitConverter.ToInt32(dir, off + 76), BitConverter.ToUInt32(dir, off + 116), (long)BitConverter.ToUInt32(dir, off + 120)));
            }

            if (firstMiniFat < EndOfChain)
            {
                byte[] mf = ReadChain(firstMiniFat, long.MaxValue);
                for (int i = 0; i + 4 <= mf.Length; i += 4) miniFat.Add(BitConverter.ToUInt32(mf, i));
            }
        }

        private int SectorOffset(uint sector) => (int)((sector + 1) * (long)sectorSize);

        private byte[] ReadChain(uint start, long size)
        {
            using var ms = new MemoryStream();
            uint s = start;
            int guard = 0;
            while (s < EndOfChain && guard++ < 1_000_000)
            {
                int off = SectorOffset(s);
                if (off >= file.Length) break;
                ms.Write(file, off, Math.Min(sectorSize, file.Length - off));
                if (ms.Length >= size) break;
                s = s < fat.Count ? fat[(int)s] : EndOfChain;
            }
            var result = ms.ToArray();
            return size < result.Length ? result[..(int)size] : result;
        }

        private byte[] ReadMiniChain(uint start, long size)
        {
            miniStream ??= ReadChain(entries[0].Start, entries[0].Size); // 0번(루트) 항목의 스트림이 작은 스트림들을 담는 그릇
            using var ms = new MemoryStream();
            uint s = start;
            int guard = 0;
            while (s < EndOfChain && ms.Length < size && guard++ < 1_000_000)
            {
                long off = (long)s * miniSectorSize;
                if (off >= miniStream.Length) break;
                ms.Write(miniStream, (int)off, (int)Math.Min(miniSectorSize, miniStream.Length - off));
                s = s < miniFat.Count ? miniFat[(int)s] : EndOfChain;
            }
            var result = ms.ToArray();
            return size < result.Length ? result[..(int)size] : result;
        }

        /// <summary>"BodyText/Section0"처럼 '/'로 이은 경로의 스트림 내용. 없으면 null.</summary>
        public byte[]? ReadStream(string path)
        {
            int current = 0; // 루트
            foreach (var part in path.Split('/'))
            {
                int found = FindChild(entries[current].Child, part);
                if (found < 0) return null;
                current = found;
            }
            var e = entries[current];
            if (e.Type != 2) return null; // 스트림이 아니다
            return e.Size < miniCutoff ? ReadMiniChain(e.Start, e.Size) : ReadChain(e.Start, e.Size);
        }

        /// <summary>한 저장소(폴더)의 자식들은 이름 순 이진 트리로 묶여 있다 — 왼쪽 · 오른쪽 형제를 따라가며 이름을 찾는다.</summary>
        private int FindChild(int node, string name)
        {
            var stack = new Stack<int>();
            if (node >= 0) stack.Push(node);
            int guard = 0;
            while (stack.Count > 0 && guard++ < 100_000)
            {
                int i = stack.Pop();
                if (i < 0 || i >= entries.Count) continue;
                var e = entries[i];
                if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) return i;
                if (e.Left >= 0) stack.Push(e.Left);
                if (e.Right >= 0) stack.Push(e.Right);
            }
            return -1;
        }
    }
}
