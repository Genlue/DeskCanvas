using System;

namespace AudioVisualizer.Services;

/// <summary>
/// 高性能基-2 Cooley-Tukey 快速傅里叶变换 (FFT) 与对数频段映射器
/// </summary>
public static class FftCalculator
{
    public const int FftSize = 1024;
    private static readonly float[] Window;
    private static readonly int[] BitReversedIndices;

    static FftCalculator()
    {
        Window = new float[FftSize];
        for (int i = 0; i < FftSize; i++)
        {
            // 汉宁窗 (Hann Window)，有效消除频域频谱泄露
            Window[i] = 0.5f * (1.0f - MathF.Cos(2.0f * MathF.PI * i / (FftSize - 1)));
        }

        BitReversedIndices = new int[FftSize];
        int bits = (int)Math.Log2(FftSize);
        for (int i = 0; i < FftSize; i++)
        {
            int rev = 0;
            for (int b = 0; b < bits; b++)
            {
                if ((i & (1 << b)) != 0)
                {
                    rev |= 1 << (bits - 1 - b);
                }
            }
            BitReversedIndices[i] = rev;
        }
    }

    /// <summary>
    /// 对输入时域采样计算对数频谱频段
    /// </summary>
    /// <param name="inputSamples">时域 PCM 归一化采样 [-1.0f, 1.0f]</param>
    /// <param name="outputMagnitudes">输出对数频段能量大小 [0.0f, 1.0f+]</param>
    public static void ComputeSpectrum(ReadOnlySpan<float> inputSamples, Span<float> outputMagnitudes)
    {
        int n = FftSize;
        Span<float> real = stackalloc float[n];
        Span<float> imag = stackalloc float[n];

        int inLen = Math.Min(inputSamples.Length, n);
        for (int i = 0; i < inLen; i++)
        {
            int rev = BitReversedIndices[i];
            real[rev] = inputSamples[i] * Window[i];
            imag[rev] = 0f;
        }

        for (int i = inLen; i < n; i++)
        {
            int rev = BitReversedIndices[i];
            real[rev] = 0f;
            imag[rev] = 0f;
        }

        // 迭代快速傅里叶变换蝴蝶运算
        for (int len = 2; len <= n; len <<= 1)
        {
            float angle = -2.0f * MathF.PI / len;
            float wlenReal = MathF.Cos(angle);
            float wlenImag = MathF.Sin(angle);

            for (int i = 0; i < n; i += len)
            {
                float wReal = 1.0f;
                float wImag = 0.0f;

                int halfLen = len >> 1;
                for (int j = 0; j < halfLen; j++)
                {
                    int uIdx = i + j;
                    int vIdx = i + j + halfLen;

                    float uReal = real[uIdx];
                    float uImag = imag[uIdx];

                    float vReal = real[vIdx] * wReal - imag[vIdx] * wImag;
                    float vImag = real[vIdx] * wImag + imag[vIdx] * wReal;

                    real[uIdx] = uReal + vReal;
                    imag[uIdx] = uImag + vImag;
                    real[vIdx] = uReal - vReal;
                    imag[vIdx] = uImag - vImag;

                    float nextWReal = wReal * wlenReal - wImag * wlenImag;
                    float nextWImag = wReal * wlenImag + wImag * wlenReal;
                    wReal = nextWReal;
                    wImag = nextWImag;
                }
            }
        }

        // 将前半频谱映射到指定数量的对数频段 (人耳对声音的感知呈对数分布)
        int half = n / 2;
        int outCount = outputMagnitudes.Length;

        // 采样率假定 48000Hz, Nyquist 频率 24000Hz, 每个 bin 大小约 46.875Hz
        // 关注范围 20Hz ~ 16000Hz (对应 bin 1 ~ 340)
        int maxUsefulBin = Math.Min(340, half - 1);

        for (int b = 0; b < outCount; b++)
        {
            double f0 = (double)b / outCount;
            double f1 = (double)(b + 1) / outCount;

            // 对数分布曲线
            double p0 = Math.Pow(f0, 2.2);
            double p1 = Math.Pow(f1, 2.2);

            int startBin = Math.Clamp((int)(p0 * maxUsefulBin) + 1, 1, maxUsefulBin);
            int endBin = Math.Clamp((int)(p1 * maxUsefulBin) + 1, startBin, maxUsefulBin);

            float sum = 0f;
            int count = 0;
            for (int k = startBin; k <= endBin; k++)
            {
                float mag = MathF.Sqrt(real[k] * real[k] + imag[k] * imag[k]) / (n / 2.0f);
                sum += mag;
                count++;
            }

            float avg = count > 0 ? (sum / count) : 0f;

            // 抑制声卡与硬件背景微弱底噪，静音时不发生微颤
            if (avg < 0.0008f)
            {
                avg = 0f;
            }

            // 高频自然能量衰减补偿，让整体频段视觉更饱满均衡
            float trebleCompensation = 1.0f + ((float)b / outCount) * 1.8f;
            outputMagnitudes[b] = avg * trebleCompensation;
        }
    }
}
