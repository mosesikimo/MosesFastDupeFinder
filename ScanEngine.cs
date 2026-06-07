using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices; // 💡 引入底層硬體指標操控
using System.Runtime.InteropServices; // 💡 引入記憶體直接存取

namespace FastDupeFinder
{
    public static class SimilarityEngine
    {
        private static (int Score, double Sec1, double Sec2, FingerprintPosition Pos1, FingerprintPosition Pos2) CompareInternal(
            DupeFileItem f1, DupeFileItem f2, FingerprintPosition requiredPosition)
        {
            VideoFingerprint? fp1_head = null, fp1_tail = null;
            VideoFingerprint? fp2_head = null, fp2_tail = null;

            foreach (var fp in f1.Fingerprints)
            {
                if (fp.Position == FingerprintPosition.Head) fp1_head = fp;
                else if (fp.Position == FingerprintPosition.Tail) fp1_tail = fp;
            }

            foreach (var fp in f2.Fingerprints)
            {
                if (fp.Position == FingerprintPosition.Head) fp2_head = fp;
                else if (fp.Position == FingerprintPosition.Tail) fp2_tail = fp;
            }

            if (requiredPosition == FingerprintPosition.Head && fp1_head != null && fp2_head != null)
            {
                // 💡 硬體級優化：直接取得記憶體指標，徹底消除 C# 陣列邊界檢查 (Bounds Checking)
                ref ulong p1 = ref MemoryMarshal.GetArrayDataReference(fp1_head.Hashes[0]);
                for (int v = 0; v < 8; v++)
                {
                    ref ulong p2 = ref MemoryMarshal.GetArrayDataReference(fp2_head.Hashes[v]);
                    int diff = BitOperations.PopCount(p1 ^ p2)
                             + BitOperations.PopCount(Unsafe.Add(ref p1, 1) ^ Unsafe.Add(ref p2, 1))
                             + BitOperations.PopCount(Unsafe.Add(ref p1, 2) ^ Unsafe.Add(ref p2, 2))
                             + BitOperations.PopCount(Unsafe.Add(ref p1, 3) ^ Unsafe.Add(ref p2, 3));
                    if (diff <= 3)
                        return (100, fp1_head.TimeSec, fp2_head.TimeSec, FingerprintPosition.Head, FingerprintPosition.Head);
                }
            }
            else if (requiredPosition == FingerprintPosition.Tail && fp1_tail != null && fp2_tail != null)
            {
                ref ulong p1 = ref MemoryMarshal.GetArrayDataReference(fp1_tail.Hashes[0]);
                for (int v = 0; v < 8; v++)
                {
                    ref ulong p2 = ref MemoryMarshal.GetArrayDataReference(fp2_tail.Hashes[v]);
                    int diff = BitOperations.PopCount(p1 ^ p2)
                             + BitOperations.PopCount(Unsafe.Add(ref p1, 1) ^ Unsafe.Add(ref p2, 1))
                             + BitOperations.PopCount(Unsafe.Add(ref p1, 2) ^ Unsafe.Add(ref p2, 2))
                             + BitOperations.PopCount(Unsafe.Add(ref p1, 3) ^ Unsafe.Add(ref p2, 3));
                    if (diff <= 3)
                        return (100, fp1_tail.TimeSec, fp2_tail.TimeSec, FingerprintPosition.Tail, FingerprintPosition.Tail);
                }
            }
            else if (requiredPosition == FingerprintPosition.Unknown)
            {
                if (fp1_head != null && fp2_head != null)
                {
                    ref ulong p1 = ref MemoryMarshal.GetArrayDataReference(fp1_head.Hashes[0]);
                    for (int v = 0; v < 8; v++)
                    {
                        ref ulong p2 = ref MemoryMarshal.GetArrayDataReference(fp2_head.Hashes[v]);
                        int diff = BitOperations.PopCount(p1 ^ p2)
                                 + BitOperations.PopCount(Unsafe.Add(ref p1, 1) ^ Unsafe.Add(ref p2, 1))
                                 + BitOperations.PopCount(Unsafe.Add(ref p1, 2) ^ Unsafe.Add(ref p2, 2))
                                 + BitOperations.PopCount(Unsafe.Add(ref p1, 3) ^ Unsafe.Add(ref p2, 3));
                        if (diff <= 3)
                            return (100, fp1_head.TimeSec, fp2_head.TimeSec, FingerprintPosition.Head, FingerprintPosition.Head);
                    }
                }

                if (fp1_tail != null && fp2_tail != null)
                {
                    ref ulong p1 = ref MemoryMarshal.GetArrayDataReference(fp1_tail.Hashes[0]);
                    for (int v = 0; v < 8; v++)
                    {
                        ref ulong p2 = ref MemoryMarshal.GetArrayDataReference(fp2_tail.Hashes[v]);
                        int diff = BitOperations.PopCount(p1 ^ p2)
                                 + BitOperations.PopCount(Unsafe.Add(ref p1, 1) ^ Unsafe.Add(ref p2, 1))
                                 + BitOperations.PopCount(Unsafe.Add(ref p1, 2) ^ Unsafe.Add(ref p2, 2))
                                 + BitOperations.PopCount(Unsafe.Add(ref p1, 3) ^ Unsafe.Add(ref p2, 3));
                        if (diff <= 3)
                            return (100, fp1_tail.TimeSec, fp2_tail.TimeSec, FingerprintPosition.Tail, FingerprintPosition.Tail);
                    }
                }
            }

            return (0, -1, -1, FingerprintPosition.Unknown, FingerprintPosition.Unknown);
        }

