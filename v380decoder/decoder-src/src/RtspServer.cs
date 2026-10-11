using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace V380Decoder.src
{
    public class RtspServer
    {
        private readonly int port;
        private readonly H264Transcoder transcoder;
        private TcpListener listener;
        private Thread acceptThread;
        private volatile bool running;

        private readonly ConcurrentDictionary<int, RtspSession> sessions = new();
        private int nextId;

        private byte[] cachedSps;
        private byte[] cachedPps;
        private byte[] cachedVps;
        private string cachedAudioConfig = "1588"; // AAC-LC 8kHz mono (verified); replaced live per ADTS
        private VideoCodec streamCodec = VideoCodec.Unknown;
        private readonly object sdpLock = new();

        public void ReportAudioConfig(byte sync1, byte profileByte, byte freqByte)
        {
            // ADTS bytes 1..3 (after 0xFF sync): profile(2) sfi(4) chcfg(3);
            // AOT = profile + 1. ASC = AOT(5) sfi(4) ch(4).
            try
            {
                int profile = (profileByte >> 6) & 0x03;
                int aot = profile + 1;
                if (aot is < 1 or > 4) return;
                int sfi = (profileByte >> 2) & 0x0F;
                int ch = ((profileByte & 0x01) << 2) | ((freqByte >> 6) & 0x03);
                byte asc0 = (byte)((aot << 3) | (sfi >> 1));
                byte asc1 = (byte)(((sfi & 0x01) << 7) | (ch << 3));
                lock (sdpLock)
                {
                    cachedAudioConfig = $"{asc0:X2}{asc1:X2}";
                }
            }
            catch { }
        }

        public RtspServer(int port)
        {
            this.port = port;
            transcoder = new H264Transcoder(PushVideoDirect);
        }

        public void Start()
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start(10);
            running = true;
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "rtsp-accept" };
            acceptThread.Start();
            Console.Error.WriteLine($"[RTSP] rtsp://{NetworkHelper.GetLocalIPAddress()}:{port}/live");
        }

        void AcceptLoop()
        {
            while (running)
            {
                try
                {
                    var tcp = listener.AcceptTcpClient();
                    tcp.NoDelay = true;
                    int id = Interlocked.Increment(ref nextId);
                    var s = new RtspSession(id, tcp, this);
                    sessions[id] = s;
                    s.Start();
                    s.OnClose += () => sessions.TryRemove(id, out _);
                }
                catch { }
            }
        }

        public void PushVideo(FrameData f)
        {
            // H265 is served natively (VLC/Agent DVR/Scrypted all handle it).
            // Re-encoding via H264Transcoder drops pictures on this firmware,
            // so only non-H265 streams go through the transcoder path.
            if (f.Codec == VideoCodec.H265)
            {
                PushVideoDirect(f);
                return;
            }

            if (transcoder.IsAvailable)
            {
                transcoder.PushFrame(f);
                return;
            }

            PushVideoDirect(f);
        }

        private void PushVideoDirect(FrameData f)
        {
            CacheSpsPps(f.Payload, f.Codec);
            foreach (var s in sessions.Values) s.PushVideo(f);
        }

        public void PushAudio(FrameData f)
        {
            foreach (var s in sessions.Values) s.PushAudio(f);
        }

        void CacheSpsPps(byte[] data, VideoCodec codec)
        {
            lock (sdpLock)
            {
                if (codec == VideoCodec.H265)
                {
                    streamCodec = VideoCodec.H265;
                    if (cachedVps != null && cachedSps != null && cachedPps != null) return;
                    ParseNals(data, VideoCodec.H265, (nalType, nal) =>
                    {
                        if (nalType == 32 && cachedVps == null) cachedVps = nal;
                        if (nalType == 33 && cachedSps == null) cachedSps = nal;
                        if (nalType == 34 && cachedPps == null) cachedPps = nal;
                    });
                    return;
                }

                streamCodec = VideoCodec.H264;
                if (cachedSps != null && cachedPps != null) return;

                ParseNals(data, VideoCodec.H264, (nalType, nal) =>
                {
                    if (nalType == 7 && cachedSps == null) cachedSps = nal;
                    if (nalType == 8 && cachedPps == null) cachedPps = nal;
                });
            }
        }

        internal static void ParseNals(byte[] data, VideoCodec codec, Action<int, byte[]> cb)
        {
            int i = 0;
            int len = data.Length;
            while (i < len)
            {
                int sc = FindStartCode(data, i);
                if (sc < 0) break;

                int scLen = (sc + 3 < len && data[sc + 2] == 1) ? 3 : 4;
                int nalStart = sc + scLen;
                if (nalStart >= len) break;

                int next = FindStartCode(data, nalStart);
                int nalEnd = next < 0 ? len : next;
                int nalType = codec == VideoCodec.H265
                    ? (data[nalStart] >> 1) & 0x3F
                    : data[nalStart] & 0x1F;

                var nal = new byte[nalEnd - nalStart];
                Array.Copy(data, nalStart, nal, 0, nal.Length);
                cb(nalType, nal);
                i = nalEnd;
            }
        }

        static int FindStartCode(byte[] d, int from)
        {
            for (int i = from; i + 3 < d.Length; i++)
            {
                if (d[i] == 0 && d[i + 1] == 0)
                {
                    if (d[i + 2] == 1) return i;
                    if (d[i + 2] == 0 && i + 3 < d.Length && d[i + 3] == 1) return i;
                }
            }
            return -1;
        }

        public string BuildSdp()
        {
            string videoBlock;
            lock (sdpLock)
            {
                if (streamCodec == VideoCodec.H265)
                {
                    string fmtp = "";
                    if (cachedVps != null && cachedSps != null && cachedPps != null)
                    {
                        fmtp = "a=fmtp:96 "
                             + $"sprop-vps={Convert.ToBase64String(cachedVps)};"
                             + $"sprop-sps={Convert.ToBase64String(cachedSps)};"
                             + $"sprop-pps={Convert.ToBase64String(cachedPps)}\r\n";
                    }
                    LogUtils.debug($"[RTSP] SDP codec=H265 hasVps={cachedVps != null} hasSps={cachedSps != null} hasPps={cachedPps != null}");
                    videoBlock =
                        "m=video 0 RTP/AVP 96\r\n" +
                        "a=rtpmap:96 H265/90000\r\n" +
                        fmtp +
                        "a=control:trackID=0\r\n";
                }
                else
                {
                    string fmtp = "";
                    if (cachedSps != null && cachedPps != null)
                    {
                        string spsB64 = Convert.ToBase64String(cachedSps);
                        string ppsB64 = Convert.ToBase64String(cachedPps);
                        string pli = cachedSps.Length >= 3
                            ? $"{cachedSps[0]:X2}{cachedSps[1]:X2}{cachedSps[2]:X2}"
                            : "64001F";
                        fmtp = $"a=fmtp:96 packetization-mode=1;sprop-parameter-sets={spsB64},{ppsB64};profile-level-id={pli}\r\n";
                    }
                    LogUtils.debug($"[RTSP] SDP codec=H264 hasSps={cachedSps != null} hasPps={cachedPps != null}");
                    videoBlock =
                        "m=video 0 RTP/AVP 96\r\n" +
                        "a=rtpmap:96 H264/90000\r\n" +
                        fmtp +
                        "a=control:trackID=0\r\n";
                }
            }

            string audioBlock;
            lock (sdpLock)
            {
                // Camera audio is AAC-LC (ADTS), served as RFC 3640 mpeg4-generic.
                audioBlock =
                    "m=audio 0 RTP/AVP 96\r\n" +
                    "a=rtpmap:96 mpeg4-generic/8000/1\r\n" +
                    "a=fmtp:96 streamtype=5;profile-level-id=1;mode=AAC-hbr;"
                    + $"config={cachedAudioConfig};SizeLength=13;IndexLength=3;IndexDeltaLength=3\r\n" +
                    "a=control:trackID=1\r\n";
            }

            return
                "v=0\r\n" +
                "o=- 1 1 IN IP4 0.0.0.0\r\n" +
                "s=V380 Live\r\n" +
                "t=0 0\r\n" +
                "a=recvonly\r\n" +
                videoBlock +
                audioBlock;
        }

        public void Dispose()
        {
            running = false;
            try { listener?.Stop(); } catch { }
            foreach (var s in sessions.Values) s.Close();
            transcoder.Dispose();
        }
    }
}
