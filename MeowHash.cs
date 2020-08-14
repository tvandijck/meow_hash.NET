//#define MEOW_DUMP

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Meow
{
    public static class MeowHash
    {
        private static readonly byte[] s_meowShiftAdjust = new byte[32] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
        private static readonly byte[] s_meowMaskLen = new byte[32] { 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        // NOTE(casey): The default seed is now a "nothing-up-our-sleeves" number for good measure.  You may verify that it is just an encoding of Pi.
        public static readonly byte[] MeowDefaultSeed = new byte[128]
        {
            0x32, 0x43, 0xF6, 0xA8, 0x88, 0x5A, 0x30, 0x8D,
            0x31, 0x31, 0x98, 0xA2, 0xE0, 0x37, 0x07, 0x34,
            0x4A, 0x40, 0x93, 0x82, 0x22, 0x99, 0xF3, 0x1D,
            0x00, 0x82, 0xEF, 0xA9, 0x8E, 0xC4, 0xE6, 0xC8,
            0x94, 0x52, 0x82, 0x1E, 0x63, 0x8D, 0x01, 0x37,
            0x7B, 0xE5, 0x46, 0x6C, 0xF3, 0x4E, 0x90, 0xC6,
            0xCC, 0x0A, 0xC2, 0x9B, 0x7C, 0x97, 0xC5, 0x0D,
            0xD3, 0xF8, 0x4D, 0x5B, 0x5B, 0x54, 0x70, 0x91,
            0x79, 0x21, 0x6D, 0x5D, 0x98, 0x97, 0x9F, 0xB1,
            0xBD, 0x13, 0x10, 0xBA, 0x69, 0x8D, 0xFB, 0x5A,
            0xC2, 0xFF, 0xD7, 0x2D, 0xBD, 0x01, 0xAD, 0xFB,
            0x7B, 0x8E, 0x1A, 0xFE, 0xD6, 0xA2, 0x67, 0xE9,
            0x6B, 0xA7, 0xC9, 0x04, 0x5F, 0x12, 0xC7, 0xF9,
            0x92, 0x4A, 0x19, 0x94, 0x7B, 0x39, 0x16, 0xCF,
            0x70, 0x80, 0x1F, 0x2E, 0x28, 0x58, 0xEF, 0xC1,
            0x66, 0x36, 0x92, 0x0D, 0x87, 0x15, 0x74, 0xE6
        };

        private const int MEOW_PREFETCH_LIMIT = 0x3ff;
        private const int MEOW_PREFETCH = 4096;
        private const int MEOW_PAGESIZE = 4096;

        //
        // NOTE(casey): Single block version
        //
        public static unsafe Vector128<byte> Hash(ReadOnlySpan<byte> Seed128Init, ReadOnlySpan<byte> SourceInit)
        {
            Vector128<byte> xmm0, xmm1, xmm2, xmm3, xmm4, xmm5, xmm6, xmm7; // NOTE(casey): xmm0-xmm7 are the hash accumulation lanes
            Vector128<byte> xmm8, xmm9, xmm10, xmm11, xmm12, xmm13, xmm14, xmm15; // NOTE(casey): xmm8-xmm15 hold values to be appended (residual, length)

            int Len = SourceInit.Length;
            fixed (byte* sourceInitPtr = SourceInit)
            fixed (byte* seedInitPtr = Seed128Init)
            {
                byte* rax = sourceInitPtr;
                byte* rcx = seedInitPtr;

                //
                // NOTE(casey): Seed the eight hash registers
                //

                xmm0 = Sse2.LoadVector128(rcx + 0x00);
                xmm1 = Sse2.LoadVector128(rcx + 0x10);
                xmm2 = Sse2.LoadVector128(rcx + 0x20);
                xmm3 = Sse2.LoadVector128(rcx + 0x30);

                xmm4 = Sse2.LoadVector128(rcx + 0x40);
                xmm5 = Sse2.LoadVector128(rcx + 0x50);
                xmm6 = Sse2.LoadVector128(rcx + 0x60);
                xmm7 = Sse2.LoadVector128(rcx + 0x70);

                // MEOW_DUMP_STATE("Seed", xmm0, xmm1, xmm2, xmm3, xmm4, xmm5, xmm6, xmm7, 0);

                //
                // NOTE(casey): Hash all full 256-byte blocks
                //

                int BlockCount = (SourceInit.Length >> 8);
                if (BlockCount > MEOW_PREFETCH_LIMIT)
                {
                    // NOTE(casey): For large input, modern Intel x64's can't hit full speed without prefetching, so we use this loop
                    while (BlockCount-- > 0)
                    {
                        Sse.Prefetch0(rax + MEOW_PREFETCH + 0x00);
                        Sse.Prefetch0(rax + MEOW_PREFETCH + 0x40);
                        Sse.Prefetch0(rax + MEOW_PREFETCH + 0x80);
                        Sse.Prefetch0(rax + MEOW_PREFETCH + 0xc0);

                        MEOW_MIX(ref xmm0, ref xmm4, ref xmm6, ref xmm1, ref xmm2, rax + 0x00);
                        MEOW_MIX(ref xmm1, ref xmm5, ref xmm7, ref xmm2, ref xmm3, rax + 0x20);
                        MEOW_MIX(ref xmm2, ref xmm6, ref xmm0, ref xmm3, ref xmm4, rax + 0x40);
                        MEOW_MIX(ref xmm3, ref xmm7, ref xmm1, ref xmm4, ref xmm5, rax + 0x60);
                        MEOW_MIX(ref xmm4, ref xmm0, ref xmm2, ref xmm5, ref xmm6, rax + 0x80);
                        MEOW_MIX(ref xmm5, ref xmm1, ref xmm3, ref xmm6, ref xmm7, rax + 0xa0);
                        MEOW_MIX(ref xmm6, ref xmm2, ref xmm4, ref xmm7, ref xmm0, rax + 0xc0);
                        MEOW_MIX(ref xmm7, ref xmm3, ref xmm5, ref xmm0, ref xmm1, rax + 0xe0);

                        rax += 0x100;
                    }
                }
                else
                {
                    // NOTE(casey): For small input, modern Intel x64's can't hit full speed _with_ prefetching (because of port pressure), so we use this loop.
                    while (BlockCount-- > 0)
                    {
                        MEOW_MIX(ref xmm0, ref xmm4, ref xmm6, ref xmm1, ref xmm2, rax + 0x00);
                        MEOW_MIX(ref xmm1, ref xmm5, ref xmm7, ref xmm2, ref xmm3, rax + 0x20);
                        MEOW_MIX(ref xmm2, ref xmm6, ref xmm0, ref xmm3, ref xmm4, rax + 0x40);
                        MEOW_MIX(ref xmm3, ref xmm7, ref xmm1, ref xmm4, ref xmm5, rax + 0x60);
                        MEOW_MIX(ref xmm4, ref xmm0, ref xmm2, ref xmm5, ref xmm6, rax + 0x80);
                        MEOW_MIX(ref xmm5, ref xmm1, ref xmm3, ref xmm6, ref xmm7, rax + 0xa0);
                        MEOW_MIX(ref xmm6, ref xmm2, ref xmm4, ref xmm7, ref xmm0, rax + 0xc0);
                        MEOW_MIX(ref xmm7, ref xmm3, ref xmm5, ref xmm0, ref xmm1, rax + 0xe0);

                        rax += 0x100;
                    }
                }

#if MEOW_DUMP
                MEOW_DUMP_STATE("PostBlocks", xmm0, xmm1, xmm2, xmm3, xmm4, xmm5, xmm6, xmm7);
#endif

                //
                // NOTE(casey): Load any less-than-32-byte residual
                //

                xmm9 = Vector128<byte>.Zero;
                xmm11 = Vector128<byte>.Zero;

                //
                // TODO(casey): I need to put more thought into how the end-of-buffer stuff is actually working out here,
                // because I _think_ it may be possible to remove the first branch (on Len8) and let the mask zero out the
                // result, but it would take a little thought to make sure it couldn't read off the end of the buffer due
                // to the & 0xf on the align computation.
                //

                // NOTE(casey): First, we have to load the part that is _not_ 16-byte aligned
                byte* Last = (byte*)sourceInitPtr + (Len & ~0xf);
                int Len8 = (Len & 0xf);
                if (Len8 > 0)
                {
                    // NOTE(casey): Load the mask early
                    fixed (byte* MeowMaskLen = s_meowMaskLen)
                    {
                        xmm8 = Sse2.LoadVector128(&MeowMaskLen[0x10 - Len8]);
                    }

                    byte* LastOk = (byte*)((((ulong)(((byte*)sourceInitPtr) + Len - 1)) | (MEOW_PAGESIZE - 1)) - 16);
                    int Align = (Last > LastOk) ? ((int)(ulong)Last) & 0xf : 0;

                    fixed (byte* MeowShiftAdjust = s_meowShiftAdjust)
                    {
                        xmm10 = Sse2.LoadVector128(&MeowShiftAdjust[Align]);
                    }

                    xmm9 = Sse2.LoadVector128(Last - Align);
                    xmm9 = Ssse3.Shuffle(xmm9, xmm10);

                    // NOTE(jeffr): and off the extra bytes
                    xmm9 = Sse2.And(xmm9, xmm8);
                }

                // NOTE(casey): Next, we have to load the part that _is_ 16-byte aligned
                if ((Len & 0x10) != 0)
                {
                    xmm11 = xmm9;
                    xmm9 = Sse2.LoadVector128(Last - 0x10);
                }

                //
                // NOTE(casey): Construct the residual and length injests
                //

                xmm8 = xmm9;
                xmm10 = xmm9;
                xmm8 = Ssse3.AlignRight(xmm8, xmm11, 15);
                xmm10 = Ssse3.AlignRight(xmm10, xmm11, 1);

                // NOTE(casey): We have room for a 128-bit nonce and a 64-bit none here, but
                // the decision was made to leave them zero'd so as not to confuse people
                // about hwo to use them or what security implications they had.
                xmm12 = Vector128<byte>.Zero;
                xmm13 = Vector128<byte>.Zero;
                xmm14 = Vector128<byte>.Zero;
                xmm15 = Vector128.Create((ulong)Len, 0).AsByte();
                xmm12 = Ssse3.AlignRight(xmm12, xmm15, 15);
                xmm14 = Ssse3.AlignRight(xmm14, xmm15, 1);

#if MEOW_DUMP
                MEOW_DUMP_STATE("Residuals", xmm8, xmm9, xmm10, xmm11, xmm12, xmm13, xmm14, xmm15);
#endif

                // NOTE(casey): To maintain the mix-down pattern, we always Meow Mix the less-than-32-byte residual, even if it was empty
                MEOW_MIX_REG(ref xmm0, ref xmm4, ref xmm6, ref xmm1, ref xmm2, xmm8, xmm9, xmm10, xmm11);

                // NOTE(casey): Append the length, to avoid problems with our 32-byte padding
                MEOW_MIX_REG(ref xmm1, ref xmm5, ref xmm7, ref xmm2, ref xmm3, xmm12, xmm13, xmm14, xmm15);

#if MEOW_DUMP
                MEOW_DUMP_STATE("PostAppend", xmm0, xmm1, xmm2, xmm3, xmm4, xmm5, xmm6, xmm7);
#endif

                //
                // NOTE(casey): Hash all full 32-byte blocks
                //
                int LaneCount = (Len >> 5) & 0x7;
                if (LaneCount == 0) goto MixDown; MEOW_MIX(ref xmm2, ref xmm6, ref xmm0, ref xmm3, ref xmm4, rax + 0x00); --LaneCount;
                if (LaneCount == 0) goto MixDown; MEOW_MIX(ref xmm3, ref xmm7, ref xmm1, ref xmm4, ref xmm5, rax + 0x20); --LaneCount;
                if (LaneCount == 0) goto MixDown; MEOW_MIX(ref xmm4, ref xmm0, ref xmm2, ref xmm5, ref xmm6, rax + 0x40); --LaneCount;
                if (LaneCount == 0) goto MixDown; MEOW_MIX(ref xmm5, ref xmm1, ref xmm3, ref xmm6, ref xmm7, rax + 0x60); --LaneCount;
                if (LaneCount == 0) goto MixDown; MEOW_MIX(ref xmm6, ref xmm2, ref xmm4, ref xmm7, ref xmm0, rax + 0x80); --LaneCount;
                if (LaneCount == 0) goto MixDown; MEOW_MIX(ref xmm7, ref xmm3, ref xmm5, ref xmm0, ref xmm1, rax + 0xa0); --LaneCount;
                if (LaneCount == 0) goto MixDown; MEOW_MIX(ref xmm0, ref xmm4, ref xmm6, ref xmm1, ref xmm2, rax + 0xc0); --LaneCount;

            //
            // NOTE(casey): Mix the eight lanes down to one 128-bit hash
            //

            MixDown:

#if MEOW_DUMP
                MEOW_DUMP_STATE("PostLanes", xmm0, xmm1, xmm2, xmm3, xmm4, xmm5, xmm6, xmm7);
#endif

                MEOW_SHUFFLE(ref xmm0, ref xmm1, xmm2, ref xmm4, ref xmm5, xmm6);
                MEOW_SHUFFLE(ref xmm1, ref xmm2, xmm3, ref xmm5, ref xmm6, xmm7);
                MEOW_SHUFFLE(ref xmm2, ref xmm3, xmm4, ref xmm6, ref xmm7, xmm0);
                MEOW_SHUFFLE(ref xmm3, ref xmm4, xmm5, ref xmm7, ref xmm0, xmm1);
                MEOW_SHUFFLE(ref xmm4, ref xmm5, xmm6, ref xmm0, ref xmm1, xmm2);
                MEOW_SHUFFLE(ref xmm5, ref xmm6, xmm7, ref xmm1, ref xmm2, xmm3);
                MEOW_SHUFFLE(ref xmm6, ref xmm7, xmm0, ref xmm2, ref xmm3, xmm4);
                MEOW_SHUFFLE(ref xmm7, ref xmm0, xmm1, ref xmm3, ref xmm4, xmm5);
                MEOW_SHUFFLE(ref xmm0, ref xmm1, xmm2, ref xmm4, ref xmm5, xmm6);
                MEOW_SHUFFLE(ref xmm1, ref xmm2, xmm3, ref xmm5, ref xmm6, xmm7);
                MEOW_SHUFFLE(ref xmm2, ref xmm3, xmm4, ref xmm6, ref xmm7, xmm0);
                MEOW_SHUFFLE(ref xmm3, ref xmm4, xmm5, ref xmm7, ref xmm0, xmm1);

#if MEOW_DUMP
                MEOW_DUMP_STATE("PostMix", xmm0, xmm1, xmm2, xmm3, xmm4, xmm5, xmm6, xmm7);
#endif

                xmm0 = AddQ(xmm0, xmm2);
                xmm1 = AddQ(xmm1, xmm3);
                xmm4 = AddQ(xmm4, xmm6);
                xmm5 = AddQ(xmm5, xmm7);
                xmm0 = Sse2.Xor(xmm0, xmm1);
                xmm4 = Sse2.Xor(xmm4, xmm5);
                xmm0 = AddQ(xmm0, xmm4);

#if MEOW_DUMP
                MEOW_DUMP_STATE("PostFold", xmm0, xmm1, xmm2, xmm3, xmm4, xmm5, xmm6, xmm7);
#endif

                return xmm0;
            }
        }


        //
        // NOTE(casey): Streaming construction
        //

        public unsafe struct State
        {
            public const int c_bufferSize = 256;

            public Vector128<byte> xmm0, xmm1, xmm2, xmm3, xmm4, xmm5, xmm6, xmm7;
            public long TotalLengthInBytes;

            public int BufferLen;
            public byte[] Buffer;
        }

        public static unsafe void Begin(ref State state, ReadOnlySpan<byte> seed128)
        {
            fixed (byte* rcx = seed128)
            {
                state.xmm0 = Sse2.LoadVector128(rcx + 0x00);
                state.xmm1 = Sse2.LoadVector128(rcx + 0x10);
                state.xmm2 = Sse2.LoadVector128(rcx + 0x20);
                state.xmm3 = Sse2.LoadVector128(rcx + 0x30);
                state.xmm4 = Sse2.LoadVector128(rcx + 0x40);
                state.xmm5 = Sse2.LoadVector128(rcx + 0x50);
                state.xmm6 = Sse2.LoadVector128(rcx + 0x60);
                state.xmm7 = Sse2.LoadVector128(rcx + 0x70);

#if MEOW_DUMP
                MEOW_DUMP_STATE("Seed", state.xmm0, state.xmm1, state.xmm2, state.xmm3, state.xmm4, state.xmm5, state.xmm6, state.xmm7);
#endif

                state.BufferLen = 0;
                state.TotalLengthInBytes = 0;
                state.Buffer = new byte[State.c_bufferSize + 32]; // NOTE(tvandijck): +32,  So we know we can over-read Buffer as necessary
            }
        }

        private static unsafe void AbsorbBlocks(ref State state, int blockCount, ReadOnlySpan<byte> bytes)
        {
            Vector128<byte> xmm0 = state.xmm0;
            Vector128<byte> xmm1 = state.xmm1;
            Vector128<byte> xmm2 = state.xmm2;
            Vector128<byte> xmm3 = state.xmm3;
            Vector128<byte> xmm4 = state.xmm4;
            Vector128<byte> xmm5 = state.xmm5;
            Vector128<byte> xmm6 = state.xmm6;
            Vector128<byte> xmm7 = state.xmm7;

            fixed (byte* bytesPtr = bytes)
            {
                byte* rax = bytesPtr;
                if (blockCount > MEOW_PREFETCH_LIMIT)
                {
                    while (blockCount-- > 0)
                    {
                        Sse.Prefetch0(rax + MEOW_PREFETCH + 0x00);
                        Sse.Prefetch0(rax + MEOW_PREFETCH + 0x40);
                        Sse.Prefetch0(rax + MEOW_PREFETCH + 0x80);
                        Sse.Prefetch0(rax + MEOW_PREFETCH + 0xc0);

                        MEOW_MIX(ref xmm0, ref xmm4, ref xmm6, ref xmm1, ref xmm2, rax + 0x00);
                        MEOW_MIX(ref xmm1, ref xmm5, ref xmm7, ref xmm2, ref xmm3, rax + 0x20);
                        MEOW_MIX(ref xmm2, ref xmm6, ref xmm0, ref xmm3, ref xmm4, rax + 0x40);
                        MEOW_MIX(ref xmm3, ref xmm7, ref xmm1, ref xmm4, ref xmm5, rax + 0x60);
                        MEOW_MIX(ref xmm4, ref xmm0, ref xmm2, ref xmm5, ref xmm6, rax + 0x80);
                        MEOW_MIX(ref xmm5, ref xmm1, ref xmm3, ref xmm6, ref xmm7, rax + 0xa0);
                        MEOW_MIX(ref xmm6, ref xmm2, ref xmm4, ref xmm7, ref xmm0, rax + 0xc0);
                        MEOW_MIX(ref xmm7, ref xmm3, ref xmm5, ref xmm0, ref xmm1, rax + 0xe0);

                        rax += 0x100;
                    }
                }
                else
                {
                    while (blockCount-- > 0)
                    {
                        MEOW_MIX(ref xmm0, ref xmm4, ref xmm6, ref xmm1, ref xmm2, rax + 0x00);
                        MEOW_MIX(ref xmm1, ref xmm5, ref xmm7, ref xmm2, ref xmm3, rax + 0x20);
                        MEOW_MIX(ref xmm2, ref xmm6, ref xmm0, ref xmm3, ref xmm4, rax + 0x40);
                        MEOW_MIX(ref xmm3, ref xmm7, ref xmm1, ref xmm4, ref xmm5, rax + 0x60);
                        MEOW_MIX(ref xmm4, ref xmm0, ref xmm2, ref xmm5, ref xmm6, rax + 0x80);
                        MEOW_MIX(ref xmm5, ref xmm1, ref xmm3, ref xmm6, ref xmm7, rax + 0xa0);
                        MEOW_MIX(ref xmm6, ref xmm2, ref xmm4, ref xmm7, ref xmm0, rax + 0xc0);
                        MEOW_MIX(ref xmm7, ref xmm3, ref xmm5, ref xmm0, ref xmm1, rax + 0xe0);

                        rax += 0x100;
                    }
                }
            }

            state.xmm0 = xmm0;
            state.xmm1 = xmm1;
            state.xmm2 = xmm2;
            state.xmm3 = xmm3;
            state.xmm4 = xmm4;
            state.xmm5 = xmm5;
            state.xmm6 = xmm6;
            state.xmm7 = xmm7;
        }

        public static unsafe void Absorb(ref State state, ReadOnlySpan<byte> sourceInit)
        {
            int Len = sourceInit.Length;
            state.TotalLengthInBytes += Len;

            fixed (byte* sourceInitPtr = sourceInit)
            {
                byte* source = sourceInitPtr;

                // NOTE(casey): Handle any buffered residual
                if (state.BufferLen > 0)
                {
                    int Fill = Math.Min(Len, State.c_bufferSize - state.BufferLen);

                    Len -= Fill;
                    while (Fill-- > 0)
                    {
                        state.Buffer[state.BufferLen++] = *source++;
                    }

                    if (state.BufferLen == State.c_bufferSize)
                    {
                        AbsorbBlocks(ref state, 1, state.Buffer);
                        state.BufferLen = 0;
                    }
                }

                // NOTE(casey): Handle any full blocks
                int BlockCount = (Len >> 8);
                int Advance = (BlockCount << 8);
                AbsorbBlocks(ref state, BlockCount, new ReadOnlySpan<byte>(source, Advance));

                Len -= Advance;
                source += Advance;

                // NOTE(casey): Store residual
                while (Len-- > 0)
                {
                    state.Buffer[state.BufferLen++] = *source++;
                }
            }
        }

        public static unsafe Vector128<byte> End(ref State state, Span<byte> store128)
        {
            long Len = state.TotalLengthInBytes;

            Vector128<byte> xmm0 = state.xmm0;
            Vector128<byte> xmm1 = state.xmm1;
            Vector128<byte> xmm2 = state.xmm2;
            Vector128<byte> xmm3 = state.xmm3;
            Vector128<byte> xmm4 = state.xmm4;
            Vector128<byte> xmm5 = state.xmm5;
            Vector128<byte> xmm6 = state.xmm6;
            Vector128<byte> xmm7 = state.xmm7;

            Vector128<byte> xmm8, xmm9, xmm10, xmm11, xmm12, xmm13, xmm14, xmm15;

            fixed (byte* rax = state.Buffer)
            {
                xmm9 = Vector128<byte>.Zero;
                xmm11 = Vector128<byte>.Zero;

                byte* Last = (byte*)rax + (Len & 0xf0);
                long Len8 = (Len & 0xf);
                if (Len8 > 0)
                {
                    fixed (byte* MeowMaskLen = s_meowMaskLen)
                    {
                        xmm8 = Sse2.LoadVector128(&MeowMaskLen[0x10 - Len8]);
                    }
                    xmm9 = Sse2.LoadVector128(Last);
                    xmm9 = Sse2.And(xmm9, xmm8);
                }

                if ((Len & 0x10) != 0)
                {
                    xmm11 = xmm9;
                    xmm9 = Sse2.LoadVector128(Last - 0x10);
                }


                xmm8 = xmm9;
                xmm10 = xmm9;
                xmm8 = Ssse3.AlignRight(xmm8, xmm11, 15);
                xmm10 = Ssse3.AlignRight(xmm10, xmm11, 1);

                xmm12 = Vector128<byte>.Zero;
                xmm13 = Vector128<byte>.Zero;
                xmm14 = Vector128<byte>.Zero;
                xmm15 = Vector128.Create((ulong)Len, 0).AsByte();
                xmm12 = Ssse3.AlignRight(xmm12, xmm15, 15);
                xmm14 = Ssse3.AlignRight(xmm14, xmm15, 1);

#if MEOW_DUMP
                MEOW_DUMP_STATE("PostBlocks", xmm0, xmm1, xmm2, xmm3, xmm4, xmm5, xmm6, xmm7);
                MEOW_DUMP_STATE("Residuals", xmm8, xmm9, xmm10, xmm11, xmm12, xmm13, xmm14, xmm15);
#endif

                // NOTE(casey): To maintain the mix-down pattern, we always Meow Mix the less-than-32-byte residual, even if it was empty
                MEOW_MIX_REG(ref xmm0, ref xmm4, ref xmm6, ref xmm1, ref xmm2, xmm8, xmm9, xmm10, xmm11);

                // NOTE(casey): Append the length, to avoid problems with our 32-byte padding
                MEOW_MIX_REG(ref xmm1, ref xmm5, ref xmm7, ref xmm2, ref xmm3, xmm12, xmm13, xmm14, xmm15);

#if MEOW_DUMP
                MEOW_DUMP_STATE("PostAppend", xmm0, xmm1, xmm2, xmm3, xmm4, xmm5, xmm6, xmm7);
#endif

                //
                // NOTE(casey): Hash all full 32-byte blocks
                //
                long LaneCount = (Len >> 5) & 0x7;
                if (LaneCount == 0) goto MixDown; MEOW_MIX(ref xmm2, ref xmm6, ref xmm0, ref xmm3, ref xmm4, rax + 0x00); --LaneCount;
                if (LaneCount == 0) goto MixDown; MEOW_MIX(ref xmm3, ref xmm7, ref xmm1, ref xmm4, ref xmm5, rax + 0x20); --LaneCount;
                if (LaneCount == 0) goto MixDown; MEOW_MIX(ref xmm4, ref xmm0, ref xmm2, ref xmm5, ref xmm6, rax + 0x40); --LaneCount;
                if (LaneCount == 0) goto MixDown; MEOW_MIX(ref xmm5, ref xmm1, ref xmm3, ref xmm6, ref xmm7, rax + 0x60); --LaneCount;
                if (LaneCount == 0) goto MixDown; MEOW_MIX(ref xmm6, ref xmm2, ref xmm4, ref xmm7, ref xmm0, rax + 0x80); --LaneCount;
                if (LaneCount == 0) goto MixDown; MEOW_MIX(ref xmm7, ref xmm3, ref xmm5, ref xmm0, ref xmm1, rax + 0xa0); --LaneCount;
                if (LaneCount == 0) goto MixDown; MEOW_MIX(ref xmm0, ref xmm4, ref xmm6, ref xmm1, ref xmm2, rax + 0xc0); --LaneCount;

            //
            // NOTE(casey): Mix the eight lanes down to one 128-bit hash
            //

            MixDown:

#if MEOW_DUMP
                MEOW_DUMP_STATE("PostLanes", xmm0, xmm1, xmm2, xmm3, xmm4, xmm5, xmm6, xmm7);
#endif

                MEOW_SHUFFLE(ref xmm0, ref xmm1, xmm2, ref xmm4, ref xmm5, xmm6);
                MEOW_SHUFFLE(ref xmm1, ref xmm2, xmm3, ref xmm5, ref xmm6, xmm7);
                MEOW_SHUFFLE(ref xmm2, ref xmm3, xmm4, ref xmm6, ref xmm7, xmm0);
                MEOW_SHUFFLE(ref xmm3, ref xmm4, xmm5, ref xmm7, ref xmm0, xmm1);
                MEOW_SHUFFLE(ref xmm4, ref xmm5, xmm6, ref xmm0, ref xmm1, xmm2);
                MEOW_SHUFFLE(ref xmm5, ref xmm6, xmm7, ref xmm1, ref xmm2, xmm3);
                MEOW_SHUFFLE(ref xmm6, ref xmm7, xmm0, ref xmm2, ref xmm3, xmm4);
                MEOW_SHUFFLE(ref xmm7, ref xmm0, xmm1, ref xmm3, ref xmm4, xmm5);
                MEOW_SHUFFLE(ref xmm0, ref xmm1, xmm2, ref xmm4, ref xmm5, xmm6);
                MEOW_SHUFFLE(ref xmm1, ref xmm2, xmm3, ref xmm5, ref xmm6, xmm7);
                MEOW_SHUFFLE(ref xmm2, ref xmm3, xmm4, ref xmm6, ref xmm7, xmm0);
                MEOW_SHUFFLE(ref xmm3, ref xmm4, xmm5, ref xmm7, ref xmm0, xmm1);

#if MEOW_DUMP
                MEOW_DUMP_STATE("PostMix", xmm0, xmm1, xmm2, xmm3, xmm4, xmm5, xmm6, xmm7);
#endif

                if (store128 != null)
                {
                    fixed (byte* store128Ptr = store128)
                    {
                        Sse2.Store(store128Ptr + 0x00, xmm0);
                        Sse2.Store(store128Ptr + 0x10, xmm1);
                        Sse2.Store(store128Ptr + 0x20, xmm2);
                        Sse2.Store(store128Ptr + 0x30, xmm3);
                        Sse2.Store(store128Ptr + 0x40, xmm4);
                        Sse2.Store(store128Ptr + 0x50, xmm5);
                        Sse2.Store(store128Ptr + 0x60, xmm6);
                        Sse2.Store(store128Ptr + 0x70, xmm7);
                    }
                }

                xmm0 = AddQ(xmm0, xmm2);
                xmm1 = AddQ(xmm1, xmm3);
                xmm4 = AddQ(xmm4, xmm6);
                xmm5 = AddQ(xmm5, xmm7);
                xmm0 = Sse2.Xor(xmm0, xmm1);
                xmm4 = Sse2.Xor(xmm4, xmm5);
                xmm0 = AddQ(xmm0, xmm4);

#if MEOW_DUMP
                MEOW_DUMP_STATE("PostFold", xmm0, xmm1, xmm2, xmm3, xmm4, xmm5, xmm6, xmm7);
#endif

                return xmm0;
            }
        }

        //
        // NOTE(casey): If you need to create your own seed from non-random data, you can use MeowExpandSeed
        // to create a seed which you then store for repeated use.  It is _expensive_ to generate the seed,
        // so you do not want to do this every time you hash.  You _only_ want to do it when you actually
        // need to create a new seed.
        //

        public static void ExpandSeed(ReadOnlySpan<byte> input, Span<byte> seedResult)
        {
            State state = new State();
            long lengthTab = input.Length; // NOTE(casey): We need to always injest 8-byte lengths exactly, even on 32-bit builds, to ensure identical results
            long injestCount = (256 / input.Length) + 2;

            Begin(ref state, MeowDefaultSeed);
            Absorb(ref state, BitConverter.GetBytes(lengthTab));
            while (injestCount-- > 0)
            {
                Absorb(ref state, input);
            }
            End(ref state, seedResult);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]

        private static void MEOW_MIX_REG(ref Vector128<byte> r1, ref Vector128<byte> r2, ref Vector128<byte> r3, ref Vector128<byte> r4, ref Vector128<byte> r5, Vector128<byte> i1, Vector128<byte> i2, Vector128<byte> i3, Vector128<byte> i4)
        {
            r1 = Aes.Decrypt(r1, r2);
            //INSTRUCTION_REORDER_BARRIER; 
            r3 = AddQ(r3, i1);
            r2 = Sse2.Xor(r2, i2);
            r2 = Aes.Decrypt(r2, r4);
            //INSTRUCTION_REORDER_BARRIER; 
            r5 = AddQ(r5, i3);
            r4 = Sse2.Xor(r4, i4);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void MEOW_MIX(ref Vector128<byte> r1, ref Vector128<byte> r2, ref Vector128<byte> r3, ref Vector128<byte> r4, ref Vector128<byte> r5, byte* ptr)
        {
            MEOW_MIX_REG(ref r1, ref r2, ref r3, ref r4, ref r5,
                Sse2.LoadVector128(ptr + 15),
                Sse2.LoadVector128(ptr + 0),
                Sse2.LoadVector128(ptr + 1),
                Sse2.LoadVector128(ptr + 16)
                );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MEOW_SHUFFLE(ref Vector128<byte> r1, ref Vector128<byte> r2, Vector128<byte> r3, ref Vector128<byte> r4, ref Vector128<byte> r5, Vector128<byte> r6)
        {
            r1 = Aes.Decrypt(r1, r4);
            r2 = AddQ(r2, r5);
            r4 = Sse2.Xor(r4, r6);
            r4 = Aes.Decrypt(r4, r2);
            r5 = AddQ(r5, r6);
            r2 = Sse2.Xor(r2, r3);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> AddQ(Vector128<byte> r1, Vector128<byte> r2)
        {
            return Sse2.Add(r1.AsUInt64(), r2.AsUInt64()).AsByte();
        }

#if MEOW_DUMP
        public struct Dump
        {
            public Vector128<byte> xmm0;
            public Vector128<byte> xmm1;
            public Vector128<byte> xmm2;
            public Vector128<byte> xmm3;
            public Vector128<byte> xmm4;
            public Vector128<byte> xmm5;
            public Vector128<byte> xmm6;
            public Vector128<byte> xmm7;
            public string Title;
        }

        public static List<Dump> DumpTo { get; set; }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void MEOW_DUMP_STATE(string title, Vector128<byte> xmm0, Vector128<byte> xmm1, Vector128<byte> xmm2, Vector128<byte> xmm3, Vector128<byte> xmm4, Vector128<byte> xmm5, Vector128<byte> xmm6, Vector128<byte> xmm7)
        {
            DumpTo?.Add(new Dump
            {
                xmm0 = xmm0,
                xmm1 = xmm1,
                xmm2 = xmm2,
                xmm3 = xmm3,
                xmm4 = xmm4,
                xmm5 = xmm5,
                xmm6 = xmm6,
                xmm7 = xmm7,
                Title = title
            });
        }
#endif
    }
}
