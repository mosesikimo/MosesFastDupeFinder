using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.WindowsAPICodePack.Shell;

namespace FastDupeFinder
{
    public static class FFmpegHelper
    {
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
            try { process.Start(); await process.WaitForExitAsync(ct); return process.ExitCode == 0; }
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
            if (string.IsNullOrEmpty(ffmpegPath)) return null;

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
                _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cts.Token);

                using var reader = process.StandardError;
                string? line;
                while ((line = await reader.ReadLineAsync(cts.Token)) != null)
                {
                    if (line.TrimStart().StartsWith("Duration:"))
                    {
                        var parts = line.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
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

        public static async Task ExtractCombinedFingerprintsAsync(DupeFileItem item, double headSec, double tailSec, CancellationToken ct, int ffmpegTimeoutMs)
        {
            string rwTimeout = "3000000";
            string args = $"-rw_timeout {rwTimeout} -v quiet -noaccurate_seek -threads 1 " +
               $"-ss {headSec} -i \"{item.FilePath}\" -ss {tailSec} -i \"{item.FilePath}\" " +
               $"-filter_complex \"[0:v]scale=8:8,setsar=1,format=gray[l];[1:v]scale=8:8,setsar=1,format=gray[r];[l][r]hstack,format=gray\" " +
               $"-frames:v 1 -f rawvideo pipe:1";

            byte[]? imageBytes = await RunCommandAndGetOutputAsync(args, ffmpegTimeoutMs, ct);
            if (imageBytes != null && imageBytes.Length == 128)
            {
                if (ScanEngine.CalculateCombinedHashVariantsFromMemory(imageBytes, out var headVariants, out var tailVariants))
                {
                    lock (item.Fingerprints)
                    {
                        if (headVariants != null) item.Fingerprints.Add(new VideoFingerprint(headVariants, headSec, FingerprintPosition.Head));
                        if (tailVariants != null) item.Fingerprints.Add(new VideoFingerprint(tailVariants, tailSec, FingerprintPosition.Tail));
                    }
                    return;
                }
            }

            string fallbackArgs = $"-rw_timeout {rwTimeout} -v quiet -noaccurate_seek -threads 1 " +
               $"-ss {headSec} -i \"{item.FilePath}\" -vf \"scale=8:8,setsar=1,format=gray\" -frames:v 1 -f rawvideo pipe:1";

            byte[]? headOnlyBytes = await RunCommandAndGetOutputAsync(fallbackArgs, ffmpegTimeoutMs, ct);
            if (headOnlyBytes != null && headOnlyBytes.Length == 64)
            {
                if (ScanEngine.CalculateSingleHashVariantsFromMemory(headOnlyBytes, out var headOnlyVariants))
                {
                    lock (item.Fingerprints)
                    {
                        if (headOnlyVariants != null) item.Fingerprints.Add(new VideoFingerprint(headOnlyVariants, headSec, FingerprintPosition.Head));
                    }
                }
            }
        }

        public static async Task<byte[]?> GenerateDualPreviewThumbnailBytesAsync(string videoPath, double headSec, double tailSec)
        {
            if (headSec < 0 && tailSec < 0) return null;
            if (headSec >= 0 && tailSec < 0) return await GenerateHighResPreviewThumbnailBytesAsync(videoPath, headSec);
            if (headSec < 0 && tailSec >= 0) return await GenerateHighResPreviewThumbnailBytesAsync(videoPath, tailSec);

            string args = $"-rw_timeout 3000000 -v quiet -ss {headSec} -i \"{videoPath}\" -ss {tailSec} -i \"{videoPath}\" " +
                        $"-filter_complex \"[0:v]scale=320:-2,setdar=16/9[l];[1:v]scale=320:-2,setdar=16/9[r];[l][r]vstack\" " +
                        $"-vframes 1 -q:v 2 -f image2 -c:v mjpeg pipe:1";

            return await RunCommandAndGetOutputAsync(args, 5000, CancellationToken.None);
        }

        public static async Task<byte[]?> GenerateHighResPreviewThumbnailBytesAsync(string videoPath, double seconds)
        {
            string args = $"-rw_timeout 3000000 -v quiet -ss {seconds} -i \"{videoPath}\" -vframes 1 -q:v 2 -f image2 -c:v mjpeg pipe:1";
            return await RunCommandAndGetOutputAsync(args, 5000, CancellationToken.None);
        }
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

        public static bool IsFileBadOrDownloading(string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                long length = fs.Length;
                if (length < 1024 * 1024) return false;

                byte[] buffer = new byte[8192];
                fs.Seek(-8192, SeekOrigin.End);
                int tailRead = fs.Read(buffer, 0, 8192);
                if (tailRead > 0)
                {
                    bool allZeros = true;
                    for (int i = 0; i < tailRead; i++)
                    {
                        if (buffer[i] != 0) { allZeros = false; break; }
                    }
                    if (allZeros) return true;
                }
                return false;
            }
            catch { return true; }
        }

        public static bool IsFileInUse(string filePath)
        {
            try { using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read); return false; }
            catch { return true; }
        }

        public static string GetFileHeaderHash(string filePath)
        {
            for (int i = 0; i < 4; i++)
            {
                try
                {
                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    byte[] buffer = new byte[8192];
                    int totalRead = 0;
                    while (totalRead < buffer.Length)
                    {
                        int bytesRead = fs.Read(buffer, totalRead, buffer.Length - totalRead);
                        if (bytesRead == 0) break;
                        totalRead += bytesRead;
                    }
                    using var md5 = MD5.Create();
                    return BitConverter.ToString(md5.ComputeHash(buffer, 0, totalRead)).Replace("-", "");
                }
                catch { if (i < 3) Thread.Sleep(250); }
            }
            return "ERR_" + Guid.NewGuid().ToString("N");
        }

        public static async Task<DupeFileItem?> ExtractMetadataAsync(string filePath, CancellationToken ct, bool skipFfmpegFallback = false)
        {
            long fileSize = 0;
            try { fileSize = new FileInfo(filePath).Length; } catch { return null; }

            TimeSpan? finalDuration = null;
            uint? width = 0, height = 0;

            for (int i = 0; i < 3; i++)
            {
                try
                {
                    using var shellFile = ShellFile.FromParsingName(filePath);
                    ulong? durationTicks = shellFile.Properties.System.Media.Duration.Value;
                    width = shellFile.Properties.System.Video.FrameWidth.Value;
                    height = shellFile.Properties.System.Video.FrameHeight.Value;

                    if (durationTicks.HasValue && durationTicks.Value > 0)
                        finalDuration = TimeSpan.FromTicks((long)durationTicks.Value);
                    break;
                }
                catch { if (i < 2) await Task.Delay(200, ct); }
            }

            if (!finalDuration.HasValue || finalDuration.Value.TotalSeconds <= 0)
            {
                if (!skipFfmpegFallback) finalDuration = await FFmpegHelper.GetDurationAsync(filePath, ct);
                else finalDuration = TimeSpan.FromSeconds(0.1);
            }

            if (finalDuration.HasValue && finalDuration.Value.TotalSeconds > 0)
            {
                return new DupeFileItem
                {
                    FilePath = filePath,
                    Size = fileSize,
                    Duration = finalDuration.Value,
                    Width = (int)(width ?? 0),
                    Height = (int)(height ?? 0),
                    Resolution = (width > 0 && height > 0) ? $"{width}x{height}" : I18nManager.Instance.GetString("ResUnknown", "未知"),
                };
            }

            return null;
        }
    }
}