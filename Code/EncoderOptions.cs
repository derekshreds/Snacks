using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Snacks
{
    public class EncoderOptions
    {
        public string Encoder;
        public int TargetBitrate;
        public bool TwoChannelAudio;
        public bool DeleteOriginalFile;
        public bool EnglishOnlyAudio;
        public bool EnglishOnlySubtitles;
        public bool RetryOnFail;
        public string OutputDirectory;
        public string EncodeDirectory;
        public bool StrictBitrate;
    }
}
