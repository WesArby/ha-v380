using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace V380Decoder.src
{
    public class StatusResponse
    {
        public string status { get; set; } = default!;
        public DateTime timestamp { get; set; }
        public int power_source { get; set; } = -1;
        public int video_standard { get; set; } = -1;
        public int speaker_vol { get; set; } = -1;
        public int xparam_rtsp { get; set; } = -1;
        public int xparam_vtype { get; set; } = -1;
        public string wifi { get; set; } = "";
        public string battery { get; set; } = "";
    }
}