        public static (int Score, double Sec1, double Sec2, FingerprintPosition Pos1, FingerprintPosition Pos2) Compare(
            DupeFileItem f1, DupeFileItem f2, FingerprintPosition requiredPosition = FingerprintPosition.Unknown)
        {
            return CompareInternal(f1, f2, requiredPosition);
        }

        public static bool IsDeepMatch(DupeFileItem f1, DupeFileItem f2)
        {
            VideoFingerprint? fp1_deep = null, fp2_deep = null;
            foreach (var f in f1.Fingerprints) if (f.Position == FingerprintPosition.Deep) { fp1_deep = f; break; }
            foreach (var f in f2.Fingerprints) if (f.Position == FingerprintPosition.Deep) { fp2_deep = f; break; }

            if (fp1_deep == null || fp2_deep == null) return true;

            // 💡 硬體級優化
            ref ulong p1 = ref MemoryMarshal.GetArrayDataReference(fp1_deep.Hashes[0]);
            for (int v = 0; v < 8; v++)
            {
                ref ulong p2 = ref MemoryMarshal.GetArrayDataReference(fp2_deep.Hashes[v]);
                int diff = BitOperations.PopCount(p1 ^ p2)
                         + BitOperations.PopCount(Unsafe.Add(ref p1, 1) ^ Unsafe.Add(ref p2, 1))
                         + BitOperations.PopCount(Unsafe.Add(ref p1, 2) ^ Unsafe.Add(ref p2, 2))
                         + BitOperations.PopCount(Unsafe.Add(ref p1, 3) ^ Unsafe.Add(ref p2, 3));
                
                if (diff <= 3) return true;
            }
            return false;
        }

        public static (int Score, double Sec1, double Sec2, FingerprintPosition Pos1, FingerprintPosition Pos2) CompareDeep(
            DupeFileItem f1, DupeFileItem f2)
        {
            VideoFingerprint? fp1_deep = null, fp2_deep = null;
            foreach (var f in f1.Fingerprints) if (f.Position == FingerprintPosition.Deep) { fp1_deep = f; break; }
            foreach (var f in f2.Fingerprints) if (f.Position == FingerprintPosition.Deep) { fp2_deep = f; break; }

            if (fp1_deep == null || fp2_deep == null) return (0, -1, -1, FingerprintPosition.Unknown, FingerprintPosition.Unknown);

            // 💡 硬體級優化
            ref ulong p1 = ref MemoryMarshal.GetArrayDataReference(fp1_deep.Hashes[0]);
            for (int v = 0; v < 8; v++)
            {
                ref ulong p2 = ref MemoryMarshal.GetArrayDataReference(fp2_deep.Hashes[v]);
                int diff = BitOperations.PopCount(p1 ^ p2)
                         + BitOperations.PopCount(Unsafe.Add(ref p1, 1) ^ Unsafe.Add(ref p2, 1))
                         + BitOperations.PopCount(Unsafe.Add(ref p1, 2) ^ Unsafe.Add(ref p2, 2))
                         + BitOperations.PopCount(Unsafe.Add(ref p1, 3) ^ Unsafe.Add(ref p2, 3));
                
                if (diff <= 3)
                    return (100, fp1_deep.TimeSec, fp2_deep.TimeSec, FingerprintPosition.Deep, FingerprintPosition.Deep);
            }
            return (0, -1, -1, FingerprintPosition.Unknown, FingerprintPosition.Unknown);
        }
    }

