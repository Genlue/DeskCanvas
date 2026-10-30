#pragma warning disable CA1416

namespace AudioVisualizer.Services;

using System;
using System.Runtime.InteropServices;
using System.Threading;
using AudioVisualizer.Models;

/// <summary>
/// WASAPI 底层系统/麦克风音频采集服务单例，支持零依赖实时频谱计算、耳机热插拔无感切换与全格式 PCM 适配
/// </summary>
public sealed class AudioCaptureService : IDisposable
{
    private static readonly Lazy<AudioCaptureService> LazyInstance = new(() => new AudioCaptureService());
    public static AudioCaptureService Instance => LazyInstance.Value;

    private readonly object lockObj = new();
    private Thread? workerThread;
    private CancellationTokenSource? cts;
    private AudioSourceType currentSource = AudioSourceType.SystemAudio;
    private int subscriberCount;

    // 环形 PCM 缓冲区
    private readonly float[] sampleBuffer = new float[FftCalculator.FftSize * 2];
    private int writeIndex;

    // 当前计算出的归一化频谱频段 (最多支持 128 个频段)
    private readonly float[] rawBands = new float[128];
    private readonly float[] smoothedBands = new float[128];
    private long lastAudioPacketTime;

    // 模拟发生器时钟
    private double simTime;

    private AudioCaptureService()
    {
    }

    /// <summary>
    /// 增加订阅者引用计数，并在需要时启动采集线程
    /// </summary>
    public void Retain(AudioSourceType source)
    {
        lock (lockObj)
        {
            subscriberCount++;
            if (currentSource != source || workerThread == null)
            {
                currentSource = source;
                RestartCaptureWorker();
            }
        }
    }

    /// <summary>
    /// 释放订阅者引用，没有活动订阅者时停止后台线程节省资源
    /// </summary>
    public void Release()
    {
        lock (lockObj)
        {
            subscriberCount = Math.Max(0, subscriberCount - 1);
            if (subscriberCount == 0)
            {
                StopCaptureWorker();
            }
        }
    }

    /// <summary>
    /// 动态切换音频识别源 (电脑内音频 vs 录制/麦克风)
    /// </summary>
    public void SwitchSource(AudioSourceType newSource)
    {
        lock (lockObj)
        {
            if (currentSource == newSource && workerThread != null)
                return;

            currentSource = newSource;
            if (subscriberCount > 0)
            {
                RestartCaptureWorker();
            }
        }
    }

    /// <summary>
    /// 获取最新的平滑频段能量
    /// </summary>
    public void ReadSpectrum(Span<float> destination, double sensitivity, double smoothing, bool forceSimulate = false)
    {
        int count = destination.Length;
        if (count == 0) return;

        lock (lockObj)
        {
            if (forceSimulate)
            {
                // 仅在明确强制模拟（如画板预览或单元测试）时生成模拟律动波形
                GenerateSimulatedSpectrum(destination, count, sensitivity);
                return;
            }

            long now = Environment.TickCount64;
            bool isSilent = (lastAudioPacketTime == 0 || now - lastAudioPacketTime > 600);

            if (isSilent)
            {
                // 系统静音状态：快速衰减归零，绝不跃动
                for (int i = 0; i < count; i++)
                {
                    smoothedBands[i] *= 0.75f;
                    if (smoothedBands[i] < 0.0005f)
                    {
                        smoothedBands[i] = 0f;
                    }
                    destination[i] = smoothedBands[i];
                }
                return;
            }

            // 对原始频段应用平滑与灵敏度
            float factor = (float)Math.Clamp(smoothing, 0.2, 0.98);
            float gain = (float)Math.Clamp(sensitivity, 0.2, 5.0);

            for (int i = 0; i < count; i++)
            {
                int srcIdx = (int)((double)i / count * 64);
                float target = rawBands[srcIdx] * gain;

                // 噪声门限：低于门限直接归零，消除声卡底噪和微弱抖动
                if (target < 0.0025f)
                {
                    target = 0f;
                }

                if (target > smoothedBands[i])
                {
                    // 上升快速响应
                    smoothedBands[i] = smoothedBands[i] * 0.25f + target * 0.75f;
                }
                else
                {
                    // 下降丝滑衰减
                    smoothedBands[i] = smoothedBands[i] * factor + target * (1.0f - factor);
                }

                if (smoothedBands[i] < 0.001f)
                {
                    smoothedBands[i] = 0f;
                }

                destination[i] = Math.Clamp(smoothedBands[i], 0f, 1.5f);
            }
        }
    }

