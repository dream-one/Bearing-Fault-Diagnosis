#include <iostream>
#include <vector>
#include <cmath>
#include <algorithm>
#include <fftw3.h> // 引入 FFTW 库

// 宏定义，用于导出给 C# 调用
#define EXPORT_API extern "C" __declspec(dllexport)

/*
 * 接口函数：执行包络计算与角域重采样 (无状态，线程安全)
 * in_data: 原始信号数组指针 (输入，长度应为 32768)
 * in_len: 输入数组长度
 * current_fs: 原始采样率 (例如 64000.0)
 * rpm: 当前转速
 * target_spr: 每圈重采样点数 (例如 400)
 * out_data: 重采样结果数组指针 (输出，C#端需提前分配好足够大的内存)
 * out_len: 实际输出的有效数据长度指针
 */
EXPORT_API void ComputeOrderTracking(
    const double* in_data,
    int in_len,
    double current_fs,
    double rpm,
    int target_spr,
    double* out_data,
    int* out_len
) {
    if (in_len <= 0 || rpm <= 1e-5) {
        *out_len = 0;
        return;
    }

    // ==========================================
    // 1. 包络提取 (等效于 scipy.signal.hilbert)
    // ==========================================

    // 分配 FFTW 内存
    fftw_complex* in_fft = (fftw_complex*)fftw_malloc(sizeof(fftw_complex) * in_len);
    fftw_complex* out_fft = (fftw_complex*)fftw_malloc(sizeof(fftw_complex) * in_len);

    // 创建正向 FFT 计划 (使用 FFTW_ESTIMATE 以极速创建)
    fftw_plan p_fwd = fftw_plan_dft_1d(in_len, in_fft, out_fft, FFTW_FORWARD, FFTW_ESTIMATE);

    // 填充实数数据
    for (int i = 0; i < in_len; ++i) {
        in_fft[i][0] = in_data[i];
        in_fft[i][1] = 0.0;
    }

    // 执行正向 FFT
    fftw_execute(p_fwd);

    // 构造解析信号：负频率置零，正频率翻倍
    int half = in_len / 2;
    for (int i = 1; i < half; ++i) {
        out_fft[i][0] *= 2.0;
        out_fft[i][1] *= 2.0;
    }
    // 直流分量保持不变
    out_fft[0][1] = 0.0;
    for (int i = half; i < in_len; ++i) {
        out_fft[i][0] = 0.0;
        out_fft[i][1] = 0.0;
    }

    // 创建逆向 FFT 计划
    fftw_plan p_inv = fftw_plan_dft_1d(in_len, out_fft, in_fft, FFTW_BACKWARD, FFTW_ESTIMATE);
    // 执行逆向 FFT
    fftw_execute(p_inv);

    // 计算幅值包络并顺便计算均值用于去直流
    std::vector<double> envelope(in_len);
    double mean_env = 0.0;
    for (int i = 0; i < in_len; ++i) {
        // FFTW 的 IFFT 不会自动除以 N，这里手动缩放
        double re = in_fft[i][0] / in_len;
        double im = in_fft[i][1] / in_len;
        envelope[i] = std::sqrt(re * re + im * im);
        mean_env += envelope[i];
    }
    mean_env /= in_len;

    // 去除包络的直流分量 (Center the envelope)
    for (int i = 0; i < in_len; ++i) {
        envelope[i] -= mean_env;
    }

    // 清理 FFTW 内存与计划
    fftw_destroy_plan(p_fwd);
    fftw_destroy_plan(p_inv);
    fftw_free(in_fft);
    fftw_free(out_fft);

    // ==========================================
    // 2. 角域重采样 (基于稳态假设的高速线性插值)
    // ==========================================

    double duration = (double)in_len / current_fs;
    double total_revolutions = (rpm / 60.0) * duration;
    int target_points = (int)(total_revolutions * target_spr);

    *out_len = target_points;

    // 如果转速过低导致目标点数为0，直接返回
    if (target_points <= 0) return;

    // 执行插值重采样
    for (int i = 0; i < target_points; ++i) {
        // 计算当前目标点在原始数组中的相对进度 (0.0 ~ 1.0)
        double progress = (double)i / (target_points - 1);

        // 映射到原始数组的浮点索引
        double original_idx = progress * (in_len - 1);

        // 找到相邻的两个整数索引
        int idx_floor = (int)std::floor(original_idx);
        int idx_ceil = std::min(idx_floor + 1, in_len - 1);

        // 计算小数部分权重
        double fraction = original_idx - idx_floor;

        // 线性插值
        out_data[i] = envelope[idx_floor] * (1.0 - fraction) + envelope[idx_ceil] * fraction;
    }
}