    public static class ScanEngine
    {
        public static List<List<DupeFileItem>> GroupFilesByPredicate(List<DupeFileItem> files, Func<DupeFileItem, DupeFileItem, bool> isMatch)
        {
            int n = files.Count;
            if (n == 0) return new List<List<DupeFileItem>>();

            var parent = new int[n];
            var rank = new int[n];
            for (int i = 0; i < n; i++) { parent[i] = i; rank[i] = 0; }
            
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int x, int y) {
                int rx = Find(x), ry = Find(y);
                if (rx == ry) return;
                if (rank[rx] < rank[ry]) parent[rx] = ry;
                else if (rank[rx] > rank[ry]) parent[ry] = rx;
                else { parent[ry] = rx; rank[rx]++; }
            }

            for (int i = 0; i < n; i++)
            {
                if (files[i].Fingerprints.Count == 0) continue;
                for (int j = i + 1; j < n; j++)
                {
                    if (files[j].Fingerprints.Count == 0) continue;
                    if (isMatch(files[i], files[j])) Union(i, j);
                }
            }

            var groupMap = new Dictionary<int, List<DupeFileItem>>();
            for (int i = 0; i < n; i++)
            {
                int root = Find(i);
                if (!groupMap.ContainsKey(root)) groupMap[root] = new List<DupeFileItem>();
                groupMap[root].Add(files[i]);
            }

            return groupMap.Values.Where(g => g.Count >= 2).ToList();
        }

        public static List<List<DupeFileItem>> GroupFiles(List<DupeFileItem> files)
        {
            return GroupFilesByPredicate(files, (f1, f2) => 
                SimilarityEngine.Compare(f1, f2, FingerprintPosition.Unknown).Score == 100);
        }

        public static List<List<DupeFileItem>> GroupLongsByDeepFeature(List<DupeFileItem> longs)
        {
            return GroupFilesByPredicate(longs, SimilarityEngine.IsDeepMatch);
        }

        public static List<DupeFileItem> AssignGroupMetadata(List<DupeFileItem> group, int displayGroupId, bool isPhase2 = false)
        {
            Func<DupeFileItem, DupeFileItem, (int Score, double Sec1, double Sec2, FingerprintPosition Pos1, FingerprintPosition Pos2)> compareFunc = 
                isPhase2 ? SimilarityEngine.CompareDeep : (f1, f2) => SimilarityEngine.Compare(f1, f2, FingerprintPosition.Unknown);

            DupeFileItem? reference = group.FirstOrDefault(f =>
                group.Any(other => other != f && compareFunc(f, other).Score == 100));

            reference ??= group.First();

            reference.IsReference = true;
            reference.SimilarityScore = 100;
            reference.MatchSeconds = -1;
            reference.MatchDetail = "";

            var sorted = group.OrderByDescending(x => x.IsReference).ThenBy(x => x.FilePath).ToList();

            foreach (var item in sorted)
            {
                item.OriginalGroupId = displayGroupId;
                if (item == reference) continue;

                item.IsReference = false;
                item.SimilarityScore = 100;

                var match = compareFunc(reference, item);
                if (match.Score == 100)
                {
                    if (match.Pos1 == FingerprintPosition.Head && reference.HeadMatchTime < 0) reference.HeadMatchTime = match.Sec1;
                    if (match.Pos1 == FingerprintPosition.Tail && reference.TailMatchTime < 0) reference.TailMatchTime = match.Sec1;

                    item.MatchSeconds = match.Sec2;
                    item.MatchDetail = match.Pos1 == FingerprintPosition.Head ? 
                        I18nManager.Instance.GetString("TxtHeadMatch", "頭部 {0}秒").Replace("{0}", ((int)match.Sec1).ToString()) :
                        (match.Pos1 == FingerprintPosition.Deep ? 
                        I18nManager.Instance.GetString("TxtDeepMatch", "深度特徵 {0}秒").Replace("{0}", ((int)match.Sec1).ToString()) :
                        I18nManager.Instance.GetString("TxtTailMatch", "尾部 {0}秒").Replace("{0}", ((int)match.Sec1).ToString()));
                }
                else if (isPhase2)
                {
                    var fbMatch = SimilarityEngine.Compare(reference, item, FingerprintPosition.Unknown);
                    if (fbMatch.Score == 100)
                    {
                        item.MatchSeconds = fbMatch.Sec2;
                        item.MatchDetail = fbMatch.Pos1 == FingerprintPosition.Head ? 
                            I18nManager.Instance.GetString("TxtHeadMatch", "頭部 {0}秒").Replace("{0}", ((int)fbMatch.Sec1).ToString()) :
                            I18nManager.Instance.GetString("TxtTailMatch", "尾部 {0}秒").Replace("{0}", ((int)fbMatch.Sec1).ToString());
                    }
                }
            }

            return sorted;
        }

        public static bool CalculateCombinedHashVariantsFromMemory(byte[] imageData, out List<ulong[]>? headVariants, out List<ulong[]>? tailVariants)
        {
            headVariants = null; tailVariants = null;
            if (imageData == null || imageData.Length != 512) return false;

            Span<int> leftGrays = stackalloc int[256];
            Span<int> rightGrays = stackalloc int[256];
            int leftTotal = 0, rightTotal = 0;

            for (int y = 0; y < 16; y++)
            {
                int rowStart = y * 32;
                for (int x = 0; x < 16; x++)
                {
                    byte grayLeft = imageData[rowStart + x], grayRight = imageData[rowStart + 16 + x];
                    int idx = y * 16 + x;
                    leftGrays[idx] = grayLeft; rightGrays[idx] = grayRight;
                    leftTotal += grayLeft; rightTotal += grayRight;
                }
            }

            headVariants = new List<ulong[]>(8);
            Generate16x16VariantsInline(leftGrays, leftTotal / 256, headVariants);
            
            tailVariants = new List<ulong[]>(8);
            Generate16x16VariantsInline(rightGrays, rightTotal / 256, tailVariants);
            return true;
        }

        public static bool CalculateSingleHashVariantsFromMemory(byte[] imageData, out List<ulong[]>? variants)
        {
            variants = null;
            if (imageData == null || imageData.Length != 256) return false;

            Span<int> grays = stackalloc int[256];
            int total = 0;
            for (int i = 0; i < 256; i++) { grays[i] = imageData[i]; total += imageData[i]; }

            variants = new List<ulong[]>(8);
            Generate16x16VariantsInline(grays, total / 256, variants);
            return true;
        }

        private static void Generate16x16VariantsInline(ReadOnlySpan<int> pixels, int avg, List<ulong[]> list)
        {
            for (int v = 0; v < 8; v++)
            {
                ulong[] h = new ulong[4]; 
                for (int y = 0; y < 16; y++)
                {
                    for (int x = 0; x < 16; x++)
                    {
                        int srcX = x, srcY = y;
                        switch (v)
                        {
                            case 1: srcX = 15 - x; break;
                            case 2: srcY = 15 - y; break;
                            case 3: srcX = 15 - x; srcY = 15 - y; break;
                            case 4: srcX = y; srcY = x; break;
                            case 5: srcX = y; srcY = 15 - x; break;
                            case 6: srcX = 15 - y; srcY = x; break;
                            case 7: srcX = 15 - y; srcY = 15 - x; break;
                        }
                        int idx = y * 16 + x;
                        if (pixels[srcY * 16 + srcX] >= avg)
                            h[idx / 64] |= (1UL << (63 - (idx % 64)));
                    }
                }
                list.Add(h);
            }
        }
    }
}