    private void GenerateSimulatedSpectrum(Span<float> destination, int count, double sensitivity)
    {
        simTime += 0.055;
        float gain = (float)sensitivity;

        // 模拟鼓点与贝斯主节拍
        float kick = MathF.Pow(MathF.Max(0f, MathF.Sin((float)simTime * 2.5f)), 4.0f);

        for (int i = 0; i < count; i++)
        {
            float normIdx = (float)i / count;
            // 低频受鼓点驱动显著
            float bass = (1.0f - normIdx) * kick * 0.85f;
            // 中高频波浪起伏
            float wave = 0.28f * (MathF.Sin((float)simTime * 3f + i * 0.45f) * 0.5f + 0.5f);
            float shimmer = 0.15f * (MathF.Cos((float)simTime * 5f - i * 0.8f) * 0.5f + 0.5f);

            float total = (bass + wave + shimmer) * gain;
            destination[i] = Math.Clamp(total, 0.04f, 1.3f);
        }
    }

    private void RestartCaptureWorker()
    {
        StopCaptureWorker();
        ResetAudioBuffers();

        cts = new CancellationTokenSource();
        var token = cts.Token;
        var source = currentSource;

        workerThread = new Thread(() => RunCaptureLoop(source, token))
        {
            IsBackground = true,
            Name = "AudioVisualizerWasapiWorker",
            Priority = ThreadPriority.AboveNormal
        };
        workerThread.Start();
    }

    private void StopCaptureWorker()
    {
        if (cts != null)
        {
            cts.Cancel();
            cts.Dispose();
            cts = null;
        }

        if (workerThread != null)
        {
            if (!workerThread.Join(500))
            {
                // 超时强制放弃
            }
            workerThread = null;
        }

        ResetAudioBuffers();
    }

    private void ResetAudioBuffers()
    {
        lock (lockObj)
        {
            Array.Clear(sampleBuffer);
            Array.Clear(rawBands);
            Array.Clear(smoothedBands);
            writeIndex = 0;
            lastAudioPacketTime = 0;
        }
    }

    private void RunCaptureLoop(AudioSourceType source, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                RunSingleWasapiSession(source, token);
            }
            catch (Exception)
            {
                // 异常保护，重试连接
            }

