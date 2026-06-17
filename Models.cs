using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace FastDupeFinder
{
    public static class AppConstants
    {
        public static readonly string[] SupportedVideoExts = {
            ".mp4", ".mkv", ".avi", ".wmv", ".mov", ".ts", ".m2ts",
            ".mts", ".vob", ".mpg", ".mpeg", ".m4v", ".3gp", ".divx", 
            ".flv", ".rmvb", ".rm", ".webm", ".asf", ".m2v",
            ".mpe", ".mpv", ".mp2", ".ogv", ".qt", ".f4v",  ".amv", ".trp"
        };

        public static readonly string[] LegacyVideoExts = {
            ".vob", ".avi", ".3gp", ".wmv", ".ts", ".m2ts", ".mts", 
            ".mpg", ".mpeg", ".divx", ".flv", ".rmvb", ".rm", ".mov", ".asf", ".m2v"
        };
    }

    public struct VideoFileInfo
    {
        public string Path;
        public long Length;
        public long LastWriteTimeTicks; 
    }

    public class PipelineConfig
    {
        public int Cpu { get; }
        public bool IsSsd { get; }
        public int MetadataBuffer { get; }
        public int FfmpegIoLimit { get; }
        public int PipelineSlots { get; }

        public PipelineConfig()
        {
            Cpu = Environment.ProcessorCount;
            IsSsd = false; 
            
            MetadataBuffer = 100;
            FfmpegIoLimit = 3; 
            PipelineSlots = 6; 
        }

        public override string ToString() => $"CPU:{Cpu} | HDD 模式 | FFmpeg 併發:{FfmpegIoLimit}";
    }

    public enum FingerprintPosition { Unknown, Head, Tail }

    public class VideoFingerprint
    {
        public List<ulong[]> Hashes { get; set; }
        public double TimeSec { get; set; }
        public FingerprintPosition Position { get; set; }

        public VideoFingerprint(List<ulong[]> hashes, double timeSec, FingerprintPosition position)
        {
            Hashes = hashes;
            TimeSec = timeSec;
            Position = position;
        }
    }

    public class CachedVideoData
    {
        public double DurationSec { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool IsBadFile { get; set; } 
        public List<VideoFingerprint> Fingerprints { get; set; } = new();
        // 💡 新增：儲存全畫面特徵，專供 RegroupStrict 拆解大群組使用
        public List<VideoFingerprint> FullFingerprints { get; set; } = new();
    }

    public class DupeFileItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }

        private bool _isReference;
        private string _errorMessage = "";
        private bool _isFailed;
        private bool _isUpgrading;
        private bool _isRepairing;
        private bool _isFormatUpgradeNeeded;

        public bool IsReference
        {
            get => _isReference;
            set { if (_isReference != value) { _isReference = value; OnPropertyChanged(); OnPropertyChanged(nameof(SimilarityStr)); } }
        }

        public bool IsFailed
        {
            get => _isFailed;
            set { if (_isFailed != value) { _isFailed = value; OnPropertyChanged(); OnPropertyChanged(nameof(SimilarityStr)); } }
        }

        public bool IsUpgrading
        {
            get => _isUpgrading;
            set { if (_isUpgrading != value) { _isUpgrading = value; OnPropertyChanged(); } }
        }

        public bool IsRepairing
        {
            get => _isRepairing;
            set { if (_isRepairing != value) { _isRepairing = value; OnPropertyChanged(); } }
        }

        public bool IsFormatUpgradeNeeded
        {
            get => _isFormatUpgradeNeeded;
            set { if (_isFormatUpgradeNeeded != value) { _isFormatUpgradeNeeded = value; OnPropertyChanged(); } }
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            set { if (_errorMessage != value) { _errorMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(SimilarityStr)); } }
        }

        public string MatchDetail { get; set; } = "";
        
        public string RowBackgroundColor { get; set; } = "#FFFFFF";

        public string CacheKey { get; set; } = "";
        public string ContentCacheKey { get; set; } = "";

        public bool HitCache { get; set; } = false;

        public string FilePath { get; set; } = "";
        public long Size { get; set; }
        public string SizeStr => $"{(Size / 1024.0 / 1024.0):F2} MB";
        public TimeSpan Duration { get; set; }
        public string DurationStr => Duration.ToString(@"hh\:mm\:ss");

        public double Bitrate => Duration.TotalSeconds > 0 ? Size / Duration.TotalSeconds : 0;
        public string BitrateKbpsStr => $"{(Bitrate / 1024.0 * 8):F0}";

        public string Resolution { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }

        public List<VideoFingerprint> Fingerprints { get; set; } = new();
        // 💡 記憶體物件同步支援雙層特徵
        public List<VideoFingerprint> FullFingerprints { get; set; } = new();

        public double HeadExtractSec { get; set; } = -1;
        public double TailExtractSec { get; set; } = -1;

        public int SimilarityScore { get; set; }
        
        public int OriginalGroupId { get; set; }

        public string SimilarityStr
        {
            get
            {
                if (IsFailed) return $"⚠️ {ErrorMessage}";
                if (IsReference) return I18nManager.Instance.GetString("TxtReference", "📍 基準檔案");
                return $"{SimilarityScore}% ({MatchDetail})";
            }
        }
    }

    public class AppSettings
    {
        public string TargetPaths { get; set; } = "";
        public string ExcludePaths { get; set; } = "";
        public bool IncludeSubDir { get; set; } = true;
        public string MinSize { get; set; } = "100";
        public string MinLength { get; set; } = "3";
    }
}