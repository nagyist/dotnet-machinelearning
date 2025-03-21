﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Microsoft.Data.Analysis
{
    public partial class PrimitiveDataFrameColumn<T> : DataFrameColumn
        where T : unmanaged
    {
        /// <inheritdoc/>
        public new PrimitiveDataFrameColumn<T> Sort(bool ascending = true, bool putNullValuesLast = true)
        {
            return (PrimitiveDataFrameColumn<T>)base.Sort(ascending, putNullValuesLast);
        }

        protected internal override PrimitiveDataFrameColumn<long> GetSortIndices(bool ascending = true, bool putNullValuesLast = true)
        {
            var comparer = Comparer<T>.Default;

            List<List<int>> bufferSortIndices = new List<List<int>>(_columnContainer.Buffers.Count);
            var columnNullIndices = new Int64DataFrameColumn("NullIndices", NullCount);
            long nullIndicesSlot = 0;
            // Sort each buffer first
            for (int b = 0; b < _columnContainer.Buffers.Count; b++)
            {
                ReadOnlyDataFrameBuffer<T> buffer = _columnContainer.Buffers[b];
                ReadOnlySpan<byte> nullBitMapSpan = _columnContainer.NullBitMapBuffers[b].ReadOnlySpan;
                int[] sortIndices = new int[buffer.Length];
                for (int i = 0; i < buffer.Length; i++)
                {
                    sortIndices[i] = i;
                }
                IntrospectiveSort(buffer.ReadOnlySpan, buffer.Length, sortIndices, comparer);
                // Bug fix: QuickSort is not stable. When PrimitiveDataFrameColumn has null values and default values, they move around
                List<int> nonNullSortIndices = new List<int>();
                for (int i = 0; i < sortIndices.Length; i++)
                {
                    int localSortIndex = sortIndices[i];
                    if (BitUtility.IsValid(nullBitMapSpan, localSortIndex))
                    {
                        nonNullSortIndices.Add(sortIndices[i]);
                    }
                    else
                    {
                        columnNullIndices[nullIndicesSlot] = localSortIndex + b * _columnContainer.Buffers[0].Length;
                        nullIndicesSlot++;
                    }
                }
                bufferSortIndices.Add(nonNullSortIndices);
            }

            // Simple merge sort to build the full column's sort indices
            ValueTuple<T, int> GetFirstNonNullValueAndBufferIndexStartingAtIndex(int bufferIndex, int startIndex)
            {
                int index = bufferSortIndices[bufferIndex][startIndex];
                T value;
                ReadOnlyMemory<byte> buffer = _columnContainer.Buffers[bufferIndex].ReadOnlyBuffer;
                ReadOnlyMemory<T> typedBuffer = Unsafe.As<ReadOnlyMemory<byte>, ReadOnlyMemory<T>>(ref buffer);
                if (!typedBuffer.IsEmpty)
                {
                    bool isArray = MemoryMarshal.TryGetArray(typedBuffer, out ArraySegment<T> arraySegment);
                    if (isArray)
                    {
                        value = arraySegment.Array[index + arraySegment.Offset];
                    }
                    else
                        value = _columnContainer.Buffers[bufferIndex][index];
                }
                else
                {
                    value = _columnContainer.Buffers[bufferIndex][index];
                }
                return (value, startIndex);
            }

            SortedDictionary<T, List<ValueTuple<int, int>>> heapOfValueAndListOfTupleOfSortAndBufferIndex = new SortedDictionary<T, List<ValueTuple<int, int>>>(comparer);
            IList<ReadOnlyDataFrameBuffer<T>> buffers = _columnContainer.Buffers;
            for (int i = 0; i < buffers.Count; i++)
            {
                ReadOnlyDataFrameBuffer<T> buffer = buffers[i];
                if (bufferSortIndices[i].Count == 0)
                {
                    // All nulls
                    continue;
                }
                ValueTuple<T, int> valueAndBufferIndex = GetFirstNonNullValueAndBufferIndexStartingAtIndex(i, 0);
                if (heapOfValueAndListOfTupleOfSortAndBufferIndex.ContainsKey(valueAndBufferIndex.Item1))
                {
                    heapOfValueAndListOfTupleOfSortAndBufferIndex[valueAndBufferIndex.Item1].Add((valueAndBufferIndex.Item2, i));
                }
                else
                {
                    heapOfValueAndListOfTupleOfSortAndBufferIndex.Add(valueAndBufferIndex.Item1, new List<ValueTuple<int, int>>() { (valueAndBufferIndex.Item2, i) });
                }
            }
            Int64DataFrameColumn columnSortIndices = new Int64DataFrameColumn("SortIndices", Length);

            GetBufferSortIndex getBufferSortIndex = new GetBufferSortIndex((int bufferIndex, int sortIndex) => (bufferSortIndices[bufferIndex][sortIndex]) + bufferIndex * bufferSortIndices[0].Count);
            GetValueAndBufferSortIndexAtBuffer<T> getValueAndBufferSortIndexAtBuffer = new GetValueAndBufferSortIndexAtBuffer<T>((int bufferIndex, int sortIndex) => GetFirstNonNullValueAndBufferIndexStartingAtIndex(bufferIndex, sortIndex));
            GetBufferLengthAtIndex getBufferLengthAtIndex = new GetBufferLengthAtIndex((int bufferIndex) => bufferSortIndices[bufferIndex].Count);

            PopulateColumnSortIndicesWithHeap(heapOfValueAndListOfTupleOfSortAndBufferIndex,
                columnSortIndices,
                columnNullIndices,
                ascending,
                putNullValuesLast,
                getBufferSortIndex,
                getValueAndBufferSortIndexAtBuffer,
                getBufferLengthAtIndex);

            return columnSortIndices;
        }
    }
}
