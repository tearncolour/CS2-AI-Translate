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
            public LocText Text;   // 显示时才按当前语言格式化，切换游戏语言后旧记录也跟着变
        }

        private static readonly LinkedList<Entry> s_Entries = new LinkedList<Entry>();
        private static readonly object s_Lock = new object();
        private static int s_Version;
        private static int s_RenderedVersion = -1;
        private static int s_RenderedGeneration = -1;
        private static bool s_RenderedDetails;
        private static string s_Rendered = string.Empty;

        public static int Version => s_Version;

        public static void Retry(int attempt, string reason) => Add(Level.Warning, LocText.Of("LogRetry", attempt, reason));

        public static void Info(string text) => Add(Level.Info, LocText.Literal(text));

        public static void Success(string text) => Add(Level.Success, LocText.Literal(text));

        public static void Detail(string text) => Add(Level.Detail, LocText.Literal(text));

        public static void Warning(string text) => Add(Level.Warning, LocText.Literal(text));

        public static void Error(string text) => Add(Level.Error, LocText.Literal(text));

        public static void Info(LocText text) => Add(Level.Info, text);

        public static void Success(LocText text) => Add(Level.Success, text);

        public static void Detail(LocText text) => Add(Level.Detail, text);

        public static void Warning(LocText text) => Add(Level.Warning, text);

        public static void Error(LocText text) => Add(Level.Error, text);

        public static void Add(Level level, string text) => Add(level, LocText.Literal(text));

        public static void Add(Level level, LocText text)
        {
            if (text == null || text.IsEmpty)
            {
                return;
            }
            lock (s_Lock)
            {
                s_Entries.AddFirst(new Entry { Time = DateTime.Now, Level = level, Text = text });
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
            int generation = LocText.Generation;
            if (version == s_RenderedVersion && generation == s_RenderedGeneration && showDetails == s_RenderedDetails)
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
                    string line = Shorten(e.Text.ToString().Replace("\r", string.Empty), kMaxLineLength);
                    // 明细行缩进、不带时间，紧跟在它所属的批次下面（列表是倒序的，所以明细先于批次行写入）。
                    if (e.Level == Level.Detail)
                    {
                        sb.Append("　　　　　").Append(line);
                        continue;
                    }
                    sb.Append(e.Time.ToString("HH:mm:ss")).Append("　").Append(prefix?.Invoke(e.Level) ?? string.Empty).Append(line);
                }
            }
            s_Rendered = sb.Length > 0 ? sb.ToString() : emptyText;
            s_RenderedVersion = version;
            s_RenderedGeneration = generation;
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