            if (!token.IsCancellationRequested)
            {
                // 等待 300ms 后重新检测设备连接
                token.WaitHandle.WaitOne(300);
            }
        }
    }

    private void RunSingleWasapiSession(AudioSourceType source, CancellationToken token)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        IntPtr pFormat = IntPtr.Zero;
        string? activeDeviceId = null;

        try
        {
            var enumType = Type.GetTypeFromCLSID(WasapiGuids.CLSID_MMDeviceEnumerator);
            if (enumType == null) return;
            enumerator = (IMMDeviceEnumerator?)Activator.CreateInstance(enumType);
            if (enumerator == null) return;

            EDataFlow flow = source == AudioSourceType.SystemAudio ? EDataFlow.eRender : EDataFlow.eCapture;

            // 获取默认端点：优先 eMultimedia，其次回退 eConsole
            int hr = enumerator.GetDefaultAudioEndpoint(flow, ERole.eMultimedia, out device);
            if (hr != 0 || device == null)
            {
                hr = enumerator.GetDefaultAudioEndpoint(flow, ERole.eConsole, out device);
                if (hr != 0 || device == null) return;
            }

            // 记录当前活动设备的 ID，用于耳机插拔热切换检测
            if (device.GetId(out string devId) == 0)
            {
                activeDeviceId = devId;
            }

            var iidAudioClient = WasapiGuids.IID_IAudioClient;
            hr = device.Activate(ref iidAudioClient, WasapiGuids.CLSCTX_ALL, IntPtr.Zero, out object? acObj);
            if (hr != 0 || acObj is not IAudioClient client) return;
            audioClient = client;

            hr = audioClient.GetMixFormat(out pFormat);
            if (hr != 0 || pFormat == IntPtr.Zero) return;

            var waveFormat = Marshal.PtrToStructure<WAVEFORMATEX>(pFormat);
            bool isFloat = false;

            if (waveFormat.wFormatTag == 3) // WAVE_FORMAT_IEEE_FLOAT
            {
                isFloat = true;
            }
            else if (waveFormat.wFormatTag == 0xFFFE && waveFormat.cbSize >= 22) // WAVE_FORMAT_EXTENSIBLE
            {
                var ext = Marshal.PtrToStructure<WAVEFORMATEXTENSIBLE>(pFormat);
                if (ext.SubFormat == WasapiGuids.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)
                {
                    isFloat = true;
                }
            }

            int channels = Math.Max(1, (int)waveFormat.nChannels);
            int bitsPerSample = waveFormat.wBitsPerSample;

            uint streamFlags = source == AudioSourceType.SystemAudio
                ? (uint)AUDCLNT_STREAMFLAGS.AUDCLNT_STREAMFLAGS_LOOPBACK
                : (uint)AUDCLNT_STREAMFLAGS.None;

            // 初始化 100ms 环形缓冲区
            long bufferDurationHns = 1000000; // 100ms
            var emptyGuid = Guid.Empty;
            hr = audioClient.Initialize(0, streamFlags, bufferDurationHns, 0, pFormat, ref emptyGuid);
            if (hr != 0) return;

            var iidCaptureClient = WasapiGuids.IID_IAudioCaptureClient;
            hr = audioClient.GetService(ref iidCaptureClient, out object? ccObj);
            if (hr != 0 || ccObj is not IAudioCaptureClient capture) return;
            captureClient = capture;

            hr = audioClient.Start();
            if (hr != 0) return;

            float[] tempPcmWindow = new float[FftCalculator.FftSize];
            float[] tempSpectrum = new float[64];
            int checkDeviceCounter = 0;

            // 自适应噪声底（会话局部状态：换设备即重新学习，不把上一副耳机的门限带过来）。
            // 蓝牙耳机（A2DP）连通后即使不播放任何声音，回路采集也会持续送出恒定的低电平
            // 底噪——固定门限挡不住，表现为"没有声音也一直有均匀的波"。
            float levelEnvelope = 0f; // 能量包络（约 0.3~0.5s 跟随），用来判断信号"时间上是否平坦"
            float noiseLevel = 0f;    // 噪声底估计值，只有明显高于它的帧才算真正的声音

            while (!token.IsCancellationRequested)
            {
                // 1. 定期检查系统默认音频输出是否发生变化（如插拔耳机、切换音响）
                if (++checkDeviceCounter % 25 == 0 && enumerator != null && activeDeviceId != null)
                {
                    if (enumerator.GetDefaultAudioEndpoint(flow, ERole.eMultimedia, out var latestDev) == 0 && latestDev != null)
                    {
                        try
                        {
                            if (latestDev.GetId(out string latestId) == 0 &&
                                !string.Equals(activeDeviceId, latestId, StringComparison.OrdinalIgnoreCase))
                            {
                                // 检测到耳机接入或设备切换，立即退出当前会话无缝重连新设备！
                                return;
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(latestDev);
                        }
                    }
                }

                // 2. 读取音频数据包
                bool hasPackets = false;

                while (true)
                {
                    int packetHr = captureClient.GetNextPacketSize(out uint packetSize);
                    if (packetHr != 0 || packetSize == 0)
                    {
                        if (packetHr < 0)
                        {
                            // 设备已断开或无效（AUDCLNT_E_DEVICE_INVALIDATED），退出以重新获取设备
                            return;
                        }
                        break;
                    }

                    hasPackets = true;
                    hr = captureClient.GetBuffer(out IntPtr pData, out uint numFramesRead, out AUDCLNT_BUFFERFLAGS flags, out _, out _);
                    if (hr != 0)
                    {
                        if (hr < 0) return;
                        break;
                    }

                    if ((flags & AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT) != 0)
                    {
                        // WASAPI 明确标记静音包：将静音帧（全0）写入 sampleBuffer
                        WriteSilenceFrames(numFramesRead);
                    }
                    else if (numFramesRead > 0 && pData != IntPtr.Zero)
                    {
                        ProcessRawAudioFrames(pData, numFramesRead, channels, bitsPerSample, isFloat);
                    }

                    captureClient.ReleaseBuffer(numFramesRead);
                }

                // 3. 执行 FFT 频谱计算或无包快速衰减
                if (hasPackets)
                {
                    ExtractRecentPcmSamples(tempPcmWindow);
                    FftCalculator.ComputeSpectrum(tempPcmWindow, tempSpectrum);

                    // 检查是否有实际声音能量，底噪以下不算有声音包
                    float maxEnergy = 0f;
                    for (int i = 0; i < tempSpectrum.Length; i++)
                    {
                        if (tempSpectrum[i] > maxEnergy) maxEnergy = tempSpectrum[i];
                    }

                    // 噪声底学习：安静帧，或"低电平且时间上平坦"的帧（蓝牙静默底噪的签名是
                    // 恒定、无动态）才参与估计；持续的中低电平真实音乐（有起伏或电平较高）
                    // 不会被误当成噪声。0.05 上限保护响度战争型压缩音乐。
                    levelEnvelope += (maxEnergy - levelEnvelope)
                        * (maxEnergy > levelEnvelope ? 0.02f : 0.03f);
                    bool temporallyFlat = MathF.Abs(maxEnergy - levelEnvelope) < levelEnvelope * 0.25f;
                    if (maxEnergy < 0.002f || (temporallyFlat && maxEnergy < 0.05f))
                    {
                        noiseLevel += (maxEnergy - noiseLevel) * 0.04f;
                        if (noiseLevel < 0f) noiseLevel = 0f;
                    }

                    lock (lockObj)
                    {
                        Array.Copy(tempSpectrum, rawBands, Math.Min(tempSpectrum.Length, rawBands.Length));

                        // 活跃判定：高于绝对门限，且明显高于自适应噪声底（约 7dB）。
                        if (maxEnergy > MathF.Max(0.001f, noiseLevel * 2.2f))
                        {
                            lastAudioPacketTime = Environment.TickCount64;
                        }
                    }
                }
                else
                {
                    lock (lockObj)
                    {
                        // 无新音频包输入时（系统没有音频输出），快速衰减 rawBands 并将采样缓冲写 0
                        bool hasNonZero = false;
                        for (int i = 0; i < rawBands.Length; i++)
                        {
                            if (rawBands[i] > 0f)
                            {
                                rawBands[i] *= 0.75f;
                                if (rawBands[i] < 0.0005f)
                                {
                                    rawBands[i] = 0f;
                                }
                                else
                                {
                                    hasNonZero = true;
                                }
                            }
                        }

                        if (hasNonZero)
                        {
                            int clearCount = Math.Min(64, sampleBuffer.Length);
                            for (int i = 0; i < clearCount; i++)
                            {
                                sampleBuffer[writeIndex] = 0f;
                                writeIndex = (writeIndex + 1) % sampleBuffer.Length;
                            }
                        }
                    }
                }

                Thread.Sleep(10);
            }

            audioClient.Stop();
        }
        finally
        {
            if (pFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(pFormat);
            if (captureClient != null) Marshal.ReleaseComObject(captureClient);
            if (audioClient != null) Marshal.ReleaseComObject(audioClient);
            if (device != null) Marshal.ReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }

    private unsafe void ProcessRawAudioFrames(IntPtr pData, uint frameCount, int channels, int bitsPerSample, bool isFloat)
    {
        lock (lockObj)
        {
            int bufLen = sampleBuffer.Length;
            byte* pBytes = (byte*)pData;

            if (isFloat && bitsPerSample == 32)
            {
                float* ptr = (float*)pData;
                for (uint i = 0; i < frameCount; i++)
                {
                    float sum = 0f;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        sum += ptr[i * channels + ch];
                    }
                    sampleBuffer[writeIndex] = sum / channels;
                    writeIndex = (writeIndex + 1) % bufLen;
                }
            }
            else if (bitsPerSample == 16)
            {
                short* ptr = (short*)pData;
                for (uint i = 0; i < frameCount; i++)
                {
                    float sum = 0f;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        sum += ptr[i * channels + ch] / 32768.0f;
                    }
                    sampleBuffer[writeIndex] = sum / channels;
                    writeIndex = (writeIndex + 1) % bufLen;
                }
            }
            else if (bitsPerSample == 24)
            {
                // 24-bit PCM (常见于高级耳机、外接 DAC 与 Realtek 高清音频)
                int bytesPerFrame = 3 * channels;
                for (uint i = 0; i < frameCount; i++)
                {
                    float sum = 0f;
                    int frameOffset = (int)(i * bytesPerFrame);
                    for (int ch = 0; ch < channels; ch++)
                    {
                        int sampleOffset = frameOffset + ch * 3;
                        int s24 = pBytes[sampleOffset] |
                                 (pBytes[sampleOffset + 1] << 8) |
                                 ((sbyte)pBytes[sampleOffset + 2] << 16);
                        sum += s24 / 8388608.0f;
                    }
                    sampleBuffer[writeIndex] = sum / channels;
                    writeIndex = (writeIndex + 1) % bufLen;
                }
            }
            else if (bitsPerSample == 32 && !isFloat)
            {
                // 32-bit 整型 PCM
                int* ptr = (int*)pData;
                for (uint i = 0; i < frameCount; i++)
                {
                    float sum = 0f;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        sum += ptr[i * channels + ch] / 2147483648.0f;
                    }
                    sampleBuffer[writeIndex] = sum / channels;
                    writeIndex = (writeIndex + 1) % bufLen;
                }
            }
        }
    }

    private void WriteSilenceFrames(uint frameCount)
    {
        lock (lockObj)
        {
            int bufLen = sampleBuffer.Length;
            for (uint i = 0; i < frameCount; i++)
            {
                sampleBuffer[writeIndex] = 0f;
                writeIndex = (writeIndex + 1) % bufLen;
            }
        }
    }

    private void ExtractRecentPcmSamples(float[] destination)
    {
        lock (lockObj)
        {
            int bufLen = sampleBuffer.Length;
            int n = destination.Length;
            int start = (writeIndex - n + bufLen) % bufLen;

            for (int i = 0; i < n; i++)
            {
                destination[i] = sampleBuffer[(start + i) % bufLen];
            }
        }
    }

    public void Dispose()
    {
        StopCaptureWorker();
    }
}
