﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SoraBot_v2.Extensions
{
    public static class Extensions
    {
        public static bool TryUpdate<TKey, TValue>(this ConcurrentDictionary<TKey, TValue> self, TKey key, TValue value)
        {
            TValue currentValue = default(TValue);
            if (self.ContainsKey(key))
                self.TryGetValue(key, out currentValue);
            return self.TryUpdate(key, value, currentValue);
        }

        public static TValue TryGet<TKey, TValue>(this ConcurrentDictionary<TKey, TValue> self, TKey key)
        {
            TValue value = default(TValue);
            if (self.ContainsKey(key))
                self.TryGetValue(key, out value);
            return value;
        }

        public static bool Contains(this string source, string toCheck, StringComparison comp) //this string source makes "string".contains possible
        {
            return source.IndexOf(toCheck, comp) >= 0;
        }
        
        private static Random rng = new Random();  

        public static void Shuffle<T>(this IList<T> list)  
        {  
            int n = list.Count;  
            while (n > 1) {  
                n--;  
                int k = rng.Next(n + 1);  
                T value = list[k];  
                list[k] = list[n];  
                list[n] = value;  
            }  
        }
    }
}