#include <iostream>
#include <vector>
#include <cmath>
#include <algorithm>
#include <fftw3.h>
#include <mutex>

#define EXPORT_API extern "C" __declspec(dllexport)
constexpr double M_PI = 3.1415926;
// 固定长度缓存 (假设 in_len 恒为 32768)
static constexpr int FIXED_LEN = 32768;
static fftw_complex* g_fft_in = nullptr;
static fftw_complex* g_fft_out = nullptr;
static fftw_plan g_plan_fwd = nullptr;
static fftw_plan g_plan_inv = nullptr;
static std::mutex g_init_mutex;

// 初始化接口 (C# 启动时调用一次)
EXPORT_API void InitFFTW() {
    std::lock_guard<std::mutex> lock(g_init_mutex);
    if (g_plan_fwd) return;
    g_fft_in = (fftw_complex*)fftw_malloc(sizeof(fftw_complex) * FIXED_LEN);
    g_fft_out = (fftw_complex*)fftw_malloc(sizeof(fftw_complex) * FIXED_LEN);
    g_plan_fwd = fftw_plan_dft_1d(FIXED_LEN, g_fft_in, g_fft_out, FFTW_FORWARD, FFTW_MEASURE);
    g_plan_inv = fftw_plan_dft_1d(FIXED_LEN, g_fft_out, g_fft_in, FFTW_BACKWARD, FFTW_MEASURE);
}

EXPORT_API void ComputeOrderTracking(
    const double* in_data, int in_len, double current_fs, double rpm,
    int target_spr, double* out_data, int* out_len, int out_capacity
) {
    if (in_len != FIXED_LEN || !g_plan_fwd) { *out_len = 0; return; }
    if (rpm <= 1e-5) { *out_len = 0; return; }

    // 1. 填充实数并执行正向 FFT (复用全局 Plan)
    for (int i = 0; i < in_len; ++i) {
        g_fft_in[i][0] = in_data[i];
        g_fft_in[i][1] = 0.0;
    }
    fftw_execute(g_plan_fwd);

    // 2. 构造解析信号 (Hilbert)
    int half = in_len / 2;
    g_fft_out[0][0] *= 1.0; g_fft_out[0][1] = 0.0; // DC
    for (int i = 1; i < half; ++i) {
        g_fft_out[i][0] *= 2.0; g_fft_out[i][1] *= 2.0;
    }
    if (in_len % 2 == 0) { // Nyquist
        g_fft_out[half][0] *= 1.0; g_fft_out[half][1] = 0.0;
    }
    for (int i = half + (in_len % 2); i < in_len; ++i) {
        g_fft_out[i][0] = 0.0; g_fft_out[i][1] = 0.0;
    }

    fftw_execute(g_plan_inv);

    // 3. 包络 + 去直流
    std::vector<double> envelope(in_len);
    double mean_env = 0.0;
    for (int i = 0; i < in_len; ++i) {
        double re = g_fft_in[i][0] / in_len;
        double im = g_fft_in[i][1] / in_len;
        envelope[i] = std::sqrt(re * re + im * im);
        mean_env += envelope[i];
    }
    mean_env /= in_len;
    for (int i = 0; i < in_len; ++i) envelope[i] -= mean_env;

    // 4. 角域重采样
    double duration = (double)in_len / current_fs;
    double total_rev = (rpm / 60.0) * duration;
    int target_points = (int)(total_rev * target_spr);
    *out_len = std::min(target_points, out_capacity); // 防溢出
    if (*out_len <= 0) return;
    if (*out_len == 1) { out_data[0] = envelope[0]; return; }

    for (int i = 0; i < *out_len; ++i) {
        double progress = (double)i / (*out_len - 1);
        double orig_idx = progress * (in_len - 1);
        int idx_f = (int)std::floor(orig_idx);
        int idx_c = std::min(idx_f + 1, in_len - 1);
        double frac = orig_idx - idx_f;
        out_data[i] = envelope[idx_f] * (1.0 - frac) + envelope[idx_c] * frac;
    }
}

EXPORT_API void ComputeSpectrum(const double* in_data, int in_len, double* out_mag) {
    if (in_len <= 0) return;
    // 加 Hanning 窗防泄漏
    for (int i = 0; i < in_len; ++i) {
        double w = 0.5 * (1.0 - cos(2.0 * M_PI * i / (in_len - 1)));
        g_fft_in[i][0] = in_data[i] * w;
        g_fft_in[i][1] = 0.0;
    }
    fftw_execute(g_plan_fwd);

    int half = in_len / 2;
    int out_size = half + (in_len % 2);
    for (int i = 0; i < half; ++i) {
        double re = g_fft_out[i][0], im = g_fft_out[i][1];
        out_mag[i] = 2.0 * std::sqrt(re * re + im * im) / in_len;
    }
    out_mag[0] /= 2.0;
    if (in_len % 2 == 0) {
        double re = g_fft_out[half][0], im = g_fft_out[half][1];
        out_mag[half] = std::sqrt(re * re + im * im) / in_len;
    }
}