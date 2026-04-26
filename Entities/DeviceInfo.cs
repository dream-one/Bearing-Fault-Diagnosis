using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace BearingFaultDiagnosis.Entities
{
    public class DeviceInfo
    {
        /// <summary>
        /// 设备主键 ID。
        /// </summary>
        public long Id { get; set; }

        /// <summary>
        /// 设备编码（唯一业务标识）。
        /// </summary>
        [Required]
        [MaxLength(50)]
        public string DeviceCode { get; set; } = string.Empty;

        /// <summary>
        /// 设备名称。
        /// </summary>
        [Required]
        [MaxLength(100)]
        public string DeviceName { get; set; } = string.Empty;

        /// <summary>
        /// 设备 IP 地址。
        /// </summary>
        [MaxLength(64)]
        public string? IpAddress { get; set; }

        /// <summary>
        /// 设备通信端口。
        /// </summary>
        public int? Port { get; set; }

        /// <summary>
        /// 通信协议（如 TCP/Modbus）。
        /// </summary>
        [MaxLength(50)]
        public string? Protocol { get; set; }

        /// <summary>
        /// 固件版本。
        /// </summary>
        [MaxLength(50)]
        public string? FirmwareVersion { get; set; }

        /// <summary>
        /// 安装位置。
        /// </summary>
        [MaxLength(200)]
        public string? InstallLocation { get; set; }

        /// <summary>
        /// 设备是否在线。
        /// </summary>
        public bool IsOnline { get; set; }

        /// <summary>
        /// 最近在线时间（UTC）。
        /// </summary>
        public DateTime? LastSeenAt { get; set; }

        /// <summary>
        /// 备注信息。
        /// </summary>
        [MaxLength(500)]
        public string? Remark { get; set; }

        /// <summary>
        /// 设备指令交互日志集合。
        /// </summary>
        public ICollection<DeviceCommandLog> CommandLogs { get; set; } = new List<DeviceCommandLog>();
    }
}
