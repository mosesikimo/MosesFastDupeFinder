using System;
using System.Buffers; 
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices; 
using System.Runtime.InteropServices; 
using System.Threading;

namespace FastDupeFinder
{
    public static class SimilarityEngine
    {
        // 💡 提取為高效公用方法
        private static (int Score, FingerprintPosition MatchPos) CompareHashes(List<VideoFingerprint> list1, List<VideoFingerprint> list2)
        {
            VideoFingerprint? fp1_head = null, fp1_tail = null;
            VideoFingerprint? fp2_head = null, fp2_tail = null;

            var f1Span = CollectionsMarshal.AsSpan(list1);
            for (int i = 0; i < f1Span.Length; i++)
            {
                var fp = f1Span[i];
                if (fp.Position == FingerprintPosition.Head) fp1_head = fp;
                else if (fp.Position == FingerprintPosition.Tail) fp1_tail = fp;
            }

            var f2Span = CollectionsMarshal.AsSpan(list2);
            for (int i = 0; i < f2Span.Length; i++)
            {
                var fp = f2Span[i];
                if (fp.Position == FingerprintPosition.Head) fp2_head = fp;
                else if (fp.Position == FingerprintPosition.Tail) fp2_tail = fp;
            }

            bool Match(VideoFingerprint? a, VideoFingerprint? b) {
                if (a == null || b == null || a.Hashes.Count == 0 || b.Hashes.Count == 0) return false;
                ref ulong q = ref MemoryMarshal.GetArrayDataReference(a.Hashes[0]);
                for (int v = 0; v < 8; v++) {
                    ref ulong c = ref MemoryMarshal.GetArrayDataReference(b.Hashes[v]);
                    int diff = BitOperations.PopCount(q ^ c) 
                             + BitOperations.PopCount(Unsafe.Add(ref q, 1) ^ Unsafe.Add(ref c, 1)) 
                             + BitOperations.PopCount(Unsafe.Add(ref q, 2) ^ Unsafe.Add(ref c, 2)) 
                             + BitOperations.PopCount(Unsafe.Add(ref q, 3) ^ Unsafe.Add(ref c, 3));
                    if (diff <= 5) return true;
                }
                return false;
            }

            // 維持 OR 邏輯
            if (Match(fp1_head, fp2_head)) return (100, FingerprintPosition.Head);
            if (Match(fp1_tail, fp2_tail)) return (100, FingerprintPosition.Tail);

            return (0, FingerprintPosition.Unknown);
        }

        public static (int Score, FingerprintPosition MatchPos) Compare(DupeFileItem f1, DupeFileItem f2)
        {
            // 💡 顯示比對細節時，先嘗試用全畫面看是否有成功相連
            var (score, pos) = CompareHashes(f1.FullFingerprints, f2.FullFingerprints);
            if (score == 100) return (score, pos);

            // 若無則退回中央畫面
            return CompareHashes(f1.Fingerprints, f2.Fingerprints);
        }
    }

    public static class ScanEngine
    {
        private static bool CheckHashMatch(VideoFingerprint? fA, VideoFingerprint? fB)
        {
            if (fA == null || fB == null || fA.Hashes.Count == 0 || fB.Hashes.Count == 0) return false;
            ref ulong q = ref MemoryMarshal.GetArrayDataReference(fA.Hashes[0]);
            for (int v = 0; v < 8; v++)
            {
                ref ulong c = ref MemoryMarshal.GetArrayDataReference(fB.Hashes[v]);
                int diff = BitOperations.PopCount(q ^ c)
                         + BitOperations.PopCount(Unsafe.Add(ref q, 1) ^ Unsafe.Add(ref c, 1))
                         + BitOperations.PopCount(Unsafe.Add(ref q, 2) ^ Unsafe.Add(ref c, 2))
                         + BitOperations.PopCount(Unsafe.Add(ref q, 3) ^ Unsafe.Add(ref c, 3));
                if (diff <= 5) return true;
            }
            return false;
        }

