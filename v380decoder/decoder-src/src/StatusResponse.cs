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
        public int login_battery { get; set; } = -1;
        public int pir_wake_time { get; set; } = -1;
        public int pir_wake_interval { get; set; } = -1;
        public string wifi { get; set; } = "";
        public string battery { get; set; } = "";
        // Telemetry plane (frames 0x5B/0x5C). -1 / empty means "not received", which is
        // the expected state whenever communicationVersion < 31 (see telemetry_gate).
        public int comm_version { get; set; } = -1;
        public bool telemetry_enabled { get; set; }
        public short wifi_signal_raw { get; set; } = short.MinValue;
        public int wifi_signal_indicator { get; set; } = -1;
        public int wifi_channel { get; set; } = -1;
        public int battery_percent { get; set; } = -1;
        public int battery_power_state { get; set; } = -1;
        public string battery_power_state_name { get; set; } = "";
    }
}