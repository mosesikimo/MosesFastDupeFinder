using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices; 
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.WindowsAPICodePack.Shell;

namespace FastDupeFinder
{
    public class FFmpegWorker : IDisposable
    {
        private readonly Process _process;
        private readonly byte[] _buffer = new byte[1024]; 

        public FFmpegWorker(string ffmpegPath)
        {
            _process = new Process();
            _process.StartInfo.FileName = ffmpegPath;
            _process.StartInfo.UseShellExecute = false;
            _process.StartInfo.CreateNoWindow = true;
            _process.StartInfo.RedirectStandardOutput = true;
            _process.StartInfo.RedirectStandardError = true;
            _process.StartInfo.ErrorDialog = false;
        }

        public async Task<byte[]?> ExtractAsync(string arguments, int expectedLength, int timeoutMs, CancellationToken ct)
        {
            _process.StartInfo.Arguments = arguments;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            try
            {
                _process.Start();
                
                try { _process.PriorityClass = ProcessPriorityClass.High; } catch { }

                var discardStdErrTask = Task.Run(async () =>
                {
                    try { await _process.StandardError.BaseStream.CopyToAsync(Stream.Null, 8192, cts.Token); } catch { }
                });

                int totalRead = 0;
                var stream = _process.StandardOutput.BaseStream;
                while (totalRead < expectedLength && !cts.Token.IsCancellationRequested)
                {
                    int read = await stream.ReadAsync(_buffer.AsMemory(totalRead, expectedLength - totalRead), cts.Token);
                    if (read == 0) break;
                    totalRead += read;
                }

                await Task.WhenAny(_process.WaitForExitAsync(cts.Token), Task.Delay(timeoutMs, cts.Token));

                if (!_process.HasExited)
                {
                    try { _process.Kill(true); } catch { }
                }

                try { await discardStdErrTask; } catch { }

                if (totalRead == expectedLength)
                {
                    byte[] result = new byte[expectedLength];
                    Array.Copy(_buffer, result, expectedLength);
                    return result; 
                }

                return null;
            }
            catch { return null; }
            finally
            {
                try { _process.Close(); } catch { } 
            }
        }

        public void Dispose()
        {
            _process?.Dispose();
        }
    }

    public static class FFmpegPoolManager
    {
        private static readonly ConcurrentDictionary<string, Channel<FFmpegWorker>> _pools = new(StringComparer.OrdinalIgnoreCase);
        private static readonly int _workersPerHdd = new PipelineConfig().FfmpegIoLimit;

        public static async Task<FFmpegWorker> RentAsync(string filePath, CancellationToken ct)
        {
            string driveRoot = Path.GetPathRoot(filePath) ?? "DEFAULT";
            
            var pool = _pools.GetOrAdd(driveRoot, _ =>
            {
                var channel = Channel.CreateBounded<FFmpegWorker>(_workersPerHdd);
                string path = FFmpegHelper.GetPath();
                for (int i = 0; i < _workersPerHdd; i++)
                {
                    channel.Writer.TryWrite(new FFmpegWorker(path));
                }
                return channel;
            });

            return await pool.Reader.ReadAsync(ct);
        }

        public static void Return(string filePath, FFmpegWorker worker)
        {
            string driveRoot = Path.GetPathRoot(filePath) ?? "DEFAULT";
            if (_pools.TryGetValue(driveRoot, out var pool))
            {
                pool.Writer.TryWrite(worker);
            }
        }
    }

    public static class FFmpegHelper
    {
        private static readonly char[] _durationSplitChars = { ' ', ',', '\t' };

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern uint GetShortPathName(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);

        public static string GetShortPath(string path)
        {
            var sb = new StringBuilder(256);
            uint result = GetShortPathName(path, sb, (uint)sb.Capacity);
            if (result > sb.Capacity)
            {
                sb.Capacity = (int)result;
                result = GetShortPathName(path, sb, (uint)sb.Capacity);
            }
            return result == 0 ? path : sb.ToString();
        }

        public static string GetPath()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(path)) return path;

