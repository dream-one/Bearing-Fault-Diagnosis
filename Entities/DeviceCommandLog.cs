using System;
using System.ComponentModel.DataAnnotations;

namespace BearingFaultDiagnosis.Entities
{
    public class DeviceCommandLog
    {
        /// <summary>
        /// 日志主键 ID。
        /// </summary>
        public long Id { get; set; }

        /// <summary>
        /// 关联设备 ID。
        /// </summary>
        [Required]
        public long DeviceInfoId { get; set; }

        /// <summary>
        /// 指令名称。
        /// </summary>
        [Required]
        [MaxLength(100)]
        public string CommandName { get; set; } = string.Empty;

        /// <summary>
        /// 指令请求载荷。
        /// </summary>
        public string? CommandPayload { get; set; }

        /// <summary>
        /// 指令响应载荷。
        /// </summary>
        public string? ResponsePayload { get; set; }

        /// <summary>
        /// 指令发送时间（UTC）。
        /// </summary>
        public DateTime SentAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 指令响应时间（UTC）。
        /// </summary>
        public DateTime? RespondedAt { get; set; }

        /// <summary>
        /// 指令执行是否成功。
        /// </summary>
        public bool IsSuccess { get; set; }

        /// <summary>
        /// 错误信息。
        /// </summary>
        [MaxLength(500)]
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// 关联设备导航属性。
        /// </summary>
        public DeviceInfo DeviceInfo { get; set; } = null!;
    }
}
