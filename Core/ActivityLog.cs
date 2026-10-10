using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace AITranslate.Core
{
    /// <summary>
    /// 「日志」标签页显示的实时活动记录。任何线程都可写入；界面每帧读取，只有内容变化时才重新拼接文字。
    /// 最新的在最上面，最多保留 <see cref="kMaxLines"/> 行。
    /// </summary>
    internal static class ActivityLog
    {
        public enum Level
        {
            Info,
            Success,
            Detail,   // 译文样例等明细，可在设置中隐藏
            Warning,
            Error,
        }

        private const int kMaxLines = 80;
        private const int kMaxLineLength = 160;

        private struct Entry
        {
            public DateTime Time;
            public Level Level;
            public string Text;
        }

        private static readonly LinkedList<Entry> s_Entries = new LinkedList<Entry>();
        private static readonly object s_Lock = new object();
        private static int s_Version;
        private static int s_RenderedVersion = -1;
        private static bool s_RenderedDetails;
        private static string s_Rendered = string.Empty;

        public static int Version => s_Version;

        /// <summary>API 重试提示的格式（{0}=次数，{1}=原因），由服务按当前语言设置。</summary>
        public static string RetryFormat { get; set; } = "Retry {0}: {1}";

        public static void Retry(int attempt, string reason)
        {
            string text;
            try
            {
                text = string.Format(RetryFormat, attempt, reason);
            }
            catch (FormatException)
            {
                text = "Retry " + attempt + ": " + reason;
            }
            Add(Level.Warning, text);
        }

        public static void Info(string text) => Add(Level.Info, text);

        public static void Success(string text) => Add(Level.Success, text);

        public static void Detail(string text) => Add(Level.Detail, text);

        public static void Warning(string text) => Add(Level.Warning, text);

        public static void Error(string text) => Add(Level.Error, text);

        public static void Add(Level level, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }
            string line = text.Replace("\r", string.Empty).Replace("\n", " ");
            if (line.Length > kMaxLineLength)
            {
                line = line.Substring(0, kMaxLineLength) + "…";
            }
            lock (s_Lock)
            {
                s_Entries.AddFirst(new Entry { Time = DateTime.Now, Level = level, Text = line });
                while (s_Entries.Count > kMaxLines)
                {
                    s_Entries.RemoveLast();
                }
            }
            Interlocked.Increment(ref s_Version);
        }

        public static void Clear()
        {
            lock (s_Lock)
            {
                s_Entries.Clear();
            }
            Interlocked.Increment(ref s_Version);
        }

        /// <summary>拼接显示文字；内容和选项都没变时直接返回上次的结果。</summary>
        public static string Render(bool showDetails, string emptyText, Func<Level, string> prefix)
        {
            int version = s_Version;
            if (version == s_RenderedVersion && showDetails == s_RenderedDetails)
            {
                return s_Rendered;
            }
            var sb = new StringBuilder();
            lock (s_Lock)
            {
                foreach (var e in s_Entries)
                {
                    if (e.Level == Level.Detail && !showDetails)
                    {
                        continue;
                    }
                    if (sb.Length > 0)
                    {
                        sb.Append('\n');
                    }
                    // 明细行缩进、不带时间，紧跟在它所属的批次下面（列表是倒序的，所以明细先于批次行写入）。
                    if (e.Level == Level.Detail)
                    {
                        sb.Append("　　　　　").Append(e.Text);
                        continue;
                    }
                    sb.Append(e.Time.ToString("HH:mm:ss")).Append("　").Append(prefix?.Invoke(e.Level) ?? string.Empty).Append(e.Text);
                }
            }
            s_Rendered = sb.Length > 0 ? sb.ToString() : emptyText;
            s_RenderedVersion = version;
            s_RenderedDetails = showDetails;
            return s_Rendered;
        }


        public static string Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            text = text.Replace("\n", " ").Trim();
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }
    }
}
