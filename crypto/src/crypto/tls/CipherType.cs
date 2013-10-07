namespace Org.BouncyCastle.Crypto.Tls
{

    /**
     * RFC 2246
     * <p/>
     * Note that the values here are implementation-specific and arbitrary. It is recommended not to
     * depend on the particular values (e.g. serialization).
     */
    public class CipherType
    {

        public const int stream = 0;
        public const int block = 1;

        /*
         * RFC 5246
         */
        public const int aead = 2;
    }


}