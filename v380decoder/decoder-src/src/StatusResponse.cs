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
        public int pir { get; set; } = -1;
        public int lamplight { get; set; } = -1;
        public int ptz_h { get; set; } = -1;
        public int ptz_v { get; set; } = -1;
        public string fw_app { get; set; } = "";
        public string fw_app_date { get; set; } = "";
        public string fw_hw { get; set; } = "";
        public string fw_hw_date { get; set; } = "";
        public string device_time { get; set; } = "";
        public int tz_index { get; set; } = -1;
        public int pir_wake_time { get; set; } = -1;
        public int pir_wake_interval { get; set; } = -1;
        public string wifi { get; set; } = "";
        public string battery { get; set; } = "";
    }
}