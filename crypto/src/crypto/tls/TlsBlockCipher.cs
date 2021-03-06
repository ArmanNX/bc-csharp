using System;
using System.IO;

using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;

namespace Org.BouncyCastle.Crypto.Tls
{
    /// <summary>
    /// A generic TLS 1.0 block cipher. This can be used for AES or 3DES for example.
    /// </summary>
    public class TlsBlockCipher
        : TlsCipher
    {
        private static bool encryptThenMAC = false;

        protected TlsContext context;
        protected byte[] randomData;
        protected bool useExplicitIV;

        protected IBlockCipher encryptCipher;
        protected IBlockCipher decryptCipher;

        protected TlsMac writeMac;
        protected TlsMac readMac;

        public virtual TlsMac WriteMac
        {
            get { return writeMac; }
        }

        public virtual TlsMac ReadMac
        {
            get { return readMac; }
        }

        public TlsBlockCipher(TlsContext context, IBlockCipher clientWriteCipher, IBlockCipher serverWriteCipher,
            IDigest clientWriteDigest, IDigest serverWriteDigest, int cipherKeySize)
        {
            this.context = context;

            this.randomData = new byte[256];
            context.SecureRandom.NextBytes(randomData);

            this.useExplicitIV = TlsUtilities.IsTLSv11(context);

            int key_block_size = (2 * cipherKeySize) + clientWriteDigest.GetDigestSize()
                + serverWriteDigest.GetDigestSize();

            // From TLS 1.1 onwards, block ciphers don't need client_write_IV
            if (!useExplicitIV)
            {
                key_block_size += clientWriteCipher.GetBlockSize() + serverWriteCipher.GetBlockSize();
            }

            byte[] key_block = TlsUtilities.CalculateKeyBlock(context, key_block_size);

            int offset = 0;

            TlsMac clientWriteMac = new TlsMac(context, clientWriteDigest, key_block, offset,
                clientWriteDigest.GetDigestSize());
            offset += clientWriteDigest.GetDigestSize();
            TlsMac serverWriteMac = new TlsMac(context, serverWriteDigest, key_block, offset,
                serverWriteDigest.GetDigestSize());
            offset += serverWriteDigest.GetDigestSize();

            KeyParameter client_write_key = new KeyParameter(key_block, offset, cipherKeySize);
            offset += cipherKeySize;
            KeyParameter server_write_key = new KeyParameter(key_block, offset, cipherKeySize);
            offset += cipherKeySize;

            byte[] client_write_IV, server_write_IV;
            if (useExplicitIV)
            {
                client_write_IV = new byte[clientWriteCipher.GetBlockSize()];
                server_write_IV = new byte[serverWriteCipher.GetBlockSize()];
            }
            else
            {
                client_write_IV = Arrays.CopyOfRange(key_block, offset, offset + clientWriteCipher.GetBlockSize());
                offset += clientWriteCipher.GetBlockSize();
                server_write_IV = Arrays.CopyOfRange(key_block, offset, offset + serverWriteCipher.GetBlockSize());
                offset += serverWriteCipher.GetBlockSize();
            }

            if (offset != key_block_size)
            {
                throw new TlsFatalAlert(AlertDescription.internal_error);
            }

            ICipherParameters encryptParams, decryptParams;
            if (context.IsServer)
            {
                this.writeMac = serverWriteMac;
                this.readMac = clientWriteMac;
                this.encryptCipher = serverWriteCipher;
                this.decryptCipher = clientWriteCipher;
                encryptParams = new ParametersWithIV(server_write_key, server_write_IV);
                decryptParams = new ParametersWithIV(client_write_key, client_write_IV);
            }
            else
            {
                this.writeMac = clientWriteMac;
                this.readMac = serverWriteMac;
                this.encryptCipher = clientWriteCipher;
                this.decryptCipher = serverWriteCipher;
                encryptParams = new ParametersWithIV(client_write_key, client_write_IV);
                decryptParams = new ParametersWithIV(server_write_key, server_write_IV);
            }

            this.encryptCipher.Init(true, encryptParams);
            this.decryptCipher.Init(false, decryptParams);
        }

        public int GetPlaintextLimit(int ciphertextLimit)
        {
            int blockSize = encryptCipher.GetBlockSize();
            int macSize = writeMac.Size;

            int plaintextLimit = ciphertextLimit;

            // An explicit IV consumes 1 block
            if (useExplicitIV)
            {
                plaintextLimit -= blockSize;
            }

            // Leave room for the MAC, and require block-alignment
            if (encryptThenMAC)
            {
                plaintextLimit -= macSize;
                plaintextLimit -= plaintextLimit % blockSize;
            }
            else
            {
                plaintextLimit -= plaintextLimit % blockSize;
                plaintextLimit -= macSize;
            }

            // Minimum 1 byte of padding
            --plaintextLimit;

            return plaintextLimit;
        }

        public byte[] EncodePlaintext(long seqNo, ContentType type, byte[] plaintext, int offset, int len, int outpufOffset)
        {
            int blockSize = encryptCipher.GetBlockSize();
            int macSize = writeMac.Size;

            ProtocolVersion version = context.ServerVersion;

            int enc_input_length = len;
            if (!encryptThenMAC)
            {
                enc_input_length += macSize;
            }
            int padding_length = blockSize - 1 - (enc_input_length % blockSize);

            // TODO[DTLS] Consider supporting in DTLS (without exceeding send limit though)
            if (!version.IsDTLS && !version.IsSSL)
            {
                // Add a random number of extra blocks worth of padding
                int maxExtraPadBlocks = (255 - padding_length) / blockSize;
                int actualExtraPadBlocks = ChooseExtraPadBlocks(context.SecureRandom, maxExtraPadBlocks);
                padding_length += actualExtraPadBlocks * blockSize;
            }

            int totalSize = len + macSize + padding_length + 1;
            if (useExplicitIV)
            {
                totalSize += blockSize;
            }

            byte[] outBuf = new byte[outpufOffset + totalSize];
            int outOff = outpufOffset;

            if (useExplicitIV)
            {
                byte[] explicitIV = new byte[blockSize];
                context.SecureRandom.NextBytes(explicitIV);

                encryptCipher.Init(true, new ParametersWithIV(null, explicitIV));

                Buffer.BlockCopy(explicitIV, 0, outBuf, outOff, blockSize);
                outOff += blockSize;
            }

            int blocks_start = outOff;

            Buffer.BlockCopy(plaintext, offset, outBuf, outOff, len);
            outOff += len;

            if (!encryptThenMAC)
            {
                byte[] mac = writeMac.CalculateMac(seqNo, type, plaintext, offset, len);
                Buffer.BlockCopy(mac, 0, outBuf, outOff, mac.Length);
                outOff += mac.Length;
            }

            for (int i = 0; i <= padding_length; i++)
            {
                outBuf[outOff++] = (byte)padding_length;
            }

            for (int i = blocks_start; i < outOff; i += blockSize)
            {
                encryptCipher.ProcessBlock(outBuf, i, outBuf, i);
            }

            if (encryptThenMAC)
            {
                byte[] mac = writeMac.CalculateMac(seqNo, type, outBuf, 0, outOff);
                Buffer.BlockCopy(mac, 0, outBuf, outOff, mac.Length);
                outOff += mac.Length;
            }

            //        assert outBuf.length == outOff;

            return outBuf;
        }

        public byte[] DecodeCiphertext(long seqNo, ContentType type, byte[] ciphertext, int offset, int len)
        {
            int blockSize = decryptCipher.GetBlockSize();
            int macSize = readMac.Size;

            int minLen = blockSize;
            if (encryptThenMAC)
            {
                minLen += macSize;
            }
            else
            {
                minLen = System.Math.Max(minLen, macSize + 1);
            }
            if (useExplicitIV)
            {
                minLen += blockSize;
            }

            if (len < minLen)
            {
                throw new TlsFatalAlert(AlertDescription.decode_error);
            }

            int blocks_length = len;
            if (encryptThenMAC)
            {
                blocks_length -= macSize;
            }

            if (blocks_length % blockSize != 0)
            {
                throw new TlsFatalAlert(AlertDescription.decryption_failed);
            }

            if (encryptThenMAC)
            {
                int end = offset + len;
                byte[] receivedMac = Arrays.CopyOfRange(ciphertext, end - macSize, end);
                byte[] calculatedMac = readMac.CalculateMac(seqNo, type, ciphertext, offset, len - macSize);

                bool badMac = !Arrays.ConstantTimeAreEqual(calculatedMac, receivedMac);

                if (badMac)
                {
                    throw new TlsFatalAlert(AlertDescription.bad_record_mac);
                }
            }

            if (useExplicitIV)
            {
                decryptCipher.Init(false, new ParametersWithIV(null, ciphertext, offset, blockSize));

                offset += blockSize;
                blocks_length -= blockSize;
            }

            for (int i = 0; i < blocks_length; i += blockSize)
            {
                decryptCipher.ProcessBlock(ciphertext, offset + i, ciphertext, offset + i);
            }

            // If there's anything wrong with the padding, this will return zero
            int totalPad = CheckPaddingConstantTime(ciphertext, offset, blocks_length, blockSize, encryptThenMAC ? 0 : macSize);

            int dec_output_length = blocks_length - totalPad;

            if (!encryptThenMAC)
            {
                dec_output_length -= macSize;
                int macInputLen = dec_output_length;
                int macOff = offset + macInputLen;
                byte[] receivedMac = Arrays.CopyOfRange(ciphertext, macOff, macOff + macSize);
                byte[] calculatedMac = readMac.CalculateMacConstantTime(seqNo, type, ciphertext, offset, macInputLen,
                    blocks_length - macSize, randomData);

                bool badMac = !Arrays.ConstantTimeAreEqual(calculatedMac, receivedMac);

                if (badMac || totalPad == 0)
                {
                    throw new TlsFatalAlert(AlertDescription.bad_record_mac);
                }
            }

            return Arrays.CopyOfRange(ciphertext, offset, offset + dec_output_length);
        }

        protected int CheckPaddingConstantTime(byte[] buf, int off, int len, int blockSize, int macSize)
        {
            int end = off + len;
            byte lastByte = buf[end - 1];
            int padlen = lastByte & 0xff;
            int totalPad = padlen + 1;

            int dummyIndex = 0;
            byte padDiff = 0;

            if ((context.ServerVersion.IsSSL && totalPad > blockSize) || (macSize + totalPad > len))
            {
                totalPad = 0;
            }
            else
            {
                int padPos = end - totalPad;
                do
                {
                    padDiff |= (byte)(buf[padPos++] ^ lastByte);
                }
                while (padPos < end);

                dummyIndex = totalPad;

                if (padDiff != 0)
                {
                    totalPad = 0;
                }
            }

            // Run some extra dummy checks so the number of checks is always constant
            {
                byte[] dummyPad = randomData;
                while (dummyIndex < 256)
                {
                    padDiff |= (byte)(dummyPad[dummyIndex++] ^ lastByte);
                }
                // Ensure the above loop is not eliminated
                dummyPad[0] ^= padDiff;
            }

            return totalPad;
        }

        protected virtual int ChooseExtraPadBlocks(SecureRandom r, int max)
        {
            //			return r.NextInt(max + 1);

            uint x = (uint)r.NextInt();
            int n = LowestBitSet(x);
            return System.Math.Min(n, max);
        }

        private int LowestBitSet(uint x)
        {
            if (x == 0)
            {
                return 32;
            }

            int n = 0;
            while ((x & 1) == 0)
            {
                ++n;
                x >>= 1;
            }
            return n;
        }
    }
}
