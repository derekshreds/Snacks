using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Snacks
{
    /// <summary> User encoding options </summary>
    public class EncoderOptions
    {
        public string Format;
        public string Codec;
        public string Encoder;
        public int TargetBitrate;
        public bool TwoChannelAudio;
        public bool DeleteOriginalFile;
        public bool EnglishOnlyAudio;
        public bool EnglishOnlySubtitles;
        public bool RemoveBlackBorders;
        public bool RetryOnFail;
        public string OutputDirectory;
        public string EncodeDirectory;
        public bool StrictBitrate;
    }
}
