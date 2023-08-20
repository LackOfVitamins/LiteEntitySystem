using System;
using System.Collections.Generic;
using System.Text;

namespace LiteEntitySystem.Compatibility
{
    public static class MathUtils
    {
        public static T Clamp<T>(T value, T min, T max) where T : System.IComparable<T>
        {
            T result = value;
            if (value.CompareTo(max) > 0)
                result = max;
            if (value.CompareTo(min) < 0)
                result = min;
            return result;
        }
    }   
}
