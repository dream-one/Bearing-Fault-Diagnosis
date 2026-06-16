#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
BearingFaultDiagnosis 传感器数据模拟器
======================================
通过 TCP 向上位机发送 93 字节传感器帧 (5kHz)，支持四种故障模式。
仅使用 Python 标准库，无需安装任何第三方包。

用法:
  python3 sensor_simulator.py                          # 默认连 WSL 网关
  python3 sensor_simulator.py --host 127.0.0.1         # 本机直连
  python3 sensor_simulator.py --port 9000               # 指定端口

快捷键:
  1~4  切换故障模式    +/-  调节严重程度    Q  退出
"""

import socket
import struct
import math
import random
import time
import argparse
import threading
import sys
import os

# ═══════════════════════════════════════════════════════════════
#  常量与帧格式
# ═══════════════════════════════════════════════════════════════

# <H I H H f f f f f f f f f f f f f f f B H  = 93 bytes
PKT_FMT = '<HIHH ffff fff fff fff fff fff f B H'
PKT_SIZE = struct.calcsize(PKT_FMT)
assert PKT_SIZE == 93, f"帧大小应为 93 字节, 实际 {PKT_SIZE}"

HEAD = 0x55AA
TAIL = 0x0D0A
SAMPLE_RATE = 5000        # Hz
FR = 2000.0 / 60.0        # 转频 ~33.33 Hz
BATCH_SIZE = 100           # 每批发送样本数 (20ms)
TWO_PI = 2.0 * math.pi

# ═══════════════════════════════════════════════════════════════
#  信号生成器 (纯标准库)
# ═══════════════════════════════════════════════════════════════

MODE_NORMAL          = 'normal'
MODE_BLADE_IMBALANCE = 'blade_imbalance'
MODE_BLOCKAGE        = 'blockage'
MODE_BOLT_LOOSEN     = 'bolt_loosen'

MODE_NAMES = {
    MODE_NORMAL:          '正常运行 (宽带噪声 + 微弱谐波)',
    MODE_BLADE_IMBALANCE: '叶片不平衡 (强 1×fr = 33.33Hz)',
    MODE_BLOCKAGE:        '风口堵塞 (电流偏移 + 噪声抬升)',
    MODE_BOLT_LOOSEN:     '螺栓松动 (多谐波 + 倾角漂移)',
}

class SignalGen:
    def __init__(self):
        self.mode = MODE_NORMAL
        self.severity = 50
        self._bolt_t0 = -1.0
        self._rng = random.Random(42)

    def set_mode(self, mode):
        self.mode = mode
        if mode == MODE_BOLT_LOOSEN:
            self._bolt_t0 = -1.0

    # ── 高斯噪声 ──
    def _gauss(self, sigma):
        return self._rng.gauss(0, sigma)

    # ── 振动 V1_X ──
    def vib(self, t):
        if self.mode == MODE_BLADE_IMBALANCE:
            return self._vib_blade(t)
        elif self.mode == MODE_BLOCKAGE:
            return self._vib_block(t)
        elif self.mode == MODE_BOLT_LOOSEN:
            return self._vib_bolt(t)
        return self._vib_normal(t)

    def _vib_normal(self, t):
        s = self._gauss(0.3)
        s += 0.05 * math.sin(TWO_PI * FR * t)
        s += 0.02 * math.sin(TWO_PI * FR * 2 * t + 0.5)
        return s

    def _vib_blade(self, t):
        amp = 1.0 + 3.0 * (self.severity / 50.0)
        s = amp * math.sin(TWO_PI * FR * t)
        s += 0.1 * amp * math.sin(TWO_PI * FR * 2 * t + 0.3)
        s += self._gauss(0.2)
        return s

    def _vib_block(self, t):
        noise_amp = 0.3 * (1.0 + self.severity / 30.0)
        s = self._gauss(noise_amp)
        s += 0.1 * math.sin(TWO_PI * FR * t)
        bpf_amp = 0.15 * (1.0 + self.severity / 50.0)
        s += bpf_amp * math.sin(TWO_PI * FR * 7 * t)
        return s

    def _vib_bolt(self, t):
        sev = self.severity / 50.0
        s = self._gauss(0.4)
        s += 0.3 * (1 + sev) * math.sin(TWO_PI * FR * t)
        s += 0.2 * sev * math.sin(TWO_PI * FR * 2 * t)
        s += 0.15 * sev * math.sin(TWO_PI * FR * 3 * t)
        s += 0.1 * sev * math.sin(TWO_PI * FR * 0.5 * t)
        # 周期性冲击
        phase = (t * FR) % 1.0
        if phase < 0.01:
            s += 1.5 * sev * math.exp(-phase * 500)
        return s

    # ── 倾角 ──
    def tilt(self, t):
        base = 0.05
        if self.mode == MODE_BOLT_LOOSEN:
            if self._bolt_t0 < 0:
                self._bolt_t0 = t
            rate = 0.0005 + 0.002 * (self.severity / 50.0)
            drift = rate * max(0, t - self._bolt_t0)
            return base + drift + self._gauss(0.001)
        return base + self._gauss(0.005)

    # ── 电流 ──
    def current(self):
        base = 15.0
        noise = self._gauss(0.1)
        if self.mode == MODE_BLOCKAGE:
            offset = (self.severity - 50) / 25.0
            return base + offset + noise
        return base + noise

    # ── 温度 ──
    def temp(self, t):
        return 25.0 + 3.0 * math.sin(TWO_PI * t / 3600.0) + self._gauss(0.3)


# ═══════════════════════════════════════════════════════════════
#  帧构建
# ═══════════════════════════════════════════════════════════════

def build_frame(gen, t, sample_idx):
    """构建一个 93 字节的传感器帧"""
    return struct.pack(
        PKT_FMT,
        HEAD,
        int(t) & 0xFFFFFFFF,
        sample_idx & 0xFFFF,
        sample_idx & 0xFFFF,
        gen.tilt(t),               # Roll
        gen._gauss(0.002),         # Pitch
        gen.temp(t),               # Temp
        60.0 + gen._gauss(1.0),    # Humi
        gen.vib(t),                # V1_X (主通道)
        gen._gauss(0.1),           # V1_Y
        gen._gauss(0.1),           # V1_Z
        0.0, 0.0, 0.0,             # V2
        0.0, 0.0, 0.0,             # V3
        0.0, 0.0, 0.0,             # V4
        0.0, 0.0, 0.0,             # V5
        gen.current(),             # Current
        sample_idx & 0xFF,         # CheckSum
        TAIL
    )


# ═══════════════════════════════════════════════════════════════
#  终端 UI
# ═══════════════════════════════════════════════════════════════

def print_banner(host, port):
    print("╔══════════════════════════════════════════════════════╗")
    print("║      BearingFaultDiagnosis 传感器数据模拟器          ║")
    print("╠══════════════════════════════════════════════════════╣")
    print(f"║  目标: {host}:{port:<45}║")
    print(f"║  采样率: {SAMPLE_RATE} Hz  |  帧: {PKT_SIZE} bytes  |  批量: {BATCH_SIZE}    ║")
    print("╚══════════════════════════════════════════════════════╝")

def print_menu(gen):
    print("\n── 故障模式 (按数字键切换) ──────────────────────")
    for i, (key, name) in enumerate(MODE_NAMES.items(), 1):
        arrow = " ◄" if gen.mode == key else ""
        print(f"  [{i}] {name}{arrow}")
    print("── 参数 ──────────────────────────────────────")
    print("  [+/-] 调节严重程度  |  [Ctrl+C] 退出")
    print("──────────────────────────────────────────────")
    print(f"  当前: [{MODE_NAMES[gen.mode]}] 严重程度: {gen.severity}%\n")


# ═══════════════════════════════════════════════════════════════
#  键盘输入 (跨平台)
# ═══════════════════════════════════════════════════════════════

class KeyReader:
    """非阻塞键盘输入读取器"""
    def __init__(self):
        self._key = None
        self._lock = threading.Lock()
        self._thread = threading.Thread(target=self._read_loop, daemon=True)
        self._thread.start()

    def _read_loop(self):
        try:
            import tty, termios
            fd = sys.stdin.fileno()
            old = termios.tcgetattr(fd)
            tty.setcbreak(fd)
            try:
                while True:
                    ch = sys.stdin.read(1)
                    with self._lock:
                        self._key = ch
            finally:
                termios.tcsetattr(fd, termios.TCSADRAIN, old)
        except Exception:
            # Windows fallback
            while True:
                ch = sys.stdin.read(1)
                with self._lock:
                    self._key = ch

    def get_key(self):
        with self._lock:
            k = self._key
            self._key = None
            return k


# ═══════════════════════════════════════════════════════════════
#  主发送循环
# ═══════════════════════════════════════════════════════════════

def send_loop(sock, gen):
    """以 5kHz 速率批量发送传感器帧"""
    total = 0
    batch_interval = BATCH_SIZE / SAMPLE_RATE   # 0.02s
    t0 = time.monotonic()
    last_stat_t = t0
    last_stat_n = 0

    reader = KeyReader()

    print_menu(gen)

    try:
        while True:
            bt = time.monotonic()

            # 处理键盘输入
            ch = reader.get_key()
            if ch:
                handle_key(ch, gen)

            # 构建并发送一批数据
            parts = []
            for i in range(BATCH_SIZE):
                idx = total + i
                t = idx / SAMPLE_RATE
                parts.append(build_frame(gen, t, idx))
            sock.sendall(b''.join(parts))
            total += BATCH_SIZE

            # 统计 (每秒更新)
            now = time.monotonic()
            dt = now - last_stat_t
            if dt >= 1.0:
                dn = total - last_stat_n
                rate = dn / dt
                kbps = rate * PKT_SIZE / 1024.0
                elapsed = now - t0
                data_s = total / SAMPLE_RATE
                h, m = int(elapsed // 3600), int((elapsed % 3600) // 60)
                s = int(elapsed % 60)
                sys.stdout.write(
                    f"\r  速率: {rate:6.0f} 样本/秒 | {kbps:5.0f} KB/s"
                    f" | 总: {total:>10,} ({data_s:7.1f}s 数据)"
                    f" | 运行 {h:02d}:{m:02d}:{s:02d}  "
                )
                sys.stdout.flush()
                last_stat_t = now
                last_stat_n = total

            # 精确速率控制
            elapsed_batch = time.monotonic() - bt
            sleep_ms = batch_interval - elapsed_batch
            if sleep_ms > 0:
                time.sleep(sleep_ms)

    except (ConnectionResetError, BrokenPipeError, OSError) as e:
        print(f"\n\n连接中断: {e}")
    except KeyboardInterrupt:
        pass

    elapsed = time.monotonic() - t0
    print(f"\n\n已停止。共发送 {total:,} 个样本 ({total / SAMPLE_RATE:.1f}s 数据量)")


def handle_key(ch, gen):
    key_map = {'1': MODE_NORMAL, '2': MODE_BLADE_IMBALANCE,
               '3': MODE_BLOCKAGE, '4': MODE_BOLT_LOOSEN}
    if ch in key_map:
        gen.set_mode(key_map[ch])
        print(f"\r  ► [{MODE_NAMES[gen.mode]}] 严重程度: {gen.severity}%              ")
    elif ch in ('+', '='):
        gen.severity = min(100, gen.severity + 10)
        print(f"\r  ► [{MODE_NAMES[gen.mode]}] 严重程度: {gen.severity}%              ")
    elif ch == '-':
        gen.severity = max(0, gen.severity - 10)
        print(f"\r  ► [{MODE_NAMES[gen.mode]}] 严重程度: {gen.severity}%              ")
    elif ch in ('q', 'Q'):
        raise KeyboardInterrupt


# ═══════════════════════════════════════════════════════════════
#  入口
# ═══════════════════════════════════════════════════════════════

def main():
    parser = argparse.ArgumentParser(description='BearingFaultDiagnosis 传感器数据模拟器')
    parser.add_argument('--host', default='10.255.255.254',
                        help='目标主机 (WSL 默认 10.255.255.254, 本机用 127.0.0.1)')
    parser.add_argument('--port', type=int, default=5000, help='目标端口 (默认 5000, 见 appsettings.json)')
    args = parser.parse_args()

    print_banner(args.host, args.port)

    gen = SignalGen()

    # 连接
    print("正在连接...", end=' ', flush=True)
    try:
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.connect((args.host, args.port))
    except Exception as e:
        print(f"\n连接失败: {e}")
        print("请确保上位机 TCP 服务器已启动。")
        if '10.255' in args.host:
            print("提示: WSL 网关地址可能变化，请用 --host 指定正确的 Windows 主机 IP。")
        sys.exit(1)

    print("已连接!\n")

    try:
        send_loop(sock, gen)
    finally:
        sock.close()


if __name__ == '__main__':
    main()
