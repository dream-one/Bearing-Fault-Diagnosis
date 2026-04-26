using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BearingFaultDiagnosis.Services.Implements;

namespace BearingFaultDiagnosis.Models
{
    public class SensorDisplayItem
    {
        // 基本信息
        public uint Time { get; set; }
        public ushort CountQ { get; set; }
        public ushort CountPn { get; set; }
        public float Roll { get; set; }
        public float Pitch { get; set; }
        public float Temperature { get; set; }
        public float Humidity { get; set; }

        // 振动数据 - 5个点的三轴数据
        public List<VibrationData> Vibrations { get; set; } = new List<VibrationData>();

        // 电流
        public float Current { get; set; }



        // 构造函数
        public SensorDisplayItem()
        {
            // 初始化5个振动数据点
            for (int i = 0; i < 5; i++)
            {
                Vibrations.Add(new VibrationData());
            }
        }

        // 从结构体转换的静态方法
        public static SensorDisplayItem FromStruct(SensorFrameStruct frame)
        {
            var item = new SensorDisplayItem
            {
                Time = frame.Time_T,
                CountQ = frame.Cnt_Q,
                CountPn = frame.Cnt_Pn,
                Roll = frame.Roll,
                Pitch = frame.Pitch,
                Temperature = frame.Temp,
                Humidity = frame.Humi,
                Current = frame.Current
            };

         
            return item;
        }

        public static SensorDisplayItem SensorDisplayItemForChart(SensorFrameStruct frame)
        {
            var item = new SensorDisplayItem() { Time = frame.Time_T };
            item.Vibrations[0] = new VibrationData { X = frame.V1_X, Y = frame.V1_Y, Z = frame.V1_Z };
            item.Vibrations[1] = new VibrationData { X = frame.V2_X, Y = frame.V2_Y, Z = frame.V2_Z };
            item.Vibrations[2] = new VibrationData { X = frame.V3_X, Y = frame.V3_Y, Z = frame.V3_Z };
            item.Vibrations[3] = new VibrationData { X = frame.V4_X, Y = frame.V4_Y, Z = frame.V4_Z };
            item.Vibrations[4] = new VibrationData { X = frame.V5_X, Y = frame.V5_Y, Z = frame.V5_Z };
            return item;
            }

        // 重写ToString方法便于显示
        public override string ToString()
        {
            return $"Time: {Time}, Roll: {Roll:F2}°, Pitch: {Pitch:F2}°, Temp: {Temperature:F1}°C, Humi: {Humidity:F1}%, Current: {Current:F2}A";
        }
    }

    // 振动数据类
    public class VibrationData
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        public override string ToString()
        {
            return $"X:{X:F3}, Y:{Y:F3}, Z:{Z:F3}";
        }

        // 计算振动幅度
        public double GetMagnitude()
        {
            return Math.Sqrt(X * X + Y * Y + Z * Z);
        }
    }
}