        public static List<List<DupeFileItem>> GroupFiles(List<DupeFileItem> files)
        {
            ThreadPriority originalPriority = Thread.CurrentThread.Priority;
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.Highest;

                int n = files.Count;
                if (n == 0) return new List<List<DupeFileItem>>();

                int[] parent = ArrayPool<int>.Shared.Rent(n);
                int[] rank = ArrayPool<int>.Shared.Rent(n);
                
                try
                {
                    for (int i = 0; i < n; i++) { parent[i] = i; rank[i] = 0; }
                    
                    int Find(int x) 
                    { 
                        int root = x;
                        while (root != parent[root]) root = parent[root];
                        int curr = x;
                        while (curr != root) { int next = parent[curr]; parent[curr] = root; curr = next; }
                        return root; 
                    }
                    
                    void Union(int x, int y) {
                        int rx = Find(x), ry = Find(y);
                        if (rx == ry) return;
                        if (rank[rx] < rank[ry]) parent[rx] = ry;
                        else if (rank[rx] > rank[ry]) parent[ry] = rx;
                        else { parent[ry] = rx; rank[rx]++; }
                    }

                    int dictCapacity = (n / 4) + 1;
                    
                    var headBuckets = new Dictionary<ulong, List<int>>[4];
                    var tailBuckets = new Dictionary<ulong, List<int>>[4];
                    for (int p = 0; p < 4; p++)
                    {
                        headBuckets[p] = new Dictionary<ulong, List<int>>(dictCapacity);
                        tailBuckets[p] = new Dictionary<ulong, List<int>>(dictCapacity);
                    }

                    var candidates = new HashSet<int>();

                    for (int i = 0; i < n; i++)
                    {
                        var f = files[i];
                        if (f.Fingerprints.Count == 0) continue;

                        VideoFingerprint? f_head = null;
                        VideoFingerprint? f_tail = null;
                        
                        var fpSpan = CollectionsMarshal.AsSpan(f.Fingerprints);
                        for (int fi = 0; fi < fpSpan.Length; fi++)
                        {
                            var fp = fpSpan[fi];
                            if (fp.Position == FingerprintPosition.Head) f_head = fp;
                            else if (fp.Position == FingerprintPosition.Tail) f_tail = fp;
                        }

                        if (f_head != null && f_head.Hashes.Count > 0)
                        {
                            candidates.Clear();
                            ulong[] queryHash = f_head.Hashes[0];
                            for (int p = 0; p < 4; p++)
                            {
                                if (headBuckets[p].TryGetValue(queryHash[p], out var list))
                                {
                                    int count = list.Count;
                                    var listSpan = CollectionsMarshal.AsSpan(list);
                                    for (int k = 0; k < count; k++) candidates.Add(listSpan[k]);
                                }
                            }

                            foreach (int candIdx in candidates)
                            {
                                if (Find(i) == Find(candIdx)) continue;
                                var candFile = files[candIdx];
                                VideoFingerprint? candHead = null;
                                var candFpSpan = CollectionsMarshal.AsSpan(candFile.Fingerprints);
                                for (int fi = 0; fi < candFpSpan.Length; fi++)
                                {
                                    if (candFpSpan[fi].Position == FingerprintPosition.Head) { candHead = candFpSpan[fi]; break; }
                                }

                                if (CheckHashMatch(f_head, candHead)) Union(i, candIdx);
                            }
                        }

                        if (f_tail != null && f_tail.Hashes.Count > 0)
                        {
                            candidates.Clear();
                            ulong[] queryHash = f_tail.Hashes[0];
                            for (int p = 0; p < 4; p++)
                            {
                                if (tailBuckets[p].TryGetValue(queryHash[p], out var list))
                                {
                                    int count = list.Count;
                                    var listSpan = CollectionsMarshal.AsSpan(list);
                                    for (int k = 0; k < count; k++) candidates.Add(listSpan[k]);
                                }
                            }

                            foreach (int candIdx in candidates)
                            {
                                if (Find(i) == Find(candIdx)) continue;
                                var candFile = files[candIdx];
                                VideoFingerprint? candTail = null;
                                var candFpSpan = CollectionsMarshal.AsSpan(candFile.Fingerprints);
                                for (int fi = 0; fi < candFpSpan.Length; fi++)
                                {
                                    if (candFpSpan[fi].Position == FingerprintPosition.Tail) { candTail = candFpSpan[fi]; break; }
                                }

                                if (CheckHashMatch(f_tail, candTail)) Union(i, candIdx);
                            }
                        }

                        if (f_head != null && f_head.Hashes.Count >= 8)
                        {
                            for (int v = 0; v < 8; v++)
                            {
                                ulong[] h = f_head.Hashes[v];
                                for (int p = 0; p < 4; p++)
                                {
                                    ulong partVal = h[p];
                                    if (!headBuckets[p].TryGetValue(partVal, out var list))
                                    {
                                        list = new List<int>(8);
                                        headBuckets[p][partVal] = list;
                                        list.Add(i);
                                    }
                                    else if (list[list.Count - 1] != i)
                                    {
                                        list.Add(i);
                                    }
                                }
                            }
                        }

                        if (f_tail != null && f_tail.Hashes.Count >= 8)
                        {
                            for (int v = 0; v < 8; v++)
                            {
                                ulong[] h = f_tail.Hashes[v];
                                for (int p = 0; p < 4; p++)
                                {
                                    ulong partVal = h[p];
                                    if (!tailBuckets[p].TryGetValue(partVal, out var list))
                                    {
                                        list = new List<int>(8);
                                        tailBuckets[p][partVal] = list;
                                        list.Add(i);
                                    }
                                    else if (list[list.Count - 1] != i) 
                                    {
                                        list.Add(i);
                                    }
                                }
                            }
                        }
                    }

                    var groupMap = new Dictionary<int, List<DupeFileItem>>(n / 2 + 1);
                    for (int i = 0; i < n; i++)
                    {
                        int root = Find(i);
                        if (!groupMap.TryGetValue(root, out var list))
                        {
                            list = new List<DupeFileItem>();
                            groupMap[root] = list;
                        }
                        list.Add(files[i]);
                    }

                    var initialGroups = groupMap.Values.Where(g => g.Count >= 2).ToList();
                    var finalGroups = new List<List<DupeFileItem>>();

                    foreach (var group in initialGroups)
                    {
                        // 💡 第一階段 (中央區域) 若揪出大於等於 4 的大群組
                        // 則觸發第二階段：對它們再做一次「全畫面」的洗牌與拆解！
                        if (group.Count >= 4)
                        {
                            finalGroups.AddRange(RegroupStrict(group));
                        }
                        else
                        {
                            finalGroups.Add(group);
                        }
                    }

                    return finalGroups;
                }
                finally
                {
                    ArrayPool<int>.Shared.Return(parent);
                    ArrayPool<int>.Shared.Return(rank);
                }
            }
            finally
            {
                Thread.CurrentThread.Priority = originalPriority;
            }
        }

