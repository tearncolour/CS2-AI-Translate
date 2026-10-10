using System;
using System.Linq;
using System.Threading;

namespace AITranslate.Core
{
    /// <summary>
    /// 延迟格式化的界面文字：只记下文字名和参数（参数在创建时就取好值），显示时才按当前游戏语言格式化。
    /// 用于概况、进度、日志这类会一直留在界面上的文字，切换游戏语言后它们会跟着变。
    /// </summary>
    internal sealed class LocText
    {
        public static readonly LocText Empty = new LocText(null, null, string.Empty, null);

        /// <summary>按名字取当前语言的文字并格式化，由翻译服务设置。</summary>
        public static Func<string, object[], string> Resolver;

        private static int s_Generation;

        /// <summary>游戏语言（或本 mod 的界面译文）变化时递增；缓存了渲染结果的地方据此刷新。</summary>
        public static int Generation => s_Generation;

        public static void Invalidate() => Interlocked.Increment(ref s_Generation);

        private readonly string m_Name;
        private readonly object[] m_Args;
        private readonly string m_Literal;
        private readonly LocText[] m_Parts;

        private LocText(string name, object[] args, string literal, LocText[] parts)
        {
            m_Name = name;
            m_Args = args;
            m_Literal = literal;
            m_Parts = parts;
        }

        public static LocText Of(string name, params object[] args) => new LocText(name, args, null, null);

        /// <summary>不需要翻译的文字（如接口返回的错误信息、mod 名）。</summary>
        public static LocText Literal(string text) => new LocText(null, null, text ?? string.Empty, null);

        public static LocText Concat(params LocText[] parts) => new LocText(null, null, null, parts.Where(p => p != null).ToArray());

        public bool IsEmpty => m_Name == null && m_Parts == null && string.IsNullOrEmpty(m_Literal);

        public override string ToString()
        {
            if (m_Parts != null)
            {
                return string.Concat(m_Parts.Select(p => p.ToString()));
            }
            if (m_Name == null)
            {
                return m_Literal;
            }
            // 参数里的 LocText 也按当前语言渲染（如“上次：{0}”里的任务总结）。
            object[] args = m_Args?.Select(a => a is LocText t ? t.ToString() : a).ToArray();
            return Resolver != null ? Resolver(m_Name, args) : m_Name;
        }
    }
}
