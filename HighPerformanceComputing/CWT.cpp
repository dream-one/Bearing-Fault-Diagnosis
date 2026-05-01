#include <vector>
#include <cmath>
#include <algorithm>
#include <fftw3.h>
#include <mutex>

#define EXPORT_API extern "C" __declspec(dllexport)

static constexpr double M_PI = 3.14159265358979323846;
static constexpr double MORLET_W0 = 6.0;       // Morlet wavelet center frequency

// CWT 独立 FFTW 资源（与 OrderTracking.cpp 全局变量隔离）
static fftw_complex* cwt_fft_in = nullptr;
static fftw_complex* cwt_fft_out = nullptr;
static fftw_plan cwt_plan_fwd = nullptr;
static fftw_plan cwt_plan_inv = nullptr;
static int cwt_signal_len = 0;
static std::mutex cwt_mutex;

// 初始化 CWT 专用 FFTW Plan（只需调用一次，后续 ComputeCWT 复用）
EXPORT_API void InitCWT(int signal_len, int num_scales, int num_time_bins) {
    std::lock_guard<std::mutex> lock(cwt_mutex);
    if (cwt_plan_fwd && cwt_signal_len == signal_len) return;

    if (cwt_plan_fwd) fftw_destroy_plan(cwt_plan_fwd);
    if (cwt_plan_inv) fftw_destroy_plan(cwt_plan_inv);
    if (cwt_fft_in) fftw_free(cwt_fft_in);
    if (cwt_fft_out) fftw_free(cwt_fft_out);

    cwt_signal_len = signal_len;
    cwt_fft_in = (fftw_complex*)fftw_malloc(sizeof(fftw_complex) * signal_len);
    cwt_fft_out = (fftw_complex*)fftw_malloc(sizeof(fftw_complex) * signal_len);
    cwt_plan_fwd = fftw_plan_dft_1d(signal_len, cwt_fft_in, cwt_fft_out, FFTW_FORWARD, FFTW_MEASURE);
    cwt_plan_inv = fftw_plan_dft_1d(signal_len, cwt_fft_out, cwt_fft_in, FFTW_BACKWARD, FFTW_MEASURE);
}

// 核心 CWT 计算：频域 Morlet 小波卷积 + 对数频率尺度 + 时间轴最大值池化
EXPORT_API void ComputeCWT(
    const double* signal,
    int signal_len,
    double fs,
    double freq_low,
    double freq_high,
    double* out_matrix,
    int num_scales,
    int num_time_bins
) {
    std::lock_guard<std::mutex> lock(cwt_mutex);
    if (!cwt_plan_fwd || cwt_signal_len != signal_len) return;

    // 1. 对数频率尺度 -> 计算小波尺度
    //    scale = w0 * fs / (2*pi * freq)
    std::vector<double> scales(num_scales);
    double log_low = std::log(freq_low);
    double log_high = std::log(freq_high);
    double log_range = log_high - log_low;
    double denom = 2.0 * M_PI;
    for (int i = 0; i < num_scales; ++i) {
        double freq = freq_low * std::exp(log_range * i / (num_scales - 1));
        scales[i] = MORLET_W0 * fs / (denom * freq);
    }

    // 2. 信号 FFT
    for (int i = 0; i < signal_len; ++i) {
        cwt_fft_in[i][0] = signal[i];
        cwt_fft_in[i][1] = 0.0;
    }
    fftw_execute(cwt_plan_fwd);

    // 保存信号 FFT 结果（各尺度复用）
    std::vector<double> sig_fft_re(signal_len);
    std::vector<double> sig_fft_im(signal_len);
    for (int i = 0; i < signal_len; ++i) {
        sig_fft_re[i] = cwt_fft_out[i][0];
        sig_fft_im[i] = cwt_fft_out[i][1];
    }

    // 3. 对每个尺度计算 CWT 系数
    double sqrt_pi_inv = std::pow(M_PI, -0.25);
    int half = signal_len / 2;
    std::vector<double> temp_coeffs(num_scales * signal_len);

    for (int s = 0; s < num_scales; ++s) {
        double a = scales[s];
        double sqrt_a = std::sqrt(a);

        // 频域构造 Morlet 小波并相乘
        // 正频率: omega = 2*pi*j/N, j in [0, N/2]
        // 负频率: omega = 2*pi*(j-N)/N, j in [N/2+1, N-1]
        for (int j = 0; j < signal_len; ++j) {
            double omega = (j <= half)
                ? 2.0 * M_PI * j / signal_len
                : 2.0 * M_PI * (j - signal_len) / signal_len;

            double arg = a * omega - MORLET_W0;
            double psi = sqrt_a * sqrt_pi_inv * std::exp(-0.5 * arg * arg);

            cwt_fft_out[j][0] = sig_fft_re[j] * psi;
            cwt_fft_out[j][1] = sig_fft_im[j] * psi;
        }

        // IFFT
        fftw_execute(cwt_plan_inv);

        // 取模
        for (int j = 0; j < signal_len; ++j) {
            double re = cwt_fft_in[j][0] / signal_len;
            double im = cwt_fft_in[j][1] / signal_len;
            temp_coeffs[s * signal_len + j] = std::sqrt(re * re + im * im);
        }
    }

    // 4. 时间轴最大值池化降采样
    int pool_size = signal_len / num_time_bins;
    if (pool_size < 1) pool_size = 1;
    int effective_bins = signal_len / pool_size;

    for (int s = 0; s < num_scales; ++s) {
        const double* row_in = &temp_coeffs[s * signal_len];
        double* row_out = &out_matrix[s * num_time_bins];

        for (int t = 0; t < effective_bins && t < num_time_bins; ++t) {
            int start = t * pool_size;
            double max_val = 0.0;
            for (int j = 0; j < pool_size; ++j) {
                double v = row_in[start + j];
                if (v > max_val) max_val = v;
            }
            row_out[t] = max_val;
        }
        // 尾部填充
        for (int t = effective_bins; t < num_time_bins; ++t) {
            row_out[t] = 0.0;
        }
    }
}