        // 💡 針對大群組的「全畫面」二次洗牌
        private static List<List<DupeFileItem>> RegroupStrict(List<DupeFileItem> group)
        {
            int n = group.Count;
            int[] parent = ArrayPool<int>.Shared.Rent(n);
            int[] rank = ArrayPool<int>.Shared.Rent(n);

            try
            {
                for (int i = 0; i < n; i++) { parent[i] = i; rank[i] = 0; }

                int Find(int x) 
                { 
                    int root = x;
                    while (root != parent[root]) root = parent[root];
                    int curr = x;
                    while (curr != root) { int next = parent[curr]; parent[curr] = root; curr = next; }
                    return root; 
                }
                
                void Union(int x, int y) {
                    int rx = Find(x), ry = Find(y);
                    if (rx == ry) return;
                    if (rank[rx] < rank[ry]) parent[rx] = ry;
                    else if (rank[rx] > rank[ry]) parent[ry] = rx;
                    else { parent[ry] = rx; rank[rx]++; }
                }

                for (int i = 0; i < n; i++)
                {
                    for (int j = i + 1; j < n; j++)
                    {
                        if (Find(i) == Find(j)) continue;

                        var f1 = group[i];
                        var f2 = group[j];

                        VideoFingerprint? f1_head = null, f1_tail = null;
                        VideoFingerprint? f2_head = null, f2_tail = null;

                        // 取出第二層(全畫面)特徵
                        var fp1Span = CollectionsMarshal.AsSpan(f1.FullFingerprints);
                        for (int k = 0; k < fp1Span.Length; k++)
                        {
                            if (fp1Span[k].Position == FingerprintPosition.Head) f1_head = fp1Span[k];
                            else if (fp1Span[k].Position == FingerprintPosition.Tail) f1_tail = fp1Span[k];
                        }

                        var fp2Span = CollectionsMarshal.AsSpan(f2.FullFingerprints);
                        for (int k = 0; k < fp2Span.Length; k++)
                        {
                            if (fp2Span[k].Position == FingerprintPosition.Head) f2_head = fp2Span[k];
                            else if (fp2Span[k].Position == FingerprintPosition.Tail) f2_tail = fp2Span[k];
                        }

                        // 如果全畫面特徵中，頭或尾有任一吻合，則建立關聯
                        if (CheckHashMatch(f1_head, f2_head) || CheckHashMatch(f1_tail, f2_tail))
                        {
                            Union(i, j);
                        }
                    }
                }

                var map = new Dictionary<int, List<DupeFileItem>>();
                for (int i = 0; i < n; i++)
                {
                    int root = Find(i);
                    if (!map.TryGetValue(root, out var list)) map[root] = new List<DupeFileItem>();
                    map[root].Add(group[i]);
                }

                return map.Values.Where(g => g.Count >= 2).ToList();
            }
            finally
            {
                ArrayPool<int>.Shared.Return(parent);
                ArrayPool<int>.Shared.Return(rank);
            }
        }

