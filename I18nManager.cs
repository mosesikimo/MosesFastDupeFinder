using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Markup;

namespace FastDupeFinder
{
    public class LocExtension : MarkupExtension
    {
        public string Key { get; set; }
        public string DefaultValue { get; set; }

        public LocExtension() { Key = ""; DefaultValue = ""; }

        public LocExtension(string key, string defaultValue)
        {
            Key = key;
            DefaultValue = defaultValue;
        }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            return new Binding($"[{Key}]")
            {
                Source = I18nManager.Instance,
                Mode = BindingMode.OneWay,
                TargetNullValue = DefaultValue,
                FallbackValue = DefaultValue
            }.ProvideValue(serviceProvider);
        }
    }

    public class I18nManager : INotifyPropertyChanged
    {
        public static I18nManager Instance { get; } = new I18nManager();
        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action? LanguageChanged;

        private Dictionary<string, string> _dict = new();
        private Dictionary<string, string> _templateDict = new();
        public string CurrentLanguage { get; private set; } = "zh-TW";

        public const string DefaultAboutContent = """
        🚀 FastDupeFinder —— 極速影片重複尋找工具

        「警告：本軟體沒有華麗的轉場動畫，因為作者的技能樹全點在『底層架構』跟『極限效能』上了。」

        如果你在找一個有著絢麗半透明視窗、各種酷炫特效的影片管理軟體，那你可能走錯棚了。

        但如果你需要一個比對速度極快、處理海量大檔不卡死、遇到壞檔不崩潰，而且絕對不會因為 Bug 誤刪你珍貴收藏的工業級檢索神器——那你找對人了。

        無論是累積多年的影音素材、或是散落各處的備份檔案，這套工具都能在轉瞬之間，為你清理出寶貴的硬碟空間。

        ⚙️ 系統需求與 FFmpeg (重要)
        
        為了保持核心極致輕量，本軟體的「影像特徵擷取」與「無損斷尾修復」高度依賴開源的 `ffmpeg.exe`。
        如果您是直接下載原始碼編譯，請務必自行前往 FFmpeg 官網 (https://ffmpeg.org/download.html) 下載 Windows 版本的 `ffmpeg.exe`，並將其與本程式 (`FastDupeFinder.exe`) 放置於同一個資料夾中。
        (若是下載發布版 zip，通常已經內含)

        🤝 聯繫作者

        若本工具為你省下了時間，或想交流系統架構與技術，歡迎隨時聯繫！
        • 開發者：渡川 Moses
        • GitHub：https://github.com/mosesikimo
        • Email：mosesikimo@gmail.com
        • Line ID：mosesikimo
        • 座右銘：程式碼說邏輯，我渡思想過川。

        🧑‍💻 關於作者：拿管工廠的牛刀，來殺你硬碟裡的重複影片

        「我是渡川 Moses，一位擺渡思想的軟體人。」

        我在 IT 與 OT 跨域整合打滾了 21 年，專精系統架構與軟硬體整合，近期正著手探索 AI Agent。
        我的日常工作不是畫美美的 UI，而是把企業核心的 ERP、PLM 系統，以及數控機台與品保量測儀器全部綁在一起。

        「檔案去重複化」，本質上就是一場極限的數據比對與 I/O 戰爭。

        我的核心戰場，在於嚴格的流程狀態管理與 LEAN 精實管理，主導過無數複雜的工單、品保與 NC 版控系統；
        同時深耕 PLM 與研發量產成本管理，從現場物料節省、備料優化，一路到錙銖必較的節費機制。

        早在 20 年前，我就曾為兩座跨國工廠操刀過「資料庫跨廠即時同步」的底層架構；
        面對海量實體檔案的備援，我也深知如何拿捏效能與系統負載的平衡——精準排程每日三次的巨量檔案無損對接，絕不盲目耗損網路頻寬。

        這些「一旦當機、資料不同步，整廠就會停擺、損失千萬」的巨型商業邏輯，造就了我對系統穩定性的極度苛求。

        現在，我把這種從異地備援與嚴苛流程防呆中淬鍊出來的偏執，帶到了這個小巧的影片整理程式裡。
        我把你的影片當作「跨國交易」在保護——比對不容絲毫誤差，不該發生的意外，連發生的按鈕都沒有。

        我相信「程式碼會說話」，更相信好的技術，應當像一條靜水流深的河——表面從容，內裡有力，能載人渡過難關。

        這套軟體可能沒有最漂亮的皮囊，但它有一顆經過 21 年企業戰場千錘百鍊的強大心臟。
        把你的硬碟交給它，喝杯咖啡，感受一下什麼叫做「絕對精準的數據清理」。

        ✨ 核心技術亮點：降維打擊的底層引擎

        多數工程師寫的是單一功能，而我習慣建造自動防呆、精準比對的底層引擎。
        當這個程式在掃描你那塞滿幾 TB 影片的硬碟時，背後運作的是企業級的併發控制邏輯：

        🏎️ 獨家極速管線
        採用 C# Channels 雙層限流與高併發架構。完美榨乾現代多核心 CPU 與 SSD 的極限，絕不吃光記憶體，更不會跑到一半卡死。

        🧠 智慧特徵快取
        指紋掃描過一次即永久記憶。無視檔案被改得面目全非或隨意搬移，第二次掃描直接觸發「快取秒殺」。

        🛡️ 斷尾容錯與無損修復
        內建「頭部降級容錯」與「毒藥快取」機制。遇到壞檔自動跳過防卡死；更支援「無損斷尾修復」，一鍵拯救未下載完整的殘缺影片。

        ⚡ 零延遲標頭讀取
        結合 Windows 原生 API 與 FFmpeg 雙重掃描救援，連老舊或損壞的影片標頭都能瞬間洞察。

        ⚖️ 開源聲明

        底層影像特徵擷取引用開源工具 FFmpeg (GNU LGPL/GPL)。
        Developed with ❤️ by 渡川 Moses
        """;

        private const string EnglishAboutContent = """
        🚀 FastDupeFinder —— Ultra-Fast Duplicate Video Finder

        "Warning: This software has no fancy transition animations because the author put all the skill points into 'underlying architecture' and 'extreme performance'."

        If you are looking for a video management software with gorgeous translucent windows and cool effects, you might be in the wrong place.

        But if you need an industrial-grade retrieval artifact with blazing fast comparison speed, that won't freeze when handling massive large files, won't crash on corrupted files, and definitely won't accidentally delete your precious collections due to bugs — you've found the right one.

        Whether it's media materials accumulated over the years, or backup files scattered everywhere, this tool can clean up valuable hard drive space for you in the blink of an eye.

        ⚙️ System Requirements & FFmpeg (Important)
        
        To keep the core extremely lightweight, the "Video Feature Extraction" and "Lossless Tail Repair" heavily rely on the open-source `ffmpeg.exe`.
        If you compile from source code, please download the Windows version of `ffmpeg.exe` from the official FFmpeg website (https://ffmpeg.org/download.html) and place it in the same folder as this program (`FastDupeFinder.exe`).
        (If you downloaded the release zip, it is usually included.)

        🤝 Contact the Author

        If this tool saved you time, or if you want to discuss system architecture and technology, feel free to contact me!
        • Developer: Moses
        • GitHub: https://github.com/mosesikimo
        • Email: mosesikimo@gmail.com
        • Line ID: mosesikimo
        • Motto: Code speaks logic, I ferry thoughts across the river.

        🧑‍💻 About the Author: Using an enterprise factory-grade sledgehammer to clear out your duplicate videos

        "I am Moses, a software engineer who ferries thoughts."

        I have been rolling in IT and OT cross-domain integration for 21 years, specializing in system architecture and software-hardware integration, and recently exploring AI Agents.
        My daily work isn't drawing pretty UIs, but tying enterprise core ERP, PLM systems, CNC machines, and QA measurement instruments all together.

        "Data deduplication" is essentially an extreme data comparison and I/O war.

        My core battlefield lies in strict process state management and LEAN management, having led countless complex work orders, QA, and NC version control systems;
        At the same time, I deeply cultivate PLM and R&D mass production cost management, from on-site material saving, material preparation optimization, all the way to meticulous cost-saving mechanisms.

        As early as 20 years ago, I orchestrated the underlying architecture of "real-time database synchronization across factories" for two multinational factories;
        Facing the backup of massive physical files, I also know how to balance performance and system load — precisely scheduling lossless docking of massive files three times a day, absolutely not blindly consuming network bandwidth.

        These massive business logics of "once crashed, data out of sync, the whole factory will stop, losing millions" have forged my extreme paranoia for system stability.

        Now, I brought this paranoia, tempered from remote backup and strict process error-proofing, into this compact video organization program.
        I protect your videos like "multinational transactions" — comparison allows no error, unexpected accidents shouldn't happen, not even the button to trigger them exists.

        I believe "code speaks", and I believe even more that good technology should be like a deep, quiet river — calm on the surface, powerful inside, capable of carrying people across difficulties.

        This software might not have the prettiest skin, but it has a strong heart tempered through 21 years in the enterprise battlefield.
        Hand your hard drive over to it, grab a cup of coffee, and experience what "absolutely precise data cleanup" means.

        ✨ Core Technology Highlights: Dimensional Strike Underlying Engine

        Most engineers write single functions, but I am used to building automatic error-proofing, precisely comparing underlying engines.
        When this program is scanning your hard drive filled with TBs of videos, what operates behind the scenes is an enterprise-grade concurrency control logic:

        🏎️ Exclusive Ultra-Fast Pipeline
        Adopts C# Channels dual-tier throttling and high concurrency architecture. Perfectly squeezes the limits of modern multi-core CPUs and SSDs, absolutely won't eat up memory, and definitely won't freeze halfway.

        🧠 Smart Feature Cache
        Fingerprints are permanently remembered once scanned. Ignores files being altered beyond recognition or moved randomly, second scan triggers "cache instant kill".

        🛡️ Tail Truncation Fault Tolerance & Lossless Repair
        Built-in "head downgrade fault tolerance" and "poison cache" mechanisms. Encounters bad files and auto-skips to prevent freezing; also supports "lossless tail truncation repair", one-click rescue for incompletely downloaded broken videos.

        ⚡ Zero Latency Header Reading
        Combines Windows native API and FFmpeg dual scan rescue, even old or corrupted video headers can be instantly insighted.

        ⚖️ Open Source Declaration

        Underlying video feature extraction uses the open-source tool FFmpeg (GNU LGPL/GPL).
        Developed with ❤️ by Moses
        """;

        private readonly Dictionary<string, string> _enDict = new()
        {
            {"WindowTitle", "Moses - Fast Duplicate Video Finder"},
            {"ConfigHeader", "Scan Configuration"},
            {"LblPath", "Target Paths:"},
            {"TipPath", "Multiple paths separated by semicolon (;)"},
            {"BtnAdd", "Add..."},
            {"BtnClear", "Clear"},
            {"ChkSubDir", "Include Subdirectories"},
            {"LblMinSize", "Min Size (MB):"},
            {"LblMinLen", "Min Length (min):"},
            {"LblExclude", "Exclude Paths:"},
            {"TipExclude", "Paths to ignore (e.g. C:\\Windows)"},
            {"BtnAddExclude", "Add Exclude"},
            {"BtnScan", "🔍 Start Scan"},
            {"TipScan", "Cross-compare video fingerprints to find 100% exact matches"},
            {"BtnAbout", "ℹ️ About"},
            {"TipAbout", "View software info and author details"},
            {"BtnToggleLang", "🌐 切換繁體"},
            {"TipToggleLang", "切換介面語言 / Change Language"},
            {"BtnDeleteSelected", "🗑️ Delete Selected"},
            {"MenuOpenDir", "📂 Open File Location"},
            {"ColSim", "Similarity"},
            {"ColSize", "Size"},
            {"ColDuration", "Duration"},
            {"ColBitrate", "Bitrate(Kbps)"},
            {"ColRes", "Resolution"},
            {"ColPath", "File Path"},
            {"TxtNoFile", "No file selected"},
            {"BtnRepair", "🛠️ Repair File"},
            {"TipRepair", "Attempt to truncate damaged tail and repair file (allowable loss < 5%)"},
            {"BtnUpgrade", "✨ Upgrade Format"},
            {"TipUpgrade", "Auto convert legacy formats. Priority: fast stream copy, fallback: re-encode."},
            {"BtnOpen", "Open File"},
            {"StatusReady", "✅ System ready. Waiting to scan."},
            {"MenuRepair", "🛠️ Attempt Repair (Loss < 5%)"},
            {"MenuUpgrade", "✨ Upgrade to modern MP4 (Remux/Re-encode)"},
            {"MenuDelete", "🗑️ Delete Selected File(s)"},
            {"MenuOpenFolder", "📂 Open File Location"},
            {"AboutContent", EnglishAboutContent},
            {"AboutTitle", "About FastDupeFinder & Author"},
            {"MsgSelectBadFile", "Please select a bad file marked in 'light red' to repair!\n(Normal files do not need repair)"},
            {"TitleHint", "Hint"},
            {"StatusTaskQueued", "⏳ Queued in background..."},
            {"StatusAddedToQueue", "📥 Added {0} file(s) to background queue."},
            {"StatusAddedToQueueSingle", "📥 File added to background queue."},
            {"StatusRepairingQueue", "🛠️ Repairing: {0}... ({1} left in queue)"},
            {"StatusUpgradingQueue", "✨ Upgrading: {0}... ({1} left in queue)"},
            {"StatusQueueDone", "✅ Background queue fully processed!"},
            {"StatusRepairingNow", "🛠️ Executing repair..."},
            {"StatusUpgradingNow", "✨ Executing format upgrade..."},
            {"StatusReencoding", "🔄 Remux failed, re-encoding: {0}..."},
            {"StatusRepairDone", "🛠️ Successfully truncated tail {0:F1}% (Repair complete)"},
            {"StatusUpgradeDone", "✨ Successfully upgraded to modern MP4 format"},
            {"ErrRepairLoss", "Aborted repair (Tail missing up to {0:F1}%)"},
            {"ErrRepairMeta", "Repair failed (Unable to parse repaired file)"},
            {"ErrUpgradeMeta", "Upgrade failed (Cannot parse output file)"},
            {"ErrRepairLocked", "Repair failed (File severely corrupted or locked)"},
            {"ErrUpgradeFailed", "Upgrade failed (File severely corrupted or unsupported)"},
            {"StatusRepairSummary", "✅ Processing complete! Successfully repaired {0}, abandoned {1} severely damaged files."},
            {"StatusRepairFailLock", "❌ Operation failed: File locked or cannot be overwritten."},
            {"StatusRepairDrop", "⚠️ Aborted repair: Loss too high ({0:F1}%), exceeds allowable 5%."},
            {"StatusStopping", "⏳ Stopping pipeline, please wait..."},
            {"BtnStopping", "🛑 Stopping..."},
            {"MsgMinLenErr", "'Min Length' must be set to at least 3 minutes!\nThis ensures insignificant short clips are not compared."},
            {"TitleSettingErr", "Settings Error"},
            {"BtnStopScan", "🛑 Stop Scan"},
            {"StatusScanning", "⏳ Starting dual-tier throttled pipeline scan..."},
            {"StatusAborted", "⏹️ Scan aborted manually by user."},
            {"TitleBusyErr", "System Busy / Error"},
            {"StatusScanErr", "❌ Error occurred during scan"},
            {"TextAborted", "aborted manually"},
            {"StatusNoResult", "ℹ️ No duplicate or damaged files found."},
            {"StatusRefreshed", "✅ View refreshed!"},
            {"StatusDupeFound", " Found {0} duplicate(s)."},
            {"StatusBadFound", " ⚠️ Listed {0} unparsable/downloading files for cleanup."},
            {"ProgStart", "⚡ Starting full-memory async pipeline (Config: "},
            {"ErrLocked", "Locked (Downloading/In Use)"},
            {"ErrHeaderBad", "Header corrupted (Cannot parse length)"},
            {"ErrLegacyFormat", "⚠️ Legacy format (Requires upgrade to parse)"},
            {"ProgFail", " | ⚠️ "},
            {"ProgTimeout", "timeout/damaged"},
            {"ProgLock", " | 🔒 "},
            {"ProgDl", "downloading"},
            {"ProgShort", " | ⏭️ "}, 
            {"ProgSkipShort", "Too Short (Skipped)"}, 
            {"ProgStats", "Searched: {0} | Parsed: {1} | Extracted: {2} | Cache hits: {3}"},
            {"ErrTailHole", "Severe tail hole (Incomplete/Damaged)"},
            {"ErrDecodePoison", "Video decode failed (Same source bad file)"},
            {"ErrDecodeTimeout", "Video decode failed (Glitch/Timeout)"},
            {"ProgFinalFail", ", ⚠️ {0} damaged"},
            {"ProgFinalLock", ", 🔒 skipped {0} locked files"},
            {"ProgFinalShort", ", ⏭️ skipped {0} short files"}, 
            {"ProgDone", "⚡ [Pipeline Complete] Processed {0} files, cache hit {1} times{2}{3}{4}. Running coarse matching..."},
            {"ProgPhase2", "🔬 Performing 350s deep scan (intercepting false positives)..."},
            {"ResUnknown", "Unknown"},
            {"PrevRefBoth", "📍 Reference File (Top: Head match / Bottom: Tail match)"},
            {"PrevRefHead", "📍 Reference File (Head matched: ~{0}s)"},
            {"PrevRefTail", "📍 Reference File (Tail matched: ~{0}s)"},
            {"PrevErrDual", "❌ Unable to generate dual preview thumbnail"},
            {"PrevLoading", "⏳ Loading matched preview (~{0}s)..."},
            {"PrevShowing", "📍 Current preview position: {0}s"},
            {"PrevErrFmt", "❌ Unable to generate preview\n(Unsupported codec possible)"},
            {"PrevErrMsg", "Preview failed: "},
            {"MsgConfirmDel", "Are you sure you want to move the selected {0} file(s) to the Recycle Bin?"},
            {"TitleConfirmDel", "Confirm Deletion"},
            {"StatusDeleting", "🗑️ Deleting files in bulk, please wait..."},
            {"StatusDeletingProgress", "🗑️ Deleting files... ({0}/{1})"},
            {"StatusDelDone", "🗑️ Moved {0} file(s) to Recycle Bin"},
            {"BtnRescan", "🔍 Scan Again"},
            {"TxtRefGroup", "[Group {0}] ⭐ Reference File"},
            {"TxtGroup", "Group"},
            {"TxtHead", "Head"},
            {"TxtTail", "Tail"},
            {"TxtFeature", "Feature"},
            {"TxtHeadMatch", "頭部 {0}s"},
            {"TxtTailMatch", "尾部 {0}s"},
            {"StatusTime", " (Time: {0}m {1}s)"} 
        };

        public void Init()
        {
            CurrentLanguage = "zh-TW";
            _dict = new Dictionary<string, string>(); 
        }

        public void ToggleLanguage()
        {
            if (CurrentLanguage == "zh-TW")
            {
                CurrentLanguage = "en-US";
                _dict = _enDict;
            }
            else
            {
                CurrentLanguage = "zh-TW";
                _dict = new Dictionary<string, string>(); 
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
            LanguageChanged?.Invoke();
        }

        public string? this[string key] => _dict.TryGetValue(key, out var val) ? val : null;

        public string GetString(string key, string defaultValue)
        {
            _templateDict.TryAdd(key, defaultValue); 
            return _dict.TryGetValue(key, out var val) ? val : defaultValue;
        }

        public void ExportTemplate()
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "lang_template.json");
                var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                File.WriteAllText(path, JsonSerializer.Serialize(_templateDict, options), System.Text.Encoding.UTF8);
            }
            catch { }
        }
    }
}