            var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
            while (currentDir != null)
            {
                string checkPath = Path.Combine(currentDir.FullName, "ffmpeg.exe");
                if (File.Exists(checkPath)) return checkPath;
                currentDir = currentDir.Parent;
            }
            return "";
        }

        public static async Task<bool> RunCommandAsync(string args, CancellationToken ct)
        {
            string ffmpegPath = GetPath();
            if (string.IsNullOrEmpty(ffmpegPath)) return false;

            var processInfo = new ProcessStartInfo { FileName = ffmpegPath, Arguments = args, UseShellExecute = false, CreateNoWindow = true };
            using var process = new Process { StartInfo = processInfo, EnableRaisingEvents = true };
            try 
            { 
                process.Start(); 
                try { process.PriorityClass = ProcessPriorityClass.High; } catch { } 
                await process.WaitForExitAsync(ct); 
                return process.ExitCode == 0; 
            }
            catch { return false; }
        }

        public static async Task<byte[]?> RunCommandAndGetOutputAsync(string arguments, int timeoutMs, CancellationToken ct)
        {
            string ffmpegPath = GetPath();
            if (string.IsNullOrEmpty(ffmpegPath)) return null;

            var processInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath, Arguments = arguments,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var outputStream = new MemoryStream();

            try
            {
                using var process = new Process { StartInfo = processInfo, EnableRaisingEvents = true };
                process.Start();
                try { process.PriorityClass = ProcessPriorityClass.High; } catch { } 

                var copyStdOut = CopyStreamWithTimeout(process.StandardOutput.BaseStream, outputStream, cts.Token);
                var copyStdErr = CopyStreamWithTimeout(process.StandardError.BaseStream, Stream.Null, cts.Token);

                cts.CancelAfter(timeoutMs);

                await Task.WhenAny(process.WaitForExitAsync(cts.Token), Task.Delay(timeoutMs + 500, ct));

                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return null;
                }

                await Task.WhenAll(copyStdOut, copyStdErr);
                return process.ExitCode == 0 ? outputStream.ToArray() : null;
            }
            catch { return null; }
        }

        private static async Task CopyStreamWithTimeout(Stream source, Stream destination, CancellationToken token)
        {
            try { await source.CopyToAsync(destination, 81920, token); } catch { }
        }

        public static async Task<TimeSpan?> GetDurationAsync(string filePath, CancellationToken ct)
        {
            string ffmpegPath = GetPath();
            if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(ffmpegPath)) return null;

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath, Arguments = $"-hide_banner -i \"{filePath}\"",
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(3000);

            try
            {
                process.Start();
                try { process.PriorityClass = ProcessPriorityClass.High; } catch { } 
                _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cts.Token);

                using var reader = process.StandardError;
                string? line;
                while ((line = await reader.ReadLineAsync(cts.Token)) != null)
                {
                    if (line.TrimStart().StartsWith("Duration:"))
                    {
                        var parts = line.Split(_durationSplitChars, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 1 && TimeSpan.TryParse(parts[1], out var duration))
                        {
                            if (!process.HasExited) try { process.Kill(true); } catch { }
                            return duration;
                        }
                    }
                }
            }
            catch { }
            finally { if (!process.HasExited) try { process.Kill(true); } catch { } }
            return null;
        }

        public static async Task ExtractCombinedFingerprintsAsync(DupeFileItem item, CancellationToken ct, int ffmpegTimeoutMs)
        {
            var worker = await FFmpegPoolManager.RentAsync(item.FilePath, ct);
            try
            {
                string rwTimeout = "3000000";
                
                // 💡 雙管齊下提取術：一次性在記憶體中生成 [裁切40%防黑邊] 與 [全畫面100%防誤判] 四張圖，極速兼顧所有優點。
                string ExtractLogic(string path) => $"-rw_timeout {rwTimeout} -v quiet " +
                   $"-skip_frame nokey -noaccurate_seek -threads 1 -ss {item.HeadExtractSec} -i \"{path}\" " +
                   $"-skip_frame nokey -noaccurate_seek -threads 1 -ss {item.TailExtractSec} -i \"{path}\" " +
                   $"-an -sn -dn -filter_complex \"[0:v]crop=iw*0.4:ih*0.4,scale=16:16,setsar=1,format=gray[c_h];" +
                   $"[1:v]crop=iw*0.4:ih*0.4,scale=16:16,setsar=1,format=gray[c_t];" +
                   $"[0:v]scale=16:16,setsar=1,format=gray[f_h];" +
                   $"[1:v]scale=16:16,setsar=1,format=gray[f_t];" +
                   $"[c_h][c_t][f_h][f_t]hstack=inputs=4,format=gray\" " +
                   $"-frames:v 1 -f rawvideo pipe:1";

                byte[]? imageBytes = await worker.ExtractAsync(ExtractLogic(item.FilePath), 1024, ffmpegTimeoutMs, ct);
                
                if (imageBytes == null || imageBytes.Length != 1024)
                {
                    string shortPath = GetShortPath(item.FilePath);
                    if (shortPath != item.FilePath)
                    {
                        imageBytes = await worker.ExtractAsync(ExtractLogic(shortPath), 1024, ffmpegTimeoutMs, ct);
                    }
                }

                if (imageBytes != null && imageBytes.Length == 1024)
                {
                    if (ScanEngine.CalculateCombinedHashVariantsFromMemory(imageBytes, out var headV, out var tailV, out var fullHeadV, out var fullTailV))
                    {
                        lock (item.Fingerprints)
                        {
                            if (headV != null) item.Fingerprints.Add(new VideoFingerprint(headV, item.HeadExtractSec, FingerprintPosition.Head));
                            if (tailV != null) item.Fingerprints.Add(new VideoFingerprint(tailV, item.TailExtractSec, FingerprintPosition.Tail));
                            if (fullHeadV != null) item.FullFingerprints.Add(new VideoFingerprint(fullHeadV, item.HeadExtractSec, FingerprintPosition.Head));
                            if (fullTailV != null) item.FullFingerprints.Add(new VideoFingerprint(fullTailV, item.TailExtractSec, FingerprintPosition.Tail));
                        }
                        return;
                    }
                }

                string FallbackLogic(string path) => $"-rw_timeout {rwTimeout} -v quiet -skip_frame nokey -noaccurate_seek -threads 1 " +
                   $"-ss {item.HeadExtractSec} -i \"{path}\" -an -sn -dn -filter_complex \"[0:v]crop=iw*0.4:ih*0.4,scale=16:16,setsar=1,format=gray[c_h];" +
                   $"[0:v]scale=16:16,setsar=1,format=gray[f_h];[c_h][f_h]hstack=inputs=2,format=gray\" " +
                   $"-frames:v 1 -f rawvideo pipe:1";

                byte[]? headOnlyBytes = await worker.ExtractAsync(FallbackLogic(item.FilePath), 512, ffmpegTimeoutMs, ct);
                if (headOnlyBytes == null || headOnlyBytes.Length != 512)
                {
                    string shortPath = GetShortPath(item.FilePath);
                    if (shortPath != item.FilePath) headOnlyBytes = await worker.ExtractAsync(FallbackLogic(shortPath), 512, ffmpegTimeoutMs, ct);
                }

                if (headOnlyBytes != null && headOnlyBytes.Length == 512)
                {
                    if (ScanEngine.CalculateSingleHashVariantsFromMemory(headOnlyBytes, out var hV, out var fhV))
                    {
                        lock (item.Fingerprints)
                        {
                            if (hV != null) item.Fingerprints.Add(new VideoFingerprint(hV, item.HeadExtractSec, FingerprintPosition.Head));
                            if (fhV != null) item.FullFingerprints.Add(new VideoFingerprint(fhV, item.HeadExtractSec, FingerprintPosition.Head));
                        }
                    }
                }
            }
            finally
            {
                FFmpegPoolManager.Return(item.FilePath, worker);
            }
        }

        public static async Task<byte[]?> GenerateDualPreviewThumbnailBytesAsync(string videoPath, double headSec, double tailSec)
        {
            if (headSec < 0) headSec = 0;
            if (tailSec < 0) tailSec = headSec + 5;

            string args = $"-rw_timeout 3000000 -v quiet -ss {headSec} -i \"{videoPath}\" -ss {tailSec} -i \"{videoPath}\" " +
                        $"-filter_complex \"[0:v]scale=320:-2,setdar=16/9[l];[1:v]scale=320:-2,setdar=16/9[r];[l][r]hstack=inputs=2\" " +
                        $"-vframes 1 -q:v 2 -f image2 -c:v mjpeg pipe:1";

            byte[]? result = await RunCommandAndGetOutputAsync(args, 6000, CancellationToken.None);

            if (result == null || result.Length == 0)
            {
                string shortPath = GetShortPath(videoPath);
                if (shortPath != videoPath)
                {
                    args = $"-rw_timeout 3000000 -v quiet -ss {headSec} -i \"{shortPath}\" -ss {tailSec} -i \"{shortPath}\" " +
                           $"-filter_complex \"[0:v]scale=320:-2,setdar=16/9[l];[1:v]scale=320:-2,setdar=16/9[r];[l][r]hstack=inputs=2\" " +
                           $"-vframes 1 -q:v 2 -f image2 -c:v mjpeg pipe:1";
                    result = await RunCommandAndGetOutputAsync(args, 6000, CancellationToken.None);
                }
            }

            return result;
        }
    }

    public class FileInspectionResult
    {
        public string CacheKey { get; set; } = "";
        public string ContentCacheKey { get; set; } = ""; 
        public bool IsCacheHit { get; set; }
        public CachedVideoData? CachedData { get; set; }
        public bool IsBadOrDownloading { get; set; }
        public DupeFileItem? Metadata { get; set; }
    }

    public static class FileHelper
    {
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT FileOp);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        public static bool SendToRecycleBin(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                var shf = new SHFILEOPSTRUCT { wFunc = 0x0003, fFlags = 0x0454, pFrom = path + '\0' + '\0' };
                return SHFileOperation(ref shf) == 0;
            }
            catch { return false; }
        }

        private static bool IsBufferAllZeros(ReadOnlySpan<byte> buffer)
        {
            int len = buffer.Length;
            int vectorSize = Vector<byte>.Count;
            int i = 0;

            while (i <= len - vectorSize)
            {
                var vector = new Vector<byte>(buffer.Slice(i));
                if (vector != Vector<byte>.Zero) return false;
                i += vectorSize;
            }

            for (; i < len; i++)
            {
                if (buffer[i] != 0) return false;
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsSupportedVideoExtension(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;
            ReadOnlySpan<char> pathSpan = filePath.AsSpan();
            int dotIdx = pathSpan.LastIndexOf('.');
            if (dotIdx < 0) return false;
            ReadOnlySpan<char> extSpan = pathSpan.Slice(dotIdx);

            foreach (var ext in AppConstants.SupportedVideoExts)
            {
                if (extSpan.Equals(ext.AsSpan(), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool VerifyVideoHeaderSignature(ReadOnlySpan<byte> header)
        {
            if (header.Length < 4) return false;

            if (header.Length >= 8 && header[4] == 0x66 && header[5] == 0x74 && header[6] == 0x79 && header[7] == 0x70) return true;
            if (header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3) return true;
            if (header.Length >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
                header[8] == 0x41 && header[9] == 0x56 && header[10] == 0x49 && header[11] == 0x20) return true;
            if (header.Length >= 8 && header[0] == 0x30 && header[1] == 0x26 && header[2] == 0xB2 && header[3] == 0x75 &&
                header[4] == 0x8E && header[5] == 0x66 && header[6] == 0xCF && header[7] == 0x11) return true;
            if (header[0] == 0x46 && header[1] == 0x4C && header[2] == 0x56) return true;
            if (header[0] == 0x00 && header[1] == 0x00 && header[2] == 0x01 && (header[3] == 0xBA || header[3] == 0xB3)) return true;
            if (header[0] == 0x47) return true;
            if (header[0] == 0x2E && header[1] == 0x52 && header[2] == 0x4D && header[3] == 0x46) return true;

            return false;
        }

        public static bool IsFileInUse(string filePath)
        {
            try { using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read); return false; }
            catch { return true; }
        }

        public static async Task<FileInspectionResult?> InspectFileAsync(
            string filePath, 
            long sz, 
            long ticks,
            ConcurrentDictionary<string, CachedVideoData> cache,
            SemaphoreSlim? driveSemaphore, 
            CancellationToken ct)
        {
            var result = new FileInspectionResult();

            // 💡 推進到 v9，確保讀取到包含了 4 倍寬度全畫面提取的新資料結構
            string cacheKey = $"{sz}_{ticks}_{Path.GetFileName(filePath)}_v9";
            result.CacheKey = cacheKey;

            if (cache.TryGetValue(cacheKey, out var cachedData) && cachedData != null)
            {
                if (cachedData.IsBadFile)
                {
                    result.IsCacheHit = true;
                    result.IsBadOrDownloading = true; 
                    result.CachedData = cachedData;
                    return result;
                }

                if (cachedData.Fingerprints.Count > 0)
                {
                    result.IsCacheHit = true;
                    result.CachedData = cachedData;
                    
                    result.Metadata = new DupeFileItem
                    {
                        FilePath = filePath,
                        Size = sz,
                        Duration = TimeSpan.FromSeconds(cachedData.DurationSec),
                        Width = cachedData.Width,
                        Height = cachedData.Height,
                        Resolution = (cachedData.Width > 0 && cachedData.Height > 0) ? $"{cachedData.Width}x{cachedData.Height}" : I18nManager.Instance.GetString("ResUnknown", "未知"),
                        HitCache = true,
                        CacheKey = cacheKey,
                        HeadExtractSec = cachedData.Fingerprints.FirstOrDefault(f => f.Position == FingerprintPosition.Head)?.TimeSec ?? 60,
                        TailExtractSec = cachedData.Fingerprints.FirstOrDefault(f => f.Position == FingerprintPosition.Tail)?.TimeSec ?? 140
                    };
                    return result; 
                }
            }

            int totalRead = 0;
            bool isBadOrDownloading = false;
            byte[] headerBuffer = ArrayPool<byte>.Shared.Rent(131072);

            try
            {
                if (driveSemaphore != null) await driveSemaphore.WaitAsync(ct);

                try
                {
                    using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 131072, FileOptions.SequentialScan))
                    {
                        long fileLen = fs.Length;
                        
                        if (fileLen < 12)
                        {
                            isBadOrDownloading = true;
                        }
                        else
                        {
                            byte[] sigBuffer = ArrayPool<byte>.Shared.Rent(12);
                            try
                            {
                                int readSig = await fs.ReadAsync(sigBuffer.AsMemory(0, 12), ct);
                                if (readSig < 12 || !VerifyVideoHeaderSignature(sigBuffer.AsSpan(0, readSig)))
                                {
                                    isBadOrDownloading = true;
                                }
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(sigBuffer);
                            }

                            if (!isBadOrDownloading)
                            {
                                fs.Seek(0, SeekOrigin.Begin); 
                                int bytesToRead = (int)Math.Min(131072, fileLen);
                                await fs.ReadExactlyAsync(headerBuffer.AsMemory(0, bytesToRead), ct);
                                totalRead = bytesToRead;

                                int hashLen = Math.Min(4096, totalRead);
                                ulong contentHash = NativeHeaderParser.ComputeFastHash64(headerBuffer.AsSpan(0, hashLen));
                                result.ContentCacheKey = $"{sz}_{contentHash}_content_v9";

                                if (fileLen >= 131072)
                                {
                                    byte[] tailBuffer = ArrayPool<byte>.Shared.Rent(8192);
                                    try
                                    {
                                        fs.Seek(-8192, SeekOrigin.End);
                                        await fs.ReadExactlyAsync(tailBuffer.AsMemory(0, 8192), ct);
                                        isBadOrDownloading = IsBufferAllZeros(tailBuffer.AsSpan(0, 8192));
                                    }
                                    finally
                                    {
                                        ArrayPool<byte>.Shared.Return(tailBuffer);
                                    }
                                }
                            }
                        }
                    }

                    result.IsBadOrDownloading = isBadOrDownloading;
                    if (isBadOrDownloading) return result;

                    TimeSpan? finalDuration = null;
                    int finalWidth = 0;
                    int finalHeight = 0;

                    if (totalRead > 0)
                    {
                        ReadOnlySpan<char> extSpan = Path.GetExtension(filePath.AsSpan());

                        if (extSpan.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || 
                            extSpan.Equals(".m4v", StringComparison.OrdinalIgnoreCase))
                        {
                            if (NativeHeaderParser.TryParseMp4(headerBuffer.AsSpan(0, totalRead), out double durSec, out int w, out int h))
                            {
                                finalDuration = TimeSpan.FromSeconds(durSec);
                                finalWidth = w;
                                finalHeight = h;
                            }
                        }
                        else if (extSpan.Equals(".mkv", StringComparison.OrdinalIgnoreCase))
                        {
                            if (NativeHeaderParser.TryParseMkv(headerBuffer.AsSpan(0, totalRead), out double durSec, out int w, out int h))
                            {
                                finalDuration = TimeSpan.FromSeconds(durSec);
                                finalWidth = w;
                                finalHeight = h;
                            }
                        }
                    }

                    if (!finalDuration.HasValue || finalDuration.Value.TotalSeconds <= 0)
                    {
                        for (int i = 0; i < 2; i++)
                        {
                            try
                            {
                                using var shellFile = ShellFile.FromParsingName(filePath);
                                ulong? durationTicks = shellFile.Properties.System.Media.Duration.Value;
                                uint? width = shellFile.Properties.System.Video.FrameWidth.Value;
                                uint? height = shellFile.Properties.System.Video.FrameHeight.Value;

                                if (durationTicks.HasValue && durationTicks.Value > 0)
                                {
                                    finalDuration = TimeSpan.FromTicks((long)durationTicks.Value);
                                    finalWidth = (int)(width ?? 0);
                                    finalHeight = (int)(height ?? 0);
                                    break;
                                }
                            }
                            catch { if (i < 1) await Task.Delay(100, ct); }
                        }
                    }

                    if (!finalDuration.HasValue || finalDuration.Value.TotalSeconds <= 0)
                    {
                        finalDuration = await FFmpegHelper.GetDurationAsync(filePath, ct);
                    }

                    if (finalDuration.HasValue && finalDuration.Value.TotalSeconds > 0)
                    {
                        result.Metadata = new DupeFileItem
                        {
                            FilePath = filePath,
                            Size = sz,
                            Duration = finalDuration.Value,
                            Width = finalWidth,
                            Height = finalHeight,
                            Resolution = (finalWidth > 0 && finalHeight > 0) ? $"{finalWidth}x{finalHeight}" : I18nManager.Instance.GetString("ResUnknown", "未知"),
                            ContentCacheKey = result.ContentCacheKey
                        };

                        double totalSec = finalDuration.Value.TotalSeconds;
                        
                        // 動態智能擷取：完全依據您提供的階梯秒數條件實作
                        if (totalSec >= 400)
                        {
                            result.Metadata.HeadExtractSec = 300;
                            result.Metadata.TailExtractSec = totalSec - 300;
                        }
                        else if (totalSec >= 300)
                        {
                            result.Metadata.HeadExtractSec = 200;
                            result.Metadata.TailExtractSec = totalSec - 200;
                        }
                        else if (totalSec >= 200)
                        {
                            result.Metadata.HeadExtractSec = 100;
                            result.Metadata.TailExtractSec = totalSec - 100;
                        }
                        else if (totalSec >= 100)
                        {
                            result.Metadata.HeadExtractSec = 60;
                            result.Metadata.TailExtractSec = totalSec - 60;
                        }
                        else
                        {
                            result.Metadata.HeadExtractSec = Math.Ceiling(totalSec * 0.35);
                            result.Metadata.TailExtractSec = Math.Ceiling(totalSec * 0.65);
                        }

                        if (result.Metadata.TailExtractSec >= totalSec) result.Metadata.TailExtractSec = Math.Max(0, totalSec - 1);

                        if (cache.TryGetValue(result.ContentCacheKey, out var contentCached) && contentCached != null && contentCached.Fingerprints.Count > 0)
                        {
                            result.IsCacheHit = true;
                            result.CachedData = contentCached;
                        }
                    }

                    return result;
                }
                finally
                {
                    driveSemaphore?.Release();
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(headerBuffer);
            }
        }
    }

    public static class NativeHeaderParser
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ComputeFastHash64(ReadOnlySpan<byte> data)
        {
            ulong hash = 14695981039346656037UL;
            if (data.Length == 0) return hash;

            ref byte ptr = ref MemoryMarshal.GetReference(data);
            int length = data.Length;
            int i = 0;

            for (; i <= length - 8; i += 8)
            {
                hash ^= Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref ptr, i));
                hash *= 1099511628211UL;
            }

            for (; i < length; i++)
            {
                hash ^= Unsafe.Add(ref ptr, i);
                hash *= 1099511628211UL;
            }

            return hash;
        }

        public static bool TryParseMp4(ReadOnlySpan<byte> buffer, out double durationSec, out int width, out int height)
        {
            durationSec = 0; width = 0; height = 0;
            try
            {
                int offset = 0;
                int limit = buffer.Length;
                ReadOnlySpan<byte> moovBuffer = default;

                while (offset + 8 <= limit)
                {
                    uint size = (uint)((buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3]);
                    if (size < 8) break; 

                    ReadOnlySpan<byte> typeSpan = buffer.Slice(offset + 4, 4);
                    bool isMoov = typeSpan[0] == 0x6D && typeSpan[1] == 0x6F && typeSpan[2] == 0x6F && typeSpan[3] == 0x76; 
                    bool isMdat = typeSpan[0] == 0x6D && typeSpan[1] == 0x64 && typeSpan[2] == 0x61 && typeSpan[3] == 0x74; 

                    long actualSize = size;
                    int headerSize = 8;
                    if (size == 1 && offset + 16 <= limit)
                    {
                        actualSize = (long)(((ulong)buffer[offset + 8] << 56) |
                                            ((ulong)buffer[offset + 9] << 48) |
                                            ((ulong)buffer[offset + 10] << 40) |
                                            ((ulong)buffer[offset + 11] << 32) |
                                            ((ulong)buffer[offset + 12] << 24) |
                                            ((ulong)buffer[offset + 13] << 16) |
                                            ((ulong)buffer[offset + 14] << 8) |
                                            (ulong)buffer[offset + 15]);
                        headerSize = 16;
                    }

                    if (isMoov)
                    {
                        int moovContentSize = (int)(Math.Min(actualSize, limit - offset) - headerSize);
                        if (moovContentSize > 0)
                        {
                            moovBuffer = buffer.Slice(offset + headerSize, moovContentSize);
                        }
                        break;
                    }

                    if (isMdat)
                    {
                        if (offset + actualSize > limit) break; 
                    }

                    offset += (int)actualSize;
                }

                if (moovBuffer.Length > 0)
                {
                    ReadOnlySpan<byte> mvhdPattern = stackalloc byte[] { 0x6d, 0x76, 0x68, 0x64 };
                    int mvhdIdx = moovBuffer.IndexOf(mvhdPattern); 
                    if (mvhdIdx >= 0 && mvhdIdx + 36 < moovBuffer.Length)
                    {
                        byte mvhdVer = moovBuffer[mvhdIdx + 4]; 
                        uint timescale = 0;
                        double duration = 0;
                        if (mvhdVer == 0)
                        {
                            timescale = ReadUInt32BE(moovBuffer, mvhdIdx + 16);
                            duration = ReadUInt32BE(moovBuffer, mvhdIdx + 20);
                        }
                        else if (mvhdVer == 1 && mvhdIdx + 44 < moovBuffer.Length)
                        {
                            timescale = ReadUInt32BE(moovBuffer, mvhdIdx + 24);
                            duration = ReadUInt64BE(moovBuffer, mvhdIdx + 28);
                        }

                        if (timescale > 0)
                        {
                            durationSec = duration / timescale;
                        }
                    }

                    ReadOnlySpan<byte> tkhdPattern = stackalloc byte[] { 0x74, 0x6b, 0x68, 0x64 };
                    int tkhdIdx = moovBuffer.IndexOf(tkhdPattern); 
                    if (tkhdIdx >= 0)
                    {
                        byte tkhdVer = moovBuffer[tkhdIdx + 4]; 
                        if (tkhdVer == 0 && tkhdIdx + 84 < moovBuffer.Length)
                        {
                            width = ReadUInt16BE(moovBuffer, tkhdIdx + 76);
                            height = ReadUInt16BE(moovBuffer, tkhdIdx + 80);
                        }
                        else if (tkhdVer == 1 && tkhdIdx + 96 < moovBuffer.Length)
                        {
                            width = ReadUInt16BE(moovBuffer, tkhdIdx + 88);
                            height = ReadUInt16BE(moovBuffer, tkhdIdx + 92);
                        }
                    }

                    return durationSec > 0;
                }

                ReadOnlySpan<byte> fallbackMvhdPattern = stackalloc byte[] { 0x6d, 0x76, 0x68, 0x64 };
                int fallbackMvhdIdx = buffer.IndexOf(fallbackMvhdPattern); 
                if (fallbackMvhdIdx >= 0 && fallbackMvhdIdx + 36 < buffer.Length)
                {
                    byte fallbackMvhdVer = buffer[fallbackMvhdIdx + 4]; 
                    uint timescale = 0;
                    double duration = 0;
                    if (fallbackMvhdVer == 0)
                    {
                        timescale = ReadUInt32BE(buffer, fallbackMvhdIdx + 16);
                        duration = ReadUInt32BE(buffer, fallbackMvhdIdx + 20);
                    }
                    else if (fallbackMvhdVer == 1 && fallbackMvhdIdx + 44 < buffer.Length)
                    {
                        timescale = ReadUInt32BE(buffer, fallbackMvhdIdx + 24);
                        duration = ReadUInt64BE(buffer, fallbackMvhdIdx + 28);
                    }

                    if (timescale > 0)
                    {
                        durationSec = duration / timescale;
                    }

                    ReadOnlySpan<byte> fallbackTkhdPattern = stackalloc byte[] { 0x74, 0x6b, 0x68, 0x64 };
                    int fallbackTkhdIdx = buffer.IndexOf(fallbackTkhdPattern); 
                    if (fallbackTkhdIdx >= 0)
                    {
                        byte fallbackTkhdVer = buffer[fallbackTkhdIdx + 4]; 
                        if (fallbackTkhdVer == 0 && fallbackTkhdIdx + 84 < buffer.Length)
                        {
                            width = ReadUInt16BE(buffer, fallbackTkhdIdx + 76);
                            height = ReadUInt16BE(buffer, fallbackTkhdIdx + 80);
                        }
                        else if (fallbackTkhdVer == 1 && fallbackTkhdIdx + 96 < buffer.Length)
                        {
                            width = ReadUInt16BE(buffer, fallbackTkhdIdx + 88);
                            height = ReadUInt16BE(buffer, fallbackTkhdIdx + 92);
                        }
                    }

                    return durationSec > 0;
                }
            }
            catch { }
            return false;
        }

        public static bool TryParseMkv(ReadOnlySpan<byte> buffer, out double durationSec, out int width, out int height)
        {
            durationSec = 0; width = 0; height = 0;
            try
            {
                double timecodeScale = 1000000.0;
                
                ReadOnlySpan<byte> tcPattern = stackalloc byte[] { 0x2A, 0xD7, 0xB1 };
                int tcIdx = buffer.IndexOf(tcPattern); 
                if (tcIdx >= 0 && tcIdx + 4 < buffer.Length)
                {
                    byte lenByte = buffer[tcIdx + 3];
                    int len = lenByte & 0x0F;
                    if (tcIdx + 4 + len <= buffer.Length)
                    {
                        ulong tcVal = 0;
                        for (int i = 0; i < len; i++)
                        {
                            tcVal = (tcVal << 8) | buffer[tcIdx + 4 + i];
                        }
                        if (tcVal > 0) timecodeScale = tcVal;
                    }
                }

                ReadOnlySpan<byte> durPattern = stackalloc byte[] { 0x44, 0x89 };
                int durIdx = buffer.IndexOf(durPattern); 
                if (durIdx >= 0 && durIdx + 6 < buffer.Length)
                {
                    byte lenByte = buffer[durIdx + 2];
                    if (lenByte == 0x84 && durIdx + 7 <= buffer.Length)
                    {
                        uint raw = ReadUInt32BE(buffer, durIdx + 3);
                        float val = Int32BitsToSingle((int)raw);
                        durationSec = (val * timecodeScale) / 1000000000.0;
                    }
                    else if (lenByte == 0x88 && durIdx + 11 <= buffer.Length)
                    {
                        ulong raw = ReadUInt64BE(buffer, durIdx + 3);
                        double val = Int64BitsToDouble((long)raw);
                        durationSec = (val * timecodeScale) / 1000000000.0;
                    }
                }

                ReadOnlySpan<byte> wPattern = stackalloc byte[] { 0xB0 };
                int wIdx = buffer.IndexOf(wPattern); 
                if (wIdx >= 0 && wIdx + 2 < buffer.Length)
                {
                    byte lenByte = buffer[wIdx + 1];
                    int len = lenByte & 0x0F;
                    if (wIdx + 2 + len <= buffer.Length)
                    {
                        int wVal = 0;
                        for (int i = 0; i < len; i++) wVal = (wVal << 8) | buffer[wIdx + 2 + i];
                        width = wVal;
                    }
                }

                ReadOnlySpan<byte> hPattern = stackalloc byte[] { 0xBA };
                int hIdx = buffer.IndexOf(hPattern); 
                if (hIdx >= 0 && hIdx + 2 < buffer.Length)
                {
                    byte lenByte = buffer[hIdx + 1];
                    int len = lenByte & 0x0F;
                    if (hIdx + 2 + len <= buffer.Length)
                    {
                        int hVal = 0;
                        for (int i = 0; i < len; i++) hVal = (hVal << 8) | buffer[hIdx + 2 + i];
                        height = hVal;
                    }
                }

                return durationSec > 0;
            }
            catch { return false; }
        }

        private static ushort ReadUInt16BE(ReadOnlySpan<byte> buffer, int offset)
        {
            return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
        }

        private static uint ReadUInt32BE(ReadOnlySpan<byte> buffer, int offset)
        {
            return (uint)((buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3]);
        }

        private static ulong ReadUInt64BE(ReadOnlySpan<byte> buffer, int offset)
        {
            return ((ulong)buffer[offset] << 56) |
                   ((ulong)buffer[offset + 1] << 48) |
                   ((ulong)buffer[offset + 2] << 40) |
                   ((ulong)buffer[offset + 3] << 32) |
                   ((ulong)buffer[offset + 4] << 24) |
                   ((ulong)buffer[offset + 5] << 16) |
                   ((ulong)buffer[offset + 6] << 8) |
                   (ulong)buffer[offset + 7];
        }

        private static float Int32BitsToSingle(int value)
        {
            return BitConverter.Int32BitsToSingle(value);
        }

        private static double Int64BitsToDouble(long value)
        {
            return BitConverter.Int64BitsToDouble(value);
        }
    }
}