        public static List<DupeFileItem> AssignGroupMetadata(List<DupeFileItem> group, int displayGroupId)
        {
            DupeFileItem reference = group.OrderByDescending(f => f.Duration.TotalSeconds).ThenByDescending(f => f.Size).First();

            reference.IsReference = true;
            reference.SimilarityScore = 100;
            reference.MatchDetail = "";

            var sorted = group.OrderByDescending(x => x.IsReference).ThenBy(x => x.FilePath).ToList();

            foreach (var item in sorted)
            {
                item.OriginalGroupId = displayGroupId;
                if (item == reference) continue;

                item.IsReference = false;
                item.SimilarityScore = 100;

                var match = SimilarityEngine.Compare(reference, item);
                if (match.Score == 100)
                {
                    item.MatchDetail = match.MatchPos switch {
                        FingerprintPosition.Head => string.Format(I18nManager.Instance.GetString("TxtHeadMatch", "頭部 {0}s"), (int)item.Duration.TotalSeconds),
                        FingerprintPosition.Tail => string.Format(I18nManager.Instance.GetString("TxtTailMatch", "尾部 {0}s"), (int)item.Duration.TotalSeconds),
                        _ => $"{item.Duration.TotalSeconds}s"
                    };
                }
                else
                {
                    item.MatchDetail = I18nManager.Instance.GetString("TxtIndirectMatch", "間接關聯 (同源組合片段)");
                }
            }

            return sorted;
        }

        // 💡 升級：強效「紋理與邊緣」過濾器 (Texture/Edge Check)
        // 這個方法不但會檢測最高/最低亮度的對比，還會分析「相鄰像素是否有銳利的起伏落差」。
        // 如果畫面是一整片平滑的藍天、皮膚或失焦牆壁，雖然對比度足夠，但缺乏銳利邊緣，會被完美剔除！
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool HasEnoughTexture(ReadOnlySpan<int> pixels)
        {
            int min = 255;
            int max = 0;
            int edgeCount = 0;

            for (int y = 0; y < 16; y++)
            {
                int ro = y * 16;
                for (int x = 0; x < 16; x++)
                {
                    int val = pixels[ro + x];
                    if (val < min) min = val;
                    if (val > max) max = val;

                    // 檢查右側與下方的相鄰像素落差 (避開越界)
                    // 若落差大於 4 (意味著不是平滑的漸層，而是真正的物件邊緣/紋理)，則視為有效特徵點
                    if (x < 15) {
                        int diffX = val - pixels[ro + x + 1];
                        if (diffX > 4 || diffX < -4) edgeCount++;
                    }
                    if (y < 15) {
                        int diffY = val - pixels[ro + 16 + x];
                        if (diffY > 4 || diffY < -4) edgeCount++;
                    }
                }
            }
            
            // 1. 全局對比度 >= 20 (防範純素色)
            // 2. 有效邊緣數量 >= 10 (在 480 次相鄰比對中，這要求非常低，正常的影片就算是一面白牆也會有陰影邊緣，但可以完美秒殺完全失焦或被手指遮住的漸層畫面)
            return (max - min) >= 20 && edgeCount >= 10;
        }

