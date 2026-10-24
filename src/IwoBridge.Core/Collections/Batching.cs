using System;
using System.Collections.Generic;

namespace IwoBridge.Core.Collections
{
    /// <summary>Разбиение списка на пакеты фиксированного размера — одно место на весь продукт (DRY).</summary>
    public static class Batching
    {
        public static IEnumerable<IReadOnlyList<T>> Chunk<T>(IReadOnlyList<T> source, int size)
        {
            if (source is null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (size < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(size), size, "Размер пакета должен быть не меньше 1.");
            }

            return ChunkCore(source, size);
        }

        private static IEnumerable<IReadOnlyList<T>> ChunkCore<T>(IReadOnlyList<T> source, int size)
        {
            for (int start = 0; start < source.Count; start += size)
            {
                int count = Math.Min(size, source.Count - start);
                var batch = new List<T>(count);
                for (int i = 0; i < count; i++)
                {
                    batch.Add(source[start + i]);
                }

                yield return batch;
            }
        }
    }
}
