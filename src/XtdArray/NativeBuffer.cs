﻿#if NET6_0_OR_GREATER

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XtdArray;

namespace XtdArray
{
    public sealed unsafe class NativeBuffer : IDisposable
    {
        internal readonly byte* buffer;
        readonly nuint length;
        bool isDisposed;

        public nuint Length => length;

        public NativeBuffer(nuint length)
        {
            // TODO: for .NET STANDARD
            // Marshal.AllocHGlobal((IntPtr))
            var allocSize = (long)checked(length);

            this.length = length;
            this.buffer = (byte*)NativeMemory.Alloc(length);
            GC.AddMemoryPressure(allocSize);
        }

        public ref byte this[nuint index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (index >= length) ThrowArgumentOutOfRangeException(nameof(index));
                return ref buffer[index];
            }
        }

        public Span<byte> Slice(nuint start, int length)
        {
            if (start + checked((nuint)length) > this.length) ThrowArgumentOutOfRangeException(nameof(length));
            return new Span<byte>(buffer + start, length);
        }

        public Memory<byte> SliceMemory(nuint start, int length)
        {
            if (start + checked((nuint)length) > this.length) ThrowArgumentOutOfRangeException(nameof(length));
            return new PointerMemoryManager(buffer + start, length).Memory;
        }

        public ref byte GetPinnableReference()
        {
            if (length == 0)
            {
                return ref Unsafe.NullRef<byte>();
            }
            return ref this[0];
        }

        public bool TryGetFullSpan(out Span<byte> span)
        {
            if (length < int.MaxValue)
            {
                span = new Span<byte>(buffer, (int)length);
                return true;
            }
            else
            {
                span = default;
                return false;
            }
        }

        public IBufferWriter<byte> CreateBufferWriter()
        {
            return new NativeBufferWriter(this);
        }

        public SpanSequence AsSpanSequence(int chunkSize = int.MaxValue)
        {
            return new SpanSequence(this, chunkSize);
        }

        public MemorySequence AsMemorySequence(int chunkSize = int.MaxValue)
        {
            return new MemorySequence(this, chunkSize);
        }

        public IReadOnlyList<ReadOnlyMemory<byte>> AsReadOnlyList(int chunkSize = int.MaxValue)
        {
            var array = new ReadOnlyMemory<byte>[((long)length <= chunkSize) ? 1 : ((long)length / chunkSize) + 1];
            {
                var i = 0;
                foreach (var item in this.AsMemorySequence(chunkSize))
                {
                    array[i++] = item;
                }
            }

            return array;
        }

        public ReadOnlySequence<byte> AsReadOnlySequence(int chunkSize = int.MaxValue)
        {
            // TODO: length == 0

            var array = new Segment[((long)length <= chunkSize) ? 1 : ((long)length / chunkSize) + 1];
            {
                var i = 0;
                foreach (var item in this.AsMemorySequence(chunkSize))
                {
                    array[i++] = new Segment(item);
                }
            }

            long running = 0;
            for (int i = 0; i < array.Length; i++)
            {
                var next = i < (array.Length - 1) ? array[i + 1] : null;
                array[i].SetRunningIndexAndNext(running, next);
                running += array[i].Memory.Length;
            }

            var firstSegment = array[0];
            var lastSegment = array[array.Length - 1];
            return new ReadOnlySequence<byte>(firstSegment, 0, lastSegment, lastSegment.Memory.Length);
        }

        public SpanSequence GetEnumerator()
        {
            return this.AsSpanSequence(int.MaxValue);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [DoesNotReturn]
        void ThrowArgumentOutOfRangeException(string paramName)
        {
            throw new ArgumentOutOfRangeException(paramName);
        }

        public void Dispose()
        {
            DisposeCore();
            GC.SuppressFinalize(this);
        }

        void DisposeCore()
        {
            if (!isDisposed)
            {
                isDisposed = true;
                NativeMemory.Free(buffer);
                GC.RemoveMemoryPressure((long)length);
            }
        }

        ~NativeBuffer()
        {
            DisposeCore();
        }

        public ref struct SpanSequence
        {
            readonly NativeBuffer nativeBuffer;
            readonly int chunkSize;
            nuint index;
            nuint sliceStart;

            internal SpanSequence(NativeBuffer nativeBuffer, int chunkSize)
            {
                this.nativeBuffer = nativeBuffer;
                this.index = 0;
                this.sliceStart = 0;
                this.chunkSize = chunkSize;
            }

            public SpanSequence GetEnumerator() => this;

            public Span<byte> Current
            {
                get
                {
                    return nativeBuffer.Slice(sliceStart, (int)Math.Min(checked((nuint)chunkSize), nativeBuffer.length - sliceStart));
                }
            }

            public bool MoveNext()
            {
                if (index < nativeBuffer.length)
                {
                    sliceStart = index;
                    index += checked((nuint)chunkSize);
                    return true;
                }
                return false;
            }
        }

        public ref struct MemorySequence
        {
            readonly NativeBuffer nativeBuffer;
            readonly int chunkSize;
            nuint index;
            nuint sliceStart;

            internal MemorySequence(NativeBuffer nativeBuffer, int chunkSize)
            {
                this.nativeBuffer = nativeBuffer;
                this.index = 0;
                this.sliceStart = 0;
                this.chunkSize = chunkSize;
            }

            public MemorySequence GetEnumerator() => this;

            public Memory<byte> Current
            {
                get
                {
                    return nativeBuffer.SliceMemory(sliceStart, (int)Math.Min(checked((nuint)chunkSize), nativeBuffer.length - sliceStart));
                }
            }

            public bool MoveNext()
            {
                if (index < nativeBuffer.length)
                {
                    sliceStart = index;
                    index += checked((nuint)chunkSize);
                    return true;
                }
                return false;
            }
        }

        class Segment : ReadOnlySequenceSegment<byte>
        {
            public Segment(Memory<byte> buffer)
            {
                Memory = buffer;
            }

            internal void SetRunningIndexAndNext(long runningIndex, Segment? nextSegment)
            {
                RunningIndex = runningIndex;
                Next = nextSegment;
            }
        }
    }

    internal sealed unsafe class NativeBufferWriter : IBufferWriter<byte>
    {
        readonly NativeBuffer nativeBuffer;
        PointerMemoryManager? pointerMemoryManager;
        nuint written;

        internal NativeBufferWriter(NativeBuffer nativeBuffer)
        {
            this.nativeBuffer = nativeBuffer;
            this.pointerMemoryManager = null;
        }

        public void Advance(int count)
        {
            written += checked((nuint)count);
            if (pointerMemoryManager != null)
            {
                pointerMemoryManager.AllowReuse();
            }
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            if (nativeBuffer.Length - written < checked((nuint)sizeHint)) throw new InvalidOperationException($"sizeHint:{sizeHint} is capacity:{nativeBuffer.Length} - written:{written} over");
            var length = (int)Math.Min(int.MaxValue, (nativeBuffer.Length - written));

            return new Span<byte>(nativeBuffer.buffer, length);
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (nativeBuffer.Length - written < checked((nuint)sizeHint)) throw new InvalidOperationException($"sizeHint:{sizeHint} is capacity:{nativeBuffer.Length} - written:{written} over");
            var length = (int)Math.Min(int.MaxValue, (nativeBuffer.Length - written));
            if (pointerMemoryManager == null)
            {
                pointerMemoryManager = new PointerMemoryManager(nativeBuffer.buffer, length);
            }
            else
            {
                pointerMemoryManager.ResetLength(length);
            }

            return pointerMemoryManager.Memory;
        }
    }
}

#endif