        public static bool CalculateCombinedHashVariantsFromMemory(byte[] imageData, 
            out List<ulong[]>? headVariants, out List<ulong[]>? tailVariants,
            out List<ulong[]>? fullHeadVariants, out List<ulong[]>? fullTailVariants)
        {
            headVariants = null; tailVariants = null; fullHeadVariants = null; fullTailVariants = null;
            if (imageData == null || imageData.Length != 1024) return false;

            ThreadPriority originalPriority = Thread.CurrentThread.Priority;
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.Highest;

                Span<int> leftGrays = stackalloc int[256];
                Span<int> rightGrays = stackalloc int[256];
                Span<int> fullLeftGrays = stackalloc int[256];
                Span<int> fullRightGrays = stackalloc int[256];

                for (int y = 0; y < 16; y++)
                {
                    int rowStart = y * 64; 
                    for (int x = 0; x < 16; x++)
                    {
                        int idx = y * 16 + x;
                        leftGrays[idx] = imageData[rowStart + x]; 
                        rightGrays[idx] = imageData[rowStart + 16 + x];
                        fullLeftGrays[idx] = imageData[rowStart + 32 + x];
                        fullRightGrays[idx] = imageData[rowStart + 48 + x];
                    }
                }

                // 💡 導入強效紋理檢測，完美隔離您上傳的那兩張「平滑漸層無內容」的圖片
                if (HasEnoughTexture(leftGrays))
                {
                    var temp = new List<ulong[]>(8);
                    Generate16x16VariantsInline(leftGrays, temp); 
                    int popCount = BitOperations.PopCount(temp[0][0]) + BitOperations.PopCount(temp[0][1]) + BitOperations.PopCount(temp[0][2]) + BitOperations.PopCount(temp[0][3]);
                    if (popCount >= 10 && popCount <= 246) headVariants = temp;
                }
                
                if (HasEnoughTexture(rightGrays))
                {
                    var temp = new List<ulong[]>(8);
                    Generate16x16VariantsInline(rightGrays, temp);
                    int popCount = BitOperations.PopCount(temp[0][0]) + BitOperations.PopCount(temp[0][1]) + BitOperations.PopCount(temp[0][2]) + BitOperations.PopCount(temp[0][3]);
                    if (popCount >= 10 && popCount <= 246) tailVariants = temp;
                }

                if (HasEnoughTexture(fullLeftGrays))
                {
                    var temp = new List<ulong[]>(8);
                    Generate16x16VariantsInline(fullLeftGrays, temp); 
                    int popCount = BitOperations.PopCount(temp[0][0]) + BitOperations.PopCount(temp[0][1]) + BitOperations.PopCount(temp[0][2]) + BitOperations.PopCount(temp[0][3]);
                    if (popCount >= 10 && popCount <= 246) fullHeadVariants = temp;
                }
                
                if (HasEnoughTexture(fullRightGrays))
                {
                    var temp = new List<ulong[]>(8);
                    Generate16x16VariantsInline(fullRightGrays, temp);
                    int popCount = BitOperations.PopCount(temp[0][0]) + BitOperations.PopCount(temp[0][1]) + BitOperations.PopCount(temp[0][2]) + BitOperations.PopCount(temp[0][3]);
                    if (popCount >= 10 && popCount <= 246) fullTailVariants = temp;
                }
                
                return headVariants != null || tailVariants != null || fullHeadVariants != null || fullTailVariants != null;
            }
            finally
            {
                Thread.CurrentThread.Priority = originalPriority;
            }
        }

        public static bool CalculateSingleHashVariantsFromMemory(byte[] imageData, out List<ulong[]>? variants, out List<ulong[]>? fullVariants)
        {
            variants = null; fullVariants = null;
            if (imageData == null || imageData.Length != 512) return false;

            ThreadPriority originalPriority = Thread.CurrentThread.Priority;
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.Highest;

                Span<int> grays = stackalloc int[256];
                Span<int> fullGrays = stackalloc int[256];

                for (int y = 0; y < 16; y++)
                {
                    int rowStart = y * 32;
                    for (int x = 0; x < 16; x++)
                    {
                        int idx = y * 16 + x;
                        grays[idx] = imageData[rowStart + x];
                        fullGrays[idx] = imageData[rowStart + 16 + x];
                    }
                }

                if (HasEnoughTexture(grays))
                {
                    var temp = new List<ulong[]>(8);
                    Generate16x16VariantsInline(grays, temp);
                    int popCount = BitOperations.PopCount(temp[0][0]) + BitOperations.PopCount(temp[0][1]) + BitOperations.PopCount(temp[0][2]) + BitOperations.PopCount(temp[0][3]);
                    if (popCount >= 10 && popCount <= 246) variants = temp;
                }

                if (HasEnoughTexture(fullGrays))
                {
                    var temp = new List<ulong[]>(8);
                    Generate16x16VariantsInline(fullGrays, temp);
                    int popCount = BitOperations.PopCount(temp[0][0]) + BitOperations.PopCount(temp[0][1]) + BitOperations.PopCount(temp[0][2]) + BitOperations.PopCount(temp[0][3]);
                    if (popCount >= 10 && popCount <= 246) fullVariants = temp;
                }
                
                return variants != null || fullVariants != null;
            }
            finally
            {
                Thread.CurrentThread.Priority = originalPriority;
            }
        }

        private static void Generate16x16VariantsInline(ReadOnlySpan<int> pixels, List<ulong[]> list)
        {
            for (int v = 0; v < 8; v++)
            {
                ulong[] h = new ulong[4]; 
                switch (v)
                {
                    case 0:
                        for (int y = 0; y < 16; y++) {
                            int ro = y * 16;
                            for (int x = 0; x < 16; x++) {
                                if (pixels[ro + x] > pixels[ro + ((x + 1) & 15)]) {
                                    int idx = ro + x; h[idx >> 6] |= (1UL << (63 - (idx & 63)));
                                }
                            }
                        }
                        break;
                    case 1:
                        for (int y = 0; y < 16; y++) {
                            int ro = y * 16;
                            for (int x = 0; x < 16; x++) {
                                int sx = 15 - x;
                                if (pixels[ro + sx] > pixels[ro + ((sx - 1) & 15)]) {
                                    int idx = ro + x; h[idx >> 6] |= (1UL << (63 - (idx & 63)));
                                }
                            }
                        }
                        break;
                    case 2:
                        for (int y = 0; y < 16; y++) {
                            int ro = y * 16;
                            int sr = (15 - y) * 16;
                            for (int x = 0; x < 16; x++) {
                                if (pixels[sr + x] > pixels[sr + ((x + 1) & 15)]) {
                                    int idx = ro + x; h[idx >> 6] |= (1UL << (63 - (idx & 63)));
                                }
                            }
                        }
                        break;
                    case 3:
                        for (int y = 0; y < 16; y++) {
                            int ro = y * 16;
                            int sr = (15 - y) * 16;
                            for (int x = 0; x < 16; x++) {
                                int sx = 15 - x;
                                if (pixels[sr + sx] > pixels[sr + ((sx - 1) & 15)]) {
                                    int idx = ro + x; h[idx >> 6] |= (1UL << (63 - (idx & 63)));
                                }
                            }
                        }
                        break;
                    case 4:
                        for (int y = 0; y < 16; y++) {
                            int ro = y * 16;
                            for (int x = 0; x < 16; x++) {
                                int sy = x, sx = y;
                                if (pixels[(sy << 4) + sx] > pixels[(((sy + 1) & 15) << 4) + sx]) {
                                    int idx = ro + x; h[idx >> 6] |= (1UL << (63 - (idx & 63)));
                                }
                            }
                        }
                        break;
                    case 5:
                        for (int y = 0; y < 16; y++) {
                            int ro = y * 16;
                            for (int x = 0; x < 16; x++) {
                                int sy = 15 - x, sx = y;
                                if (pixels[(sy << 4) + sx] > pixels[(((sy - 1) & 15) << 4) + sx]) {
                                    int idx = ro + x; h[idx >> 6] |= (1UL << (63 - (idx & 63)));
                                }
                            }
                        }
                        break;
                    case 6:
                        for (int y = 0; y < 16; y++) {
                            int ro = y * 16;
                            for (int x = 0; x < 16; x++) {
                                int sy = x, sx = 15 - y;
                                if (pixels[(sy << 4) + sx] > pixels[(((sy + 1) & 15) << 4) + sx]) {
                                    int idx = ro + x; h[idx >> 6] |= (1UL << (63 - (idx & 63)));
                                }
                            }
                        }
                        break;
                    case 7:
                        for (int y = 0; y < 16; y++) {
                            int ro = y * 16;
                            for (int x = 0; x < 16; x++) {
                                int sy = 15 - x, sx = 15 - y;
                                if (pixels[(sy << 4) + sx] > pixels[(((sy - 1) & 15) << 4) + sx]) {
                                    int idx = ro + x; h[idx >> 6] |= (1UL << (63 - (idx & 63)));
                                }
                            }
                        }
                        break;
                }
                list.Add(h);
            }
        }
    }
}