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
            ".mts", ".vob", ".mpg", ".mpeg", ".m4v", ".3gp", ".divx", ".flv", ".rmvb"
        };

        public static readonly string[] LegacyVideoExts = {
            ".vob", ".avi", ".3gp", ".wmv", ".ts", ".m2ts", ".mts", ".mpg", ".mpeg", ".divx", ".flv", ".rmvb", ".mov"
        };
    }

    public class PipelineConfig
    {
        public int Cpu { get; }
        // 💡 永遠將磁碟視為 HDD (傳統硬碟)，採取最穩定的 I/O 降載策略
        public bool IsSsd { get; } = false; 
        public long AvailableMemoryMb { get; }

        public PipelineConfig()
        {
            Cpu = Environment.ProcessorCount;
            AvailableMemoryMb = GC.GetTotalMemory(false) / (1024 * 1024);
        }

        public int FileBuffer
        {
            get
            {
                int base_val = Cpu * 500;
                if (AvailableMemoryMb < 2048) return Cpu * 200;
                if (AvailableMemoryMb < 4096) return Cpu * 300;
                return base_val;
            }
        }

        // 💡 HDD 優化：Metadata 解析雖然很快，但也不能瞬間全開導致磁頭亂飛
        public int MetadataBuffer => (AvailableMemoryMb < 2048) ? Cpu * 30 : Cpu * 60;
        public int MetadataParallelism => Math.Min(Cpu * 2, 8); 

        public int PipelineSlots => 2000;
        
        // 💡 HDD 優化：將 FFmpeg 同時讀取的數量嚴格限制在 2~3 個，保護磁碟壽命並最大化循序讀取效益
        public int FfmpegIoLimit => Math.Min(3, Math.Max(2, Cpu / 4));

        public override string ToString() => $"[Pipeline] CPU:{Cpu} | FileBuffer:{FileBuffer} | MetaBuffer:{MetadataBuffer} | PipelineSlots:{PipelineSlots} | FfmpegIO:{FfmpegIoLimit} | IsHDD:True";
    }

    public class AppSettings
    {
        public string TargetPaths { get; set; } = "";
        public string ExcludePaths { get; set; } = "";
        public bool IncludeSubDir { get; set; } = true;
        public string MinSize { get; set; } = "100";
        public string MinLength { get; set; } = "3";
    }

    public class DupeFileItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null!)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public string CacheKey { get; set; } = ""; 

        private bool _isFailed = false;
        public bool IsFailed { get => _isFailed; set { _isFailed = value; OnPropertyChanged(); OnPropertyChanged(nameof(SimilarityStr)); } }

        private string _errorMessage = "";
        public string ErrorMessage { get => _errorMessage; set { _errorMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(SimilarityStr)); } }

        private bool _isRepairing = false;
        public bool IsRepairing { get => _isRepairing; set { _isRepairing = value; OnPropertyChanged(); } }

        private bool _isUpgrading = false;
        public bool IsUpgrading { get => _isUpgrading; set { _isUpgrading = value; OnPropertyChanged(); } }

        private bool _isFormatUpgradeNeeded = false;
        public bool IsFormatUpgradeNeeded { get => _isFormatUpgradeNeeded; set { _isFormatUpgradeNeeded = value; OnPropertyChanged(); } }

        private string _rowBackgroundColor = "Transparent";
        public string RowBackgroundColor { get => _rowBackgroundColor; set { _rowBackgroundColor = value; OnPropertyChanged(); } }

        private string _matchDetail = "";
        public string MatchDetail { get => _matchDetail; set { _matchDetail = value; OnPropertyChanged(); OnPropertyChanged(nameof(SimilarityStr)); } }

        private bool _isReference = false;
        public bool IsReference { get => _isReference; set { _isReference = value; OnPropertyChanged(); OnPropertyChanged(nameof(SimilarityStr)); } }

        public bool HitCache { get; set; } = false;

        public double HeadMatchTime { get; set; } = -1;
        public double TailMatchTime { get; set; } = -1;
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
        public double MatchSeconds { get; set; } = -1;

        public int SimilarityScore { get; set; }
        public int OriginalGroupId { get; set; }
        public int GroupId { get; set; }

        public string SimilarityStr
        {
            get
            {
                if (IsFailed) return $"⚠️ {ErrorMessage}";
                if (IsReference) return I18nManager.Instance.GetString("TxtRefGroup", $"[群組 {GroupId}] ⭐ 基準檔").Replace("{GroupId}", GroupId.ToString());
                return $"[{I18nManager.Instance.GetString("TxtGroup", "群組")} {GroupId}] {MatchDetail}";
            }
        }
    }

    public enum FingerprintPosition { Head, Tail, Deep, Unknown }

    public class VideoFingerprint
    {
        public List<ulong[]> Hashes { get; set; }
        
        public double TimeSec { get; set; }
        public FingerprintPosition Position { get; set; }

        public VideoFingerprint(List<ulong[]> hashes, double timeSec, FingerprintPosition position = FingerprintPosition.Unknown)
        {
            Hashes = hashes;
            TimeSec = timeSec;
            Position = position;
        }
    }
}