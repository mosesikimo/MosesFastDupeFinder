using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace FastDupeFinder
{
    public static class SimilarityEngine
    {
        // 第一階段的基礎比對：只需 100秒 或 -100秒 的其中一個變體吻合即可
        public static (int Score, double Sec1, double Sec2, FingerprintPosition Pos1, FingerprintPosition Pos2) Compare(
            DupeFileItem f1, DupeFileItem f2, FingerprintPosition requiredPosition = FingerprintPosition.Unknown)
        {
            var fps1 = f1.Fingerprints.Where(f => requiredPosition == FingerprintPosition.Unknown || f.Position == requiredPosition);
            var fps2 = f2.Fingerprints.Where(f => requiredPosition == FingerprintPosition.Unknown || f.Position == requiredPosition);

            foreach (var fp1 in fps1)
            {
                foreach (var fp2 in fps2)
                {
                    if (fp1.Position != fp2.Position) continue;

                    for (int v = 0; v < 8; v++)
                    {
                        if (BitOperations.PopCount(fp1.Hashes[0] ^ fp2.Hashes[v]) == 0)
                        {
                            return (100, fp1.TimeSec, fp2.TimeSec, fp1.Position, fp2.Position);
                        }
                    }
                }
            }
            return (0, -1, -1, FingerprintPosition.Unknown, FingerprintPosition.Unknown);
        }

        // 💡 判斷兩部「長片」是否在深度特徵 (350 或 -350) 上吻合
        public static bool IsDeepMatch(DupeFileItem f1, DupeFileItem f2)
        {
            var deepFp1 = f1.Fingerprints.Where(f => Math.Abs(f.TimeSec - 350) < 5 || Math.Abs((f1.Duration.TotalSeconds - f.TimeSec) - 350) < 5).ToList();
            var deepFp2 = f2.Fingerprints.Where(f => Math.Abs(f.TimeSec - 350) < 5 || Math.Abs((f2.Duration.TotalSeconds - f.TimeSec) - 350) < 5).ToList();

            if (deepFp1.Count == 0 || deepFp2.Count == 0) return true; // 若未含深度特徵則不視為衝突

            foreach (var fp1 in deepFp1)
            {
                foreach (var fp2 in deepFp2)
                {
                    if (fp1.Position != fp2.Position) continue;
                    
                    for (int v = 0; v < 8; v++)
                    {
                        if (BitOperations.PopCount(fp1.Hashes[0] ^ fp2.Hashes[v]) == 0) return true; 
                    }
                }
            }
            return false; // 完全沒有深層吻合 -> 不同集數，踢出/分離
        }

        // 💡 嚴格深度比對：找出 350 或 -350 的特徵，如果有任何一個深度特徵吻合即判定相同
        public static (int Score, double Sec1, double Sec2, FingerprintPosition Pos1, FingerprintPosition Pos2) CompareDeep(
            DupeFileItem f1, DupeFileItem f2)
        {
            var deepFp1 = f1.Fingerprints.Where(f => Math.Abs(f.TimeSec - 350) < 5 || Math.Abs((f1.Duration.TotalSeconds - f.TimeSec) - 350) < 5).ToList();
            var deepFp2 = f2.Fingerprints.Where(f => Math.Abs(f.TimeSec - 350) < 5 || Math.Abs((f2.Duration.TotalSeconds - f.TimeSec) - 350) < 5).ToList();

            if (deepFp1.Count == 0 || deepFp2.Count == 0) return (0, -1, -1, FingerprintPosition.Unknown, FingerprintPosition.Unknown);

            foreach (var fp1 in deepFp1)
            {
                foreach (var fp2 in deepFp2)
                {
                    if (fp1.Position != fp2.Position) continue;
                    
                    for (int v = 0; v < 8; v++)
                    {
                        if (BitOperations.PopCount(fp1.Hashes[0] ^ fp2.Hashes[v]) == 0)
                        {
                            return (100, fp1.TimeSec, fp2.TimeSec, fp1.Position, fp2.Position);
                        }
                    }
                }
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
                SimilarityEngine.Compare(f1, f2, FingerprintPosition.Head).Score == 100 || 
                SimilarityEngine.Compare(f1, f2, FingerprintPosition.Tail).Score == 100);
        }

        // 💡 補回 missing 的深度群組分離函數
        public static List<List<DupeFileItem>> GroupLongsByDeepFeature(List<DupeFileItem> longs)
        {
            return GroupFilesByPredicate(longs, SimilarityEngine.IsDeepMatch);
        }

        public static List<DupeFileItem> AssignGroupMetadata(List<DupeFileItem> group, int displayGroupId, bool isPhase2)
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
                        I18nManager.Instance.GetString("TxtTailMatch", "尾部 {0}秒").Replace("{0}", ((int)match.Sec1).ToString());
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

        // --- 8x8 Feature Calculation (純記憶體轉換) ---
        public static bool CalculateCombinedHashVariantsFromMemory(byte[] imageData, out List<ulong>? headVariants, out List<ulong>? tailVariants)
        {
            headVariants = null; tailVariants = null;
            if (imageData == null || imageData.Length != 128) return false;

            int[] leftGrays = new int[64], rightGrays = new int[64];
            int leftTotal = 0, rightTotal = 0;

            for (int y = 0; y < 8; y++)
            {
                int rowStart = y * 16;
                for (int x = 0; x < 8; x++)
                {
                    byte grayLeft = imageData[rowStart + x], grayRight = imageData[rowStart + 8 + x];
                    int idx = y * 8 + x;
                    leftGrays[idx] = grayLeft; rightGrays[idx] = grayRight;
                    leftTotal += grayLeft; rightTotal += grayRight;
                }
            }

            headVariants = new List<ulong>(8);
            Generate8x8VariantsInline(leftGrays, leftTotal / 64, headVariants);
            
            tailVariants = new List<ulong>(8);
            Generate8x8VariantsInline(rightGrays, rightTotal / 64, tailVariants);
            return true;
        }

        public static bool CalculateSingleHashVariantsFromMemory(byte[] imageData, out List<ulong>? variants)
        {
            variants = null;
            if (imageData == null || imageData.Length != 64) return false;

            int[] grays = new int[64];
            int total = 0;
            for (int i = 0; i < 64; i++) { grays[i] = imageData[i]; total += imageData[i]; }

            variants = new List<ulong>(8);
            Generate8x8VariantsInline(grays, total / 64, variants);
            return true;
        }

        private static void Generate8x8VariantsInline(int[] pixels, int avg, List<ulong> list)
        {
            for (int v = 0; v < 8; v++)
            {
                ulong h = 0;
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        int srcX = x, srcY = y;
                        switch (v)
                        {
                            case 1: srcX = 7 - x; break;
                            case 2: srcY = 7 - y; break;
                            case 3: srcX = 7 - x; srcY = 7 - y; break;
                            case 4: srcX = y; srcY = x; break;
                            case 5: srcX = y; srcY = 7 - x; break;
                            case 6: srcX = 7 - y; srcY = x; break;
                            case 7: srcX = 7 - y; srcY = 7 - x; break;
                        }
                        if (pixels[srcY * 8 + srcX] >= avg) h |= (1UL << (63 - (y * 8 + x)));
                    }
                }
                list.Add(h);
            }
        }
    }
}