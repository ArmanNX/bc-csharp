namespace Org.BouncyCastle.Crypto.Tls
{

    public class SRTPProtectionProfile
    {
        /*
         * RFC 5764 4.1.2.
         */
        public const int SRTP_AES128_CM_HMAC_SHA1_80 = 0x0001;
        public const int SRTP_AES128_CM_HMAC_SHA1_32 = 0x0002;
        public const int SRTP_NULL_HMAC_SHA1_80 = 0x0005;
        public const int SRTP_NULL_HMAC_SHA1_32 = 0x0006;
    }

}