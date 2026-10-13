using System;
using System.Collections.Generic;

namespace AITranslate.Core
{
    /// <summary>
    /// 分帧复制一个可能被游戏同时修改的集合（游戏词典、本地化源）。
    /// 每处理 256 条检查一次每帧预算，超出就让出这一帧；两帧之间集合被改动（继续枚举会抛 InvalidOperationException）时从头重来。
    /// 连续 <see cref="kChunkedAttempts"/> 次都被改动后，最后一次在一帧内复制完（同一帧里不会被改动），保证拿到一致的副本。
    /// </summary>
    internal static class ChunkedCopy
    {
        public const int kChunkedAttempts = 3;

        private const int kCheckEvery = 256;

        /// <param name="source">每次（重新）开始时调用，取得要复制的集合。</param>
        /// <param name="reset">每次（重新）开始前清空目标。</param>
        /// <param name="add">复制一条。</param>
        /// <param name="overBudget">这一帧的时间是否已经用完。</param>
        /// <param name="onRestart">集合被改动、从头重来时调用（用于统计）。</param>
        /// <param name="onError">读取出错时调用，已读到的部分保留；为 null 时异常照常抛出。</param>
        /// <returns>每让出一帧返回一次。</returns>
        public static IEnumerable<bool> Run<T>(Func<IEnumerable<T>> source, Action reset, Action<T> add, Func<bool> overBudget, Action onRestart, Action<Exception> onError = null)
        {
            for (int attempt = 0; ; attempt++)
            {
                reset();
                bool chunked = attempt < kChunkedAttempts;
                bool modified = false;
                IEnumerator<T> e;
                try
                {
                    e = source().GetEnumerator();
                }
                catch (Exception ex) when (onError != null)
                {
                    onError(ex);
                    yield break;
                }
                try
                {
                    int n = 0;
                    while (true)
                    {
                        bool has;
                        try
                        {
                            has = e.MoveNext();
                        }
                        catch (InvalidOperationException) when (chunked)
                        {
                            modified = true;
                            break;
                        }
                        catch (Exception ex) when (onError != null)
                        {
                            onError(ex);
                            break;
                        }
                        if (!has)
                        {
                            break;
                        }
                        add(e.Current);
                        if (chunked && ++n % kCheckEvery == 0 && overBudget())
                        {
                            yield return true;
                        }
                    }
                }
                finally
                {
                    e.Dispose();
                }
                if (!modified)
                {
                    yield break;
                }
                onRestart?.Invoke();
            }
        }
    }
}
