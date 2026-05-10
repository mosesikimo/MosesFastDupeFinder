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
    }

    public static class ScanEngine
    {
        // 💡 [重構核心] 統一聯合尋找演算法 (Union-Find) 共用底層邏輯
        private static List<List<DupeFileItem>> GroupFilesByPredicate(List<DupeFileItem> files, Func<DupeFileItem, DupeFileItem, bool> isMatch)
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

        public static List<List<DupeFileItem>> GroupLongsByDeepFeature(List<DupeFileItem> longs)
        {
            return GroupFilesByPredicate(longs, SimilarityEngine.IsDeepMatch);
        }

        public static List<DupeFileItem> AssignGroupMetadata(List<DupeFileItem> group, int displayGroupId)
        {
            DupeFileItem? reference = group.FirstOrDefault(f =>
                f.Fingerprints.Any(fp => fp.Position == FingerprintPosition.Head) &&
                group.Any(other => other != f && SimilarityEngine.Compare(f, other, FingerprintPosition.Head).Score == 100));

            if (reference == null)
            {
                reference = group.FirstOrDefault(f =>
                    f.Fingerprints.Any(fp => fp.Position == FingerprintPosition.Tail) &&
                    group.Any(other => other != f && SimilarityEngine.Compare(f, other, FingerprintPosition.Tail).Score == 100));
            }

            reference ??= group.First();

            reference.IsReference = true;
            reference.SimilarityScore = 100;
            reference.MatchSeconds = -1;
            reference.MatchDetail = "";

            foreach (var other in group)
            {
                if (other == reference) continue;

                var headMatch = SimilarityEngine.Compare(reference, other, FingerprintPosition.Head);
                if (headMatch.Score == 100 && reference.HeadMatchTime < 0)
                    reference.HeadMatchTime = headMatch.Sec1;

                var tailMatch = SimilarityEngine.Compare(reference, other, FingerprintPosition.Tail);
                if (tailMatch.Score == 100 && reference.TailMatchTime < 0)
                    reference.TailMatchTime = tailMatch.Sec1;
            }

            var sorted = group
                .OrderByDescending(x => x.IsReference)
                .ThenBy(x => x.FilePath)
                .ToList();

            foreach (var item in sorted)
            {
                item.OriginalGroupId = displayGroupId;
                if (item == reference) continue;

                item.IsReference = false;
                item.SimilarityScore = 100;

                var headMatch = SimilarityEngine.Compare(reference, item, FingerprintPosition.Head);
                if (headMatch.Score == 100)
                {
                    item.MatchSeconds = headMatch.Sec2;
                    item.MatchDetail = I18nManager.Instance.GetString("TxtHeadMatch", "頭部 {0}秒").Replace("{0}", ((int)headMatch.Sec1).ToString());
                }
                else
                {
                    var tailMatch = SimilarityEngine.Compare(reference, item, FingerprintPosition.Tail);
                    item.MatchSeconds = tailMatch.Sec2;
                    item.MatchDetail = I18nManager.Instance.GetString("TxtTailMatch", "尾部 {0}秒").Replace("{0}", ((int)tailMatch.Sec1).ToString());
                }
            }

            return sorted;
        }
    }
}