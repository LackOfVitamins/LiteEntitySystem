using System;
using System.Collections.Generic;
using System.Text;

namespace LiteEntitySystem.Compatibility
{
    public static class QueueExtensions
    {
        public static bool TryPeek<T>(this Queue<T> queue, out T result)
        {
            result = default;
            if (queue.Count == 0)
                return false;
            result = queue.Peek();
            return true;
        }

        public static bool TryDequeue<T>(this Queue<T> queue, out T result)
        {
            result = default;
            if (queue.Count == 0)
                return false;
            result = queue.Dequeue();
            return true;
        }
    }
}
