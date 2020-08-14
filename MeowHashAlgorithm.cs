using System;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;

namespace Meow
{
    public sealed class MeowHashAlgorithm : HashAlgorithm
    {
        private MeowHash.State m_state = new MeowHash.State();
        private byte[] m_seed = MeowHash.MeowDefaultSeed;

        public MeowHashAlgorithm()
        {
            HashSizeValue = 128;
            State = -1;
        }

        public byte[] Seed 
        { 
            get { return m_seed; }
            set
            {
                if (value != null && value.Length != 128)
                {
                    throw new Exception("Seed must be 128 bytes.");
                }
                m_seed = value ?? MeowHash.MeowDefaultSeed;
            }
        }

        public override void Initialize()
        {
            MeowHash.Begin(ref m_state, m_seed);
        }

        protected override void HashCore(byte[] array, int ibStart, int cbSize)
        {
            HashCore(array.AsSpan().Slice(ibStart, cbSize));
        }

        protected override void HashCore(ReadOnlySpan<byte> source)
        {
            MeowHash.Absorb(ref m_state, source);
        }

        protected unsafe override byte[] HashFinal()
        {
            var result = MeowHash.End(ref m_state, null);

            HashValue = new byte[16];
            fixed (byte* outputPtr = HashValue)
            {
                Sse2.Store(outputPtr, result);
            }
            return HashValue;
        }
    }
}
