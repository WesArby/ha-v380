using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace V380Decoder.src
{
    public class V380Client : IDisposable
    {
        public SnapshotManager snapshotManager { get; private set; }
        private TcpClient authClient, streamClient;
        private NetworkStream authStream, streamStream;
        private readonly string ip;
        private readonly int port;
        private readonly uint deviceId;
        private readonly string username, password;
        private readonly SourceStream source;
        private readonly OutputMode mode;
        private readonly int streamQuality;
        private readonly int audioFlag;
        private readonly ushort startSubcode;
        private uint authTicket, sessionId;
        private ushort deviceVersion, communicationVersion;
        private int frameWidth = 1280;
        private int frameheight = 720;
        private byte[] aesKey = new byte[16];
        private bool needReconnect = false;
        private int extractLogCount = 0;
        private VideoCodec lastVideoCodec = VideoCodec.H264;

        // Device monitor (XParam GET 347, best-effort) + stream telemetry.
        public int PowerSource { get; private set; } = -1;
        public int VideoStandard { get; private set; } = -1;
        public int SpeakerVol { get; private set; } = -1;
        public int XParamRtspEnable { get; private set; } = -1;
        public int XParamVideoType { get; private set; } = -1;
        public string LastWifiHex { get; private set; } = "";
        public string LastBatteryHex { get; private set; } = "";
        public DateTime LastWifiAt { get; private set; }
        public DateTime LastBatteryAt { get; private set; }

        private sealed class FragAssembly
        {
            public readonly List<byte> Frags = new List<byte>();
            public ushort Total;
            public ushort Next;
            public bool Assembling;
            public readonly byte[] FirstHeader = new byte[12];
        }

        public V380Client(string ip, int port, uint deviceId, string username, string password, SourceStream source, OutputMode mode, int streamQuality, int audioFlag = 4097, int startSubcode = 0x3001)
        {
            this.ip = ip;
            this.port = port;
            this.deviceId = deviceId;
            this.username = username;
            this.password = password;
            this.source = source;
            this.mode = mode;
            this.streamQuality = streamQuality;
            this.audioFlag = audioFlag;
            this.startSubcode = (ushort)startSubcode;
            if (mode == OutputMode.Rtsp)
                snapshotManager = new SnapshotManager();
        }

        public void Run(RtspServer rtsp, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    int auth = GetAuthTicket();
                    if (auth == 0) continue;
                    if (auth == -1) break;

                    try { FetchDeviceParams(); } catch { }

                    if (!StreamLogin())
                    {
                        Console.Error.WriteLine("[STREAM] Retrying...");
                        streamStream?.Close(); streamClient?.Close();
                        continue;
                    }

                    if (!StartStream())
                    {
                        Console.Error.WriteLine("[STREAM] Retrying...");
                        continue;
                    }

                    ReceiveFrames(mode, rtsp, ct);
                }
                catch (OperationCanceledException)
                {
                    Console.Error.WriteLine("[STREAM] Operation cancelled");
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ERROR] Fatal error: {ex.Message}");
                    Console.Error.WriteLine($"[ERROR] Stack trace: {ex.StackTrace}");
                    Thread.Sleep(3000);
                }
                finally
                {
                    Console.Error.WriteLine("[STREAM] Closing connection...");
                    streamStream?.Close(); streamClient?.Close();
                }
            }
        }

        public int GetAuthTicket()
        {
            try
            {
                authClient = new TcpClient();
                var r = authClient.BeginConnect(ip, port, null, null);
                if (!r.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
                {
                    Console.Error.WriteLine("[AUTH] failed connecting to socket. retrying...");
                    return 0;
                }
                authClient.EndConnect(r);
                authStream = authClient.GetStream();

                var encryptedPassword = GeneratePassword(password);
                var userBytes = Encoding.ASCII.GetBytes(username);
                var cmd1167 = new byte[520];
                WriteUInt32LE(cmd1167, 0, 1167); // command
                if (source == SourceStream.Lan)
                {
                    WriteUInt32LE(cmd1167, 4, 120); // unknown1
                    cmd1167[8] = 31; // unknown2 (version?)
                    WriteUInt32LE(cmd1167, 9, 1); // unknown3            
                    WriteUInt32LE(cmd1167, 13, deviceId); // deviceId
                    Array.Copy(userBytes, 0, cmd1167, 49, Math.Min(userBytes.Length, 32)); //username
                    Array.Copy(encryptedPassword, 0, cmd1167, 81, Math.Min(encryptedPassword.Length, 64)); //password
                }
                else
                {
                    WriteUInt32LE(cmd1167, 4, 1022); // unknown1
                    cmd1167[8] = 31; // unknown2 (version?)
                    WriteUInt32LE(cmd1167, 9, 1); // unknown3 
                    WriteUInt32LE(cmd1167, 13, deviceId); // deviceId
                    var hostnameBytes = Encoding.ASCII.GetBytes($"{deviceId}.nvdvr.net");
                    Array.Copy(hostnameBytes, 0, cmd1167, 17, Math.Min(hostnameBytes.Length, 50)); //hostname
                    WriteUInt32LE(cmd1167, 67, (uint)port); //port
                    Array.Copy(userBytes, 0, cmd1167, 71, Math.Min(userBytes.Length, 32)); //username
                    Array.Copy(encryptedPassword, 0, cmd1167, 103, Math.Min(encryptedPassword.Length, 64)); //password
                }
                if (!SendData(authStream, cmd1167))
                {
                    Console.Error.WriteLine("[AUTH] failed send request. retrying...");
                    return 0;
                }

                var resp = ReceiveData(authStream, 256);
                if (resp == null || resp.Length < 256)
                {
                    Console.Error.WriteLine("[AUTH] failed receive response. retrying...");
                    return 0;
                }

                uint respCmd = ReadUInt32LE(resp, 0);
                if (respCmd != 1168)
                {
                    Console.Error.WriteLine($"[AUTH] invalid response cmd: {respCmd} (expected 1168). retrying...");
                    return 0;
                }
                uint loginResult = ReadUInt32LE(resp, 4);
                if (loginResult != 1001)
                {
                    if (loginResult == 1011)
                        Console.Error.WriteLine($"[AUTH] invalid username. exiting...");
                    else if (loginResult == 1012)
                        Console.Error.WriteLine($"[AUTH] invalid password. exiting...");
                    else if (loginResult == 1018)
                        Console.Error.WriteLine($"[AUTH] invalid device id. exiting...");
                    else
                        Console.Error.WriteLine($"[AUTH] login failed result: {loginResult} (expected 1001). exiting...");

                    return -1;
                }

                uint resultValue = ReadUInt32LE(resp, 8);
                byte version = resp[12];
                uint ticket = ReadUInt32LE(resp, 13);
                uint session = ReadUInt32LE(resp, 17);
                byte deviceType = resp[21];
                byte camType = resp[22];
                uint vendorId = ReadUInt16LE(resp, 23);
                uint isDomainExists = resp[25];
                byte[] domainBytes = new byte[32];
                Array.Copy(resp, 26, domainBytes, 0, 32);
                string domain = Encoding.ASCII.GetString(domainBytes).TrimEnd('\0');

                LogUtils.debug($"[AUTH] response success");
                LogUtils.debug($"[AUTH] cmd: {respCmd}");
                LogUtils.debug($"[AUTH] result: {loginResult}");
                LogUtils.debug($"[AUTH] authTicket: {ticket} (0x{ticket:X})");
                LogUtils.debug($"[AUTH] resultValue: {resultValue}");
                LogUtils.debug($"[AUTH] version: {version}");
                LogUtils.debug($"[AUTH] session: {session}");
                LogUtils.debug($"[AUTH] deviceType: {deviceType}");
                LogUtils.debug($"[AUTH] camType: {camType}");
                LogUtils.debug($"[AUTH] vendorId: {vendorId}");
                LogUtils.debug($"[AUTH] isDomainExists: {isDomainExists}");
                LogUtils.debug($"[AUTH] domain: {domain}");

                deviceVersion = version;
                sessionId = session;
                authTicket = ticket;

                Console.Error.WriteLine($"[AUTH] success ticket={authTicket} deviceVersion={deviceVersion}");
                authStream.Close(); authClient.Close();
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AUTH] Error: {ex.Message}");
                return 0;
            }
            finally
            {
                authStream?.Close(); authClient?.Close();
            }
        }


        public bool StreamLogin()
        {
            streamClient = new TcpClient
            {
                NoDelay = true,
                ReceiveBufferSize = 0x8000
            };
            var r = streamClient.BeginConnect(ip, port, null, null);
            if (!r.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
            {
                Console.Error.WriteLine("[STREAM] failed connecting to socket. retrying...");
                return false;
            }
            ;
            streamClient.EndConnect(r);
            streamStream = streamClient.GetStream();

            var cmd301 = new byte[256];
            WriteUInt32LE(cmd301, 0, 301);
            if (source == SourceStream.Lan)
            {
                WriteUInt32LE(cmd301, 4, deviceId); //device id
                WriteUInt32LE(cmd301, 8, 0); //unknown1 
                WriteUInt16LE(cmd301, 12, 20); //unknown2 
                WriteUInt32LE(cmd301, 14, authTicket); //auth ticket
                WriteUInt32LE(cmd301, 22, (uint)audioFlag); //audio 4096=off, 4097=on
                WriteUInt32LE(cmd301, 26, (uint)streamQuality);    //quality  0=SD, 1=HD
            }
            else
            {
                WriteUInt32LE(cmd301, 4, 1022); //unknown1 
                var hostnameBytes = Encoding.ASCII.GetBytes($"{deviceId}.nvdvr.net");
                Array.Copy(hostnameBytes, 0, cmd301, 8, Math.Min(hostnameBytes.Length, 50)); //hostname
                WriteUInt32LE(cmd301, 58, (uint)port); //port
                WriteUInt32LE(cmd301, 62, deviceId); //device id
                WriteUInt32LE(cmd301, 66, authTicket); // auth ticket
                WriteUInt32LE(cmd301, 70, sessionId); // session id
                WriteUInt32LE(cmd301, 74, (uint)streamQuality); //quality 0=SD, 1=HD
                cmd301[78] = 20; //unknown2 
                WriteUInt32LE(cmd301, 79, 1); //unknown23
            }

            if (!SendData(streamStream, cmd301))
            {
                Console.Error.WriteLine("[STREAM] login failed send request");
                return false;
            }

            var resp401 = ReceiveData(streamStream, 412);

            if (resp401 == null || resp401.Length < 8)
            {
                Console.Error.WriteLine("[STREAM] login failed receive response");
                return false;
            }
            var respCmd = ReadUInt32LE(resp401, 0);
            if (respCmd != 401)
            {
                Console.Error.WriteLine($"[STREAM] login invalid response cmd: {respCmd} (expected 401)");
                return false;
            }

            int result = (int)ReadUInt32LE(resp401, 4);
            if (result == -11 || result == -12)
            {
                Console.Error.WriteLine($"[STREAM] login failed result={result}");
                return false;
            }

            LogUtils.debug($"[STREAM] login response success");
            LogUtils.debug($"[STREAM] login cmd: {respCmd}");
            if (source == SourceStream.Lan)
            {
                ushort version = ReadUInt16LE(resp401, 8);
                uint width = ReadUInt32LE(resp401, 10);
                uint height = ReadUInt32LE(resp401, 14);
                uint maxPackSize = ReadUInt32LE(resp401, 18);
                byte audioFreq = resp401[22];
                byte audioBits = resp401[23];
                byte audioChannels = resp401[24];
                communicationVersion = version;
                frameWidth = (int)width;
                frameheight = (int)height;
                LogUtils.debug($"[STREAM] login result: {result}");
                LogUtils.debug($"[STREAM] login version: {version}");
                LogUtils.debug($"[STREAM] login width: {width}");
                LogUtils.debug($"[STREAM] login height: {height}");
                LogUtils.debug($"[STREAM] login maxPackSize: {maxPackSize}");
                LogUtils.debug($"[STREAM] login audioFreq: {audioFreq}");
                LogUtils.debug($"[STREAM] login audioBits: {audioBits}");
                LogUtils.debug($"[STREAM] login audioChannels: {audioChannels}");
            }

            if (deviceVersion > 30) GenerateMediaKey(authTicket);
            Console.Error.WriteLine($"[STREAM] login OK quality={(streamQuality == 0 ? "SD" : "HD")}");
            return true;
        }

        public bool StartStream()
        {
            Console.Error.WriteLine($"[STREAM] starting stream...");
            var cmd303 = new byte[256];
            WriteUInt32LE(cmd303, 0, 303);
            WriteUInt16LE(cmd303, 4, startSubcode);
            return SendData(streamStream, cmd303);
        }

        public void ReceiveFrames(OutputMode mode, RtspServer rtsp, CancellationToken ct)
        {
            bool needDecrypt = deviceVersion > 30;

            // Per-fragment-type assembly: audio (0x15/0x16/0x18/0x19/0x1A, usually
            // single-fragment) interleaves with multi-fragment video (0x28/0x29/...).
            // A single shared assembler lets audio resets starve video forever.
            var asm = new System.Collections.Generic.Dictionary<byte, FragAssembly>();
            FragAssembly GetAsm(byte t)
            {
                if (!asm.TryGetValue(t, out var a)) { a = new FragAssembly(); asm[t] = a; }
                return a;
            }
            int nonMagicCount = 0;
            var typeCensus = new System.Collections.Generic.Dictionary<byte, int>();
            int censusTotal = 0;
            int dumpCount = 0;

            var header12 = new byte[12];
            var payloadBuf = new byte[65536];

            // stdout is used only for Video or Audio output modes
            Stream stdout = (mode == OutputMode.Video || mode == OutputMode.Audio)
                ? Console.OpenStandardOutput()
                : null;

            Console.Error.WriteLine($"[RECV] mode={mode} decrypt={needDecrypt} communicationVersion={communicationVersion}");

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (needReconnect)
                    {
                        Console.Error.WriteLine($"[STREAM] lost, reconnecting... ");
                        try { streamStream?.Close(); streamClient?.Close(); } catch { }
                        if (!StreamLogin()) break;
                        if (!StartStream()) break;
                        needReconnect = false;
                    }

                    // 12-byte fragment header
                    if (ReadExact(streamStream, header12, 0, 12) < 12) continue;

                    if (header12[0] != 0x7F)
                    {
                        if (nonMagicCount < 10)
                            Console.Error.WriteLine($"[MAGIC] {BitConverter.ToString(header12)}");
                        nonMagicCount++;
                        // resync: step one byte is handled by next 12B read window shifting?
                        // keep simple: continue (may stay misaligned; count tells us)
                        continue;
                    }

                    byte type = header12[1];
                    ushort totalFrame = ReadUInt16LE(header12, 3);
                    ushort curFrame = ReadUInt16LE(header12, 5);
                    ushort payLen = ReadUInt16LE(header12, 7);

                    if (payLen == 0 || payLen > 20000 || totalFrame == 0 || curFrame >= totalFrame)
                    {
                        Console.Error.WriteLine($"[SKIP] invalid header type=0x{type:X2} total={totalFrame} cur={curFrame} len={payLen}");
                        continue;
                    }

                    if (payloadBuf.Length < payLen) payloadBuf = new byte[payLen];
                    if (ReadExact(streamStream, payloadBuf, 0, payLen) < payLen) continue;

                    if (type == 0x5B)
                    {
                        continue;
                    }

                    var st = GetAsm(type);
                    if (curFrame == 0 || !st.Assembling || totalFrame != st.Total || curFrame != st.Next)
                    {
                        if (st.Assembling && curFrame != 0)
                        {
                            LogUtils.debug($"[FRAME] resync type=0x{type:X2} expected={st.Next} got={curFrame} total={totalFrame}");
                        }

                        st.Frags.Clear();
                        st.Total = totalFrame;
                        st.Next = 0;
                        st.Assembling = true;
                        Array.Copy(header12, st.FirstHeader, 12);
                    }

                    for (int i = 0; i < payLen; i++) st.Frags.Add(payloadBuf[i]);
                    st.Next = (ushort)(curFrame + 1);

                    if (curFrame != totalFrame - 1) continue;
                    if (st.Frags.Count < 16)
                    {
                        st.Frags.Clear();
                        st.Assembling = false;
                        continue;
                    }

                    byte[] full = st.Frags.ToArray();
                    st.Frags.Clear();
                    st.Assembling = false;
                    byte frameStartType = type;

                    censusTotal++;
                    if (!typeCensus.ContainsKey(frameStartType))
                    {
                        typeCensus[frameStartType] = 0;
                        Console.Error.WriteLine($"[NEWTYPE] first 0x{frameStartType:X2} len={full.Length} head={BitConverter.ToString(full, 0, Math.Min(16, full.Length))} frag={BitConverter.ToString(st.FirstHeader)}");
                    }
                    typeCensus[frameStartType]++;
                    if (frameStartType == 0x28)
                        Console.Error.WriteLine($"[FRAG28] total={st.Total} frag={BitConverter.ToString(st.FirstHeader)}");
                    if (censusTotal % 500 == 0)
                    {
                        var parts = new System.Collections.Generic.List<string>();
                        foreach (var kv in typeCensus) parts.Add($"0x{kv.Key:X2}:{kv.Value}");
                        Console.Error.WriteLine($"[CENSUS] total={censusTotal} " + string.Join(" ", parts));
                    }

                    if (!HandleAsMediaFrame(frameStartType, full, needDecrypt, mode, rtsp, stdout))
                    {
                        Console.Error.WriteLine($"[FRAME] unknown type=0x{frameStartType:X2} len={full.Length}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("[RECV] Operation cancelled");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RECV] {ex.Message}");
                Console.Error.WriteLine($"[RECV] {ex.StackTrace}");
            }
        }

        private bool HandleAsMediaFrame(byte rawType, byte[] full, bool needDecrypt, OutputMode mode, RtspServer rtsp, Stream stdout)
        {
            ushort outerFrameType = full.Length >= 6 ? ReadUInt16LE(full, 4) : (ushort)0;            uint outerFrameId = full.Length >= 4 ? ReadUInt32LE(full, 0) : 0;
            ushort outerFrameRate = full.Length >= 8 ? ReadUInt16LE(full, 6) : (ushort)0;
            ulong outerTimestamp = full.Length >= 16 ? ReadUInt64LE(full, 8) : 0;

            // Device telemetry (plaintext per parseFrameDataV20: 0x5B wifi and
            // 0x5C battery go straight to callbacks with no decrypt).
            if (rawType == 0x5B || rawType == 0x5C)
            {
                byte[] info = new byte[Math.Max(0, full.Length - 16)];
                Array.Copy(full, 16, info, 0, info.Length);
                string hex = BitConverter.ToString(info);
                if (rawType == 0x5B) { LastWifiHex = hex; LastWifiAt = DateTime.Now; }
                else { LastBatteryHex = hex; LastBatteryAt = DateTime.Now; }
                Console.Error.WriteLine($"[TELEMETRY] type=0x{rawType:X2} len={info.Length} data={hex}");
                return true;
            }

            if (rawType == 0x1A || rawType == 0x15 || rawType == 0x18 || rawType == 0x19)
            {
                // per parseFrameDataV20: types 0x15/0x16/0x18/0x19/0x1A are audio (frame=2)
                if (full.Length < 16) return false;

                uint frameId = ReadUInt32LE(full, 0);
                ushort frameType = ReadUInt16LE(full, 4);
                ushort frameRate = ReadUInt16LE(full, 6);
                ulong timestamp = ReadUInt64LE(full, 8);

                byte[] payload = new byte[full.Length - 16];
                Array.Copy(full, 16, payload, 0, payload.Length);

                if (needDecrypt)
                {
                    if (communicationVersion == 21)
                        DecryptMediaPre2k(payload, payload.Length, 1);
                    else
                        DecryptAudioFrame(payload, payload.Length);
                }

                var audioFrame = new FrameData
                {
                    RawType = rawType,
                    FrameId = frameId,
                    FrameType = frameType,
                    FrameRate = frameRate,
                    Timestamp = timestamp,
                    Payload = payload
                };

                if (mode == OutputMode.Audio)
                {
                    stdout.Write(payload, 0, payload.Length);
                    stdout.Flush();
                }
                else if (mode == OutputMode.Rtsp)
                {
                    rtsp?.PushAudio(audioFrame);
                }

                return true;
            }

            if (rawType == 0x16)
            {
                byte[] audioPayload = ExtractAudioPayload(full, needDecrypt);
                if (audioPayload.Length == 0) return false;

                if (mode == OutputMode.Audio)
                {
                    stdout.Write(audioPayload, 0, audioPayload.Length);
                    stdout.Flush();
                }
                else if (mode == OutputMode.Rtsp)
                {
                    rtsp?.PushAudio(new FrameData
                    {
                        RawType = rawType,
                        FrameId = 0,
                        FrameType = 0,
                        FrameRate = 0,
                        Timestamp = 0,
                        Payload = audioPayload
                    });
                }

                return true;
            }

            if (!TryExtractVideoPayload(full, needDecrypt, out var normalizedPayload))
            {
                LogUtils.debug($"[FRAME] unclassified type=0x{rawType:X2} len={full.Length} head={BitConverter.ToString(full, 0, Math.Min(16, full.Length))}");
                if (extractLogCount < 40)
                {
                    Console.Error.WriteLine($"[EXTRACT] type=0x{rawType:X2} len={full.Length} result=REJECT head={BitConverter.ToString(full, 16, Math.Min(32, full.Length - 16))}");
                    extractLogCount++;
                }
                return false;
            }

            bool isKeyFrame = IsKeyVideoFrame(rawType, outerFrameType, normalizedPayload);

            if (mode == OutputMode.Rtsp)
            {
                snapshotManager.UpdateFrame(normalizedPayload, frameWidth, frameheight, isKeyFrame, outerTimestamp);
            }

            var videoFrame = new FrameData
            {
                RawType = rawType,
                FrameId = outerFrameId,
                FrameType = outerFrameType,
                FrameRate = outerFrameRate,
                Timestamp = outerTimestamp,
                Codec = DetectVideoCodec(normalizedPayload),
                Payload = normalizedPayload
            };

            if (mode == OutputMode.Video)
            {
                stdout.Write(normalizedPayload, 0, normalizedPayload.Length);
                stdout.Flush();
            }
            else if (mode == OutputMode.Rtsp)
            {
                rtsp?.PushVideo(videoFrame);
            }

            if (extractLogCount < 40 && (rawType == 0x28 || rawType == 0x29))
            {
                Console.Error.WriteLine($"[EXTRACT] type=0x{rawType:X2} len={full.Length} result=OK norm={normalizedPayload.Length} head={BitConverter.ToString(normalizedPayload, 0, Math.Min(32, normalizedPayload.Length))}");
                extractLogCount++;
            }

            return true;
        }

        private byte[] ExtractAudioPayload(byte[] full, bool needDecrypt)
        {
            byte[] payload;

            if (full.Length > 20)
            {
                payload = new byte[full.Length - 20];
                Array.Copy(full, 20, payload, 0, payload.Length);
            }
            else
            {
                payload = (byte[])full.Clone();
            }

            if (needDecrypt && payload.Length >= 16)
            {
                if (communicationVersion == 21)
                    DecryptMediaPre2k(payload, payload.Length, 1);
                else
                    DecryptAudioFrame(payload, payload.Length);
            }

            return payload;
        }

        private bool TryExtractVideoPayload(byte[] full, bool needDecrypt, out byte[] normalizedPayload)
        {
            int bestScore = int.MinValue;
            byte[] bestPayload = null;

            foreach (var payload in EnumerateVideoCandidates(full, needDecrypt))
            {
                if (!TryNormalizeVideoPayload(payload, out var candidate))
                    continue;

                int score = ScoreVideoPayload(candidate);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPayload = candidate;
                }
            }

            if (bestPayload != null)
            {
                normalizedPayload = bestPayload;
                return true;
            }

            normalizedPayload = Array.Empty<byte>();
            return false;
        }

        private IEnumerable<byte[]> EnumerateVideoCandidates(byte[] full, bool needDecrypt)
        {
            // Mirror the app (parseFrameDataV20): payload is ALWAYS at +16
            // (outer 16B header is id/type/rate/ts, never encrypted). Guessing
            // other offsets truncates NALs (gray frames / strip-only video).
            if (needDecrypt && full.Length > 16)
            {
                byte[] decrypted = (byte[])full.Clone();
                if (communicationVersion == 21)
                    DecryptMediaPre2k(decrypted, decrypted.Length, 1);
                else
                    DecryptVideoFrameAt(decrypted, 16);
                if (decrypted.Length > 16)
                {
                    byte[] slice = new byte[decrypted.Length - 16];
                    Array.Copy(decrypted, 16, slice, 0, slice.Length);
                    yield return slice;
                }
                // fall back to raw slice (some frames/versions arrive plain)
                if (full.Length > 16)
                {
                    byte[] direct = new byte[full.Length - 16];
                    Array.Copy(full, 16, direct, 0, direct.Length);
                    yield return direct;
                }
                yield break;
            }

            foreach (int offset in new[] { 0, 4, 8, 12, 16, 20 })
            {
                if (full.Length <= offset) continue;

                byte[] direct = new byte[full.Length - offset];
                Array.Copy(full, offset, direct, 0, direct.Length);
                yield return direct;
            }
        }

        private bool TryNormalizeVideoPayload(byte[] payload, out byte[] normalizedPayload)
        {
            int start = FindVideoStartCode(payload);
            if (start < 0)
            {
                normalizedPayload = payload;
                return false;
            }

            if (payload[start] == 0 && payload[start + 1] == 0 && payload[start + 2] == 1)
            {
                normalizedPayload = new byte[payload.Length - start + 1];
                normalizedPayload[0] = 0;
                Array.Copy(payload, start, normalizedPayload, 1, payload.Length - start);
                return true;
            }

            normalizedPayload = new byte[payload.Length - start];
            Array.Copy(payload, start, normalizedPayload, 0, normalizedPayload.Length);
            return true;
        }

        private bool IsKeyVideoFrame(byte rawType, ushort frameType, byte[] payload)
        {
            if (rawType == 0x00) return true;

            int nalOffset = payload.Length >= 4 && payload[2] == 0 && payload[3] == 1 ? 4 : 3;
            if (payload.Length <= nalOffset) return false;

            byte nalHeader = payload[nalOffset];
            int h264NalType = nalHeader & 0x1F;
            if (h264NalType == 5 || h264NalType == 7 || h264NalType == 8) return true;

            int h265NalType = (nalHeader >> 1) & 0x3F;
            if (h265NalType is 19 or 20 or 32 or 33 or 34) return true;

            return frameType == 0;
        }

        private int FindVideoStartCode(byte[] payload)
        {
            int limit = Math.Min(payload.Length - 4, 1024);
            for (int i = 0; i <= limit; i++)
            {
                if (payload[i] == 0 && payload[i + 1] == 0 && payload[i + 2] == 0 && payload[i + 3] == 1)
                {
                    if (IsLikelyNalHeader(payload, i + 4)) return i;
                }

                if (payload[i] == 0 && payload[i + 1] == 0 && payload[i + 2] == 1)
                {
                    if (IsLikelyNalHeader(payload, i + 3)) return i;
                }
            }

            return -1;
        }

        private int ScoreVideoPayload(byte[] payload)
        {
            int score = 0;
            int nalCount = 0;
            int parameterSetCount = 0;
            int idrCount = 0;

            int i = 0;
            while (i < payload.Length - 4 && nalCount < 12)
            {
                int sc = FindNextStartCode(payload, i);
                if (sc < 0) break;

                int scLen = payload[sc + 2] == 1 ? 3 : 4;
                int nalStart = sc + scLen;
                if (!IsLikelyNalHeader(payload, nalStart))
                {
                    i = sc + 1;
                    score -= 20;
                    continue;
                }

                nalCount++;
                byte nalHeader = payload[nalStart];
                int h264NalType = nalHeader & 0x1F;
                int h265NalType = (nalHeader >> 1) & 0x3F;

                if (h264NalType is 7 or 8 || h265NalType is 32 or 33 or 34)
                {
                    parameterSetCount++;
                    score += 50;
                }

                if (h264NalType == 5 || h265NalType is 19 or 20)
                {
                    idrCount++;
                    score += 30;
                }

                score += 10;
                i = nalStart + 1;
            }

            if (nalCount == 0) return int.MinValue;

            score += nalCount * 5;
            score += parameterSetCount * 40;
            score += idrCount * 20;

            return score;
        }

        private int FindNextStartCode(byte[] payload, int from)
        {
            for (int i = from; i + 3 < payload.Length; i++)
            {
                if (payload[i] == 0 && payload[i + 1] == 0)
                {
                    if (payload[i + 2] == 1) return i;
                    if (payload[i + 2] == 0 && payload[i + 3] == 1) return i;
                }
            }

            return -1;
        }

        private bool IsLikelyNalHeader(byte[] payload, int index)
        {
            if (index >= payload.Length) return false;

            byte nalHeader = payload[index];
            int h264NalType = nalHeader & 0x1F;
            if (h264NalType is > 0 and < 24) return true;

            int h265NalType = (nalHeader >> 1) & 0x3F;
            return h265NalType is > 0 and < 48;
        }

        private VideoCodec DetectVideoCodec(byte[] payload)
        {
            // A single first byte is ambiguous: e.g. 0x02 is H264 partition-A
            // AND H265 TRAIL_N, 0x41 is H264 slice AND H265-VPS-shifted. Decide
            // only on exact markers: H265 VPS/SPS/PPS first bytes (0x40/42/44)
            // or IDR types (19/20) with a sane TID byte; H264 IDR/SPS/PPS
            // (5/7/8). Anything else falls back to the last confident decision
            // (streams don't switch codecs mid-flight).
            if (payload == null || payload.Length < 5) return lastVideoCodec;
            bool h265 = false, h264 = false;
            int i = 0, guard = 0;
            while (i < payload.Length - 4 && guard++ < 64)
            {
                int sc = FindNextStartCode(payload, i);
                if (sc < 0) break;
                int scLen = payload[sc + 2] == 1 ? 3 : 4;
                int ns = sc + scLen;
                if (ns < payload.Length && IsLikelyNalHeader(payload, ns))
                {
                    byte b0 = payload[ns];
                    bool tidOk = ns + 1 < payload.Length && (payload[ns + 1] & 0x07) is >= 1 and <= 6;
                    if ((b0 == 0x40 || b0 == 0x42 || b0 == 0x44
                         || (((b0 >> 1) & 0x3F) is 19 or 20)) && tidOk) h265 = true;
                    if ((b0 & 0x1F) is 5 or 7 or 8) h264 = true;
                }
                i = ns + 1;
            }
            if (h265) { lastVideoCodec = VideoCodec.H265; return VideoCodec.H265; }
            if (h264) { lastVideoCodec = VideoCodec.H264; return VideoCodec.H264; }
            return lastVideoCodec;
        }

        private void DecryptVideoFrameAt(byte[] data, int start)
        {
            // AES-ECB, 64 bytes on / 16 bytes off, phase anchored at payload
            // start (16 = just past the plaintext outer frame header).
            using var aes = Aes.Create();
            aes.Key = aesKey; aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None;
            using var dec = aes.CreateDecryptor();
            for (int offset = start; offset + 64 <= data.Length; offset += 80)
                for (int i = 0; i < 4; i++)
                    dec.TransformBlock(data, offset + i * 16, 16, data, offset + i * 16);
        }

        private void DecryptVideoFrame(byte[] data, int length)
        {
            DecryptVideoFrameAt(data, 0);
        }

        private void DecryptAudioFrame(byte[] data, int length)
        {
            int alignedSize = (length / 16) * 16;
            if (alignedSize == 0) return;

            using var aes = Aes.Create();
            aes.Key = aesKey;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            using var dec = aes.CreateDecryptor();
            for (int i = 0; i < alignedSize; i += 16)
                dec.TransformBlock(data, i, 16, data, i);
        }

        // Not tested
        private void DecryptMediaPre2k(byte[] data, int length, int mode)
        {
            int decryptLength;
            if (mode == 0 && length > 2048)
            {
                decryptLength = 2048;
            }
            else
            {
                decryptLength = (length / 16) * 16;
            }
            if (decryptLength > 0)
            {
                using var aes = Aes.Create();
                aes.Key = aesKey;
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                using var decryptor = aes.CreateDecryptor();
                decryptor.TransformBlock(data, 0, decryptLength, data, 0);
            }
        }

        void GenerateMediaKey(uint ticket)
        {
            WriteUInt32LE(aesKey, 0, ticket);
            WriteUInt64LE(aesKey, 4, 0x618123462c14795c);
            WriteUInt32LE(aesKey, 12, 0x82800df0);
            LogUtils.debug($"[KEY] {BitConverter.ToString(aesKey).Replace("-", "")}");
        }

        byte[] GeneratePassword(string pw)
        {
            byte[] sk = Encoding.ASCII.GetBytes("macrovideo+*#!^@");
            var rng = new Random();
            byte[] rk = new byte[16];
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            for (int i = 0; i < 16; i++) rk[i] = (byte)chars[rng.Next(chars.Length)];

            var pb = Encoding.ASCII.GetBytes(pw);
            var pad = new byte[48];
            Array.Copy(pb, pad, Math.Min(pb.Length, 48));

            void Enc(byte[] key)
            {
                using var a = Aes.Create();
                a.Key = key; a.Mode = CipherMode.ECB; a.Padding = PaddingMode.None;
                using var e = a.CreateEncryptor();
                for (int i = 0; i < 48; i += 16) e.TransformBlock(pad, i, 16, pad, i);
            }
            Enc(sk); Enc(rk);

            var out_ = new byte[64];
            Array.Copy(rk, 0, out_, 0, 16);
            Array.Copy(pad, 0, out_, 16, 48);
            return out_;
        }

        bool SendData(NetworkStream s, byte[] d)
        {
            try { s.Write(d, 0, d.Length); s.Flush(); return true; }
            catch (Exception ex) { Console.Error.WriteLine($"[SEND] {ex.Message}"); return false; }
        }

        byte[] ReceiveData(NetworkStream s, int max)
        {
            var buf = new byte[max]; int tot = 0;
            var deadline = DateTime.Now.AddSeconds(5);
            while (DateTime.Now < deadline)
            {
                if (s.DataAvailable || tot > 0)
                {
                    int n = s.Read(buf, tot, max - tot);
                    if (n <= 0) break;
                    tot += n;
                    if (tot >= 16) break;
                }
                else Thread.Sleep(10);
            }
            if (tot == 0) return null;
            var r = new byte[tot]; Array.Copy(buf, r, tot); return r;
        }

        int ReadExact(NetworkStream s, byte[] buf, int off, int cnt)
        {
            int tot = 0;
            var deadline = DateTime.Now.AddSeconds(3);
            while (tot < cnt)
            {
                if (DateTime.Now > deadline)
                {
                    needReconnect = true;
                    return tot;
                }
                if (!s.DataAvailable)
                {
                    Thread.Sleep(10);
                    continue;
                }
                int n = s.Read(buf, off + tot, cnt - tot);
                if (n <= 0) break;
                tot += n;
            }
            return tot;
        }

        void WriteUInt32LE(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
        void WriteUInt16LE(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        void WriteUInt64LE(byte[] b, int o, ulong v) { for (int i = 0; i < 8; i++) b[o + i] = (byte)(v >> (i * 8)); }
        uint ReadUInt32LE(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        ushort ReadUInt16LE(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
        ulong ReadUInt64LE(byte[] b, int o) { ulong v = 0; for (int i = 0; i < 8; i++) v |= ((ulong)b[o + i]) << (i * 8); return v; }

        public bool PtzRight() => SendControl(V380Commands.PTZ_RIGHT);
        public bool PtzLeft() => SendControl(V380Commands.PTZ_LEFT);
        public bool PtzUp() => SendControl(V380Commands.PTZ_UP);
        public bool PtzDown() => SendControl(V380Commands.PTZ_DOWN);
        public bool PtzStop() => SendControl(V380Commands.PTZ_STOP);
        public bool LightOn() => SendControl(V380Commands.LIGHT_ON);
        public bool LightOff() => SendControl(V380Commands.LIGHT_OFF);
        public bool LightAuto() => SendControl(V380Commands.LIGHT_AUTO);
        public bool ImageColor() => SendControl(V380Commands.IMAGE_COLOR);
        public bool ImageBW() => SendControl(V380Commands.IMAGE_BW);
        public bool ImageAuto() => SendControl(V380Commands.IMAGE_AUTO);
        public bool ImageFlip() => SendControl(V380Commands.IMAGE_FLIP);
        private bool SendControl(byte[] payload)
        {
            if (streamStream == null) return false;
            return SendData(streamStream, payload);
        }

        public string GetDeviceId()
        {
            return deviceId.ToString();
        }

        /// <summary>
        /// Full XParam struct: parsed from a GET (cmd 347) response, serialized
        /// to a SET (cmd 348) request. Layouts mirror the decompiled
        /// getDeviceParam/setDeviceParam (response and request differ by a
        /// 4-byte shift plus a modelId region present only in responses).
        /// </summary>
        public sealed class XParamState
        {
            public int Pir, LightVal, LightTime, PhotoSens, LightSens;
            public int ResetEnable, RtspEnable, SmokeSens, RtspVideoType;
            public int Osd, Indicator, Led, PtzV, PtzH, VideoStandard, PowerSource;
            public int SpeakerVol, PowerMode, TimerCount, LogUpload, Hdr;
            public byte[] PowerTimers = Array.Empty<byte>();

            public static XParamState? FromGetResponse(byte[] r)
            {
                if (r.Length < 96) return null;
                if (ReadU32(r, 0) != 447 || ReadU32(r, 4) != 1001) return null;
                var s = new XParamState
                {
                    Pir = r[9], LightVal = r[10], LightTime = r[11] | (r[12] << 8),
                    PhotoSens = r[13], LightSens = r[14],
                    ResetEnable = r[16], RtspEnable = r[17], SmokeSens = r[18],
                    RtspVideoType = r[19], Osd = r[20], Indicator = r[21], Led = r[22],
                    PtzV = r[23], PtzH = r[24], VideoStandard = r[25], PowerSource = r[26],
                    SpeakerVol = r[30],
                    TimerCount = r[74],
                    LogUpload = r[76], Hdr = r[79],
                };
                s.PowerMode = (r[72] & 31) | ((r[77] & 31) << 5) | ((r[72] & 224) << 19) | ((r[77] & 224) << 22);
                int n = Math.Min(s.TimerCount, 8);
                if (n > 0 && 75 + n * 6 <= r.Length)
                {
                    s.PowerTimers = new byte[n * 6];
                    Array.Copy(r, 75, s.PowerTimers, 0, n * 6);
                }
                return s;
            }

            public byte[] ToSetRequest(uint devId, uint ticket)
            {
                var b = new byte[256];
                WriteU32(b, 0, 348);
                WriteU32(b, 4, devId);
                b[8] = 1;
                WriteU32(b, 9, ticket);
                b[13] = (byte)Pir; b[14] = (byte)LightVal;
                b[15] = (byte)(LightTime & 255); b[16] = (byte)((LightTime >> 8) & 255);
                b[17] = (byte)PhotoSens; b[18] = (byte)LightSens;
                b[20] = (byte)ResetEnable; b[21] = (byte)RtspEnable; b[22] = (byte)SmokeSens;
                b[23] = (byte)RtspVideoType; b[24] = (byte)Osd; b[25] = (byte)Indicator;
                b[26] = (byte)Led; b[27] = (byte)PtzV; b[28] = (byte)PtzH;
                b[29] = (byte)VideoStandard; b[30] = (byte)PowerSource;
                b[34] = (byte)SpeakerVol;
                b[35] = (byte)((PowerMode & 31) | ((PowerMode & 117440512) >> 19));
                int n = PowerTimers.Length / 6;
                b[36] = (byte)n;
                if (n > 0) Array.Copy(PowerTimers, 0, b, 37, n * 6);
                int i3 = 36 + n * 6;
                b[i3 + 2] = (byte)LogUpload;
                b[i3 + 3] = (byte)(((PowerMode & 992) >> 5) | ((PowerMode & 939524096) >> 22));
                b[i3 + 5] = (byte)Hdr;
                b[i3 + 7] = 0; // detection: absent from LAN GET; app sends struct default
                return b;
            }

            static void WriteU32(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
            static uint ReadU32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }

        /// <summary>Best-effort XParam read (cmd 347) for power/video settings.</summary>
        public XParamState? GetXParam()
        {
            try
            {
                using var c = new TcpClient();
                var r = c.BeginConnect(ip, port, null, null);
                if (!r.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(4))) return null;
                c.EndConnect(r);
                using var s = c.GetStream();
                var req = new byte[256];
                WriteUInt32LE(req, 0, 347);
                WriteUInt32LE(req, 4, deviceId);
                req[8] = 1;
                WriteUInt32LE(req, 9, authTicket);
                s.Write(req, 0, req.Length);
                s.Flush();
                var resp = new byte[256];
                int tot = 0;
                var dl = DateTime.Now.AddSeconds(5);
                while (tot < 256 && DateTime.Now < dl)
                {
                    if (!s.DataAvailable) { Thread.Sleep(20); continue; }
                    int n = s.Read(resp, tot, 256 - tot);
                    if (n <= 0) break;
                    tot += n;
                }
                if (tot < 96) return null;
                return XParamState.FromGetResponse(resp);
            }
            catch (Exception ex) { LogUtils.debug($"[XPARAM] {ex.Message}"); return null; }
        }

        public void FetchDeviceParams()
        {
            var st = GetXParam();
            if (st == null) return;
            XParamRtspEnable = st.RtspEnable;
            XParamVideoType = st.RtspVideoType;
            VideoStandard = st.VideoStandard;
            PowerSource = st.PowerSource;
            SpeakerVol = st.SpeakerVol;
            Console.Error.WriteLine($"[XPARAM] rtsp={XParamRtspEnable} vtype={XParamVideoType} vstd={VideoStandard} power={PowerSource} spk={SpeakerVol} pir={st.Pir} lamp={st.LightSens} ptzv={st.PtzV} ptzh={st.PtzH}");
        }

        /// <summary>
        /// Batch-1 settings writer. Read-modify-write of the full XParam
        /// struct; only the requested keys change. Supported keys:
        /// speaker_vol 0..100, pir/low|medium|high, lamplight/low|medium|high,
        /// ptz_h 1..9, ptz_v 1..9. Returns (ok, applied|error).
        /// </summary>
        public (bool ok, string info) ApplySettings(System.Collections.Generic.Dictionary<string, string> kv)
        {
            try
            {
                var st = GetXParam();
                if (st == null) return (false, "get-failed");
                var applied = new System.Collections.Generic.List<string>();
                foreach (var (k, v) in kv)
                {
                    switch (k.ToLowerInvariant())
                    {
                        case "speaker_vol":
                            if (int.TryParse(v, out int sp) && sp >= 0 && sp <= 100) { st.SpeakerVol = sp; applied.Add($"speaker_vol={sp}"); }
                            else return (false, "speaker_vol range 0..100");
                            break;
                        case "pir":
                            if (TryLevel(v, out int pir)) { st.Pir = pir; applied.Add($"pir={v}({pir})"); }
                            else return (false, "pir low|medium|high");
                            break;
                        case "lamplight":
                            if (TryLevel(v, out int lamp)) { st.LightSens = lamp; applied.Add($"lamplight={v}({lamp})"); }
                            else return (false, "lamplight low|medium|high");
                            break;
                        case "ptz_h":
                            if (int.TryParse(v, out int ph) && ph >= 1 && ph <= 9) { st.PtzH = ph; applied.Add($"ptz_h={ph}"); }
                            else return (false, "ptz_h range 1..9");
                            break;
                        case "ptz_v":
                            if (int.TryParse(v, out int pv) && pv >= 1 && pv <= 9) { st.PtzV = pv; applied.Add($"ptz_v={pv}"); }
                            else return (false, "ptz_v range 1..9");
                            break;
                        default: return (false, $"unknown key {k}");
                    }
                }
                using var c = new TcpClient();
                var r = c.BeginConnect(ip, port, null, null);
                if (!r.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5))) return (false, "connect");
                c.EndConnect(r);
                using var s = c.GetStream();
                var req = st.ToSetRequest(deviceId, authTicket);
                s.Write(req, 0, req.Length);
                s.Flush();
                var resp = new byte[32];
                int tot = 0;
                var dl = DateTime.Now.AddSeconds(8);
                while (tot < 32 && DateTime.Now < dl)
                {
                    if (!s.DataAvailable) { Thread.Sleep(50); continue; }
                    int n = s.Read(resp, tot, 32 - tot);
                    if (n <= 0) break;
                    tot += n;
                }
                if (tot < 12) return (false, "short-read");
                uint cmd = ReadUInt32LE(resp, 0), res = ReadUInt32LE(resp, 4);
                Console.Error.WriteLine($"[CONFIG] set cmd={cmd} res={res} {string.Join(",", applied)}");
                if (cmd != 448 || res != 1001) return (false, $"cmd={cmd} res={res}");
                FetchDeviceParams();
                return (true, string.Join(",", applied));
            }
            catch (Exception ex) { LogUtils.debug($"[CONFIG] {ex.Message}"); return (false, "exception"); }
        }

        static bool TryLevel(string v, out int n)
        {
            n = v.ToLowerInvariant() switch { "low" => 1, "medium" => 2, "high" => 3, _ => -1 };
            return n > 0;
        }

        /// <summary>
        /// One-key alarm arm/disarm (cmd 144,mirrors setOneKeyAlarmSetting:
        /// [72]=1000 arm / 1001 disarm). Passwords ≤28 chars go plaintext
        /// (changeDevPwd passthrough). Returns (ok, app-layer code).
        /// </summary>
        public (bool ok, int code) SetAlarm(bool arm)
        {
            try
            {
                using var c = new TcpClient();
                var r = c.BeginConnect(ip, port, null, null);
                if (!r.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5))) return (false, -1);
                c.EndConnect(r);
                using var s = c.GetStream();
                var req = new byte[412];
                WriteUInt32LE(req, 0, 144);
                var ub = Encoding.ASCII.GetBytes(username ?? "");
                Array.Copy(ub, 0, req, 4, Math.Min(ub.Length, 32));
                var pb = Encoding.ASCII.GetBytes(password ?? "");
                Array.Copy(pb, 0, req, 36, Math.Min(pb.Length, 32));
                WriteUInt32LE(req, 68, deviceId);
                WriteUInt32LE(req, 72, arm ? 1000u : 1001u);
                s.Write(req, 0, req.Length);
                s.Flush();
                var resp = new byte[412];
                int tot = 0;
                var dl = DateTime.Now.AddSeconds(8);
                while (tot < 32 && DateTime.Now < dl)
                {
                    if (!s.DataAvailable) { Thread.Sleep(50); continue; }
                    int n = s.Read(resp, tot, 412 - tot);
                    if (n <= 0) break;
                    tot += n;
                }
                if (tot < 12) return (false, -2);
                uint cmd = ReadUInt32LE(resp, 0);
                uint res = ReadUInt32LE(resp, 4);
                if (cmd != 244) return (false, (int)cmd);
                if (res != 1001) return (false, (int)ReadUInt32LE(resp, 8));
                Console.Error.WriteLine($"[ALARM] set {(arm ? "ON" : "OFF")} ok");
                return (true, 256);
            }
            catch (Exception ex) { LogUtils.debug($"[ALARM] {ex.Message}"); return (false, -3); }
        }

        public string GetDeviceVersion()
        {
            return deviceVersion.ToString();
        }

        public void Dispose()
        {
            authStream?.Close(); authClient?.Close();
            streamStream?.Close(); streamClient?.Close();
            snapshotManager?.Dispose();
        }
    }
}
