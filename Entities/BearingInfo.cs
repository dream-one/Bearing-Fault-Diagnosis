using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BearingFaultDiagnosis.Entities
{
    public class BearingInfo
    {
        public long Id { get; set; }

        /// <summary>
        /// 厂商（如 SKF, NSK, FAG）
        /// </summary>
        [MaxLength(100)]
        public string? Manufacturer { get; set; }

        /// <summary>
        /// 型号（如 6205, 7311B）
        /// </summary>
        [Required]
        [MaxLength(100)]
        public string Model { get; set; } = string.Empty;

        /// <summary>
        /// 滚子数 (n)，几何参数必填
        /// </summary>
        [Required]
        public int RollerCount_n { get; set; }

        /// <summary>
        /// 滚子直径 (d)，单位 mm
        /// </summary>
        public double? RollerDiameter_d { get; set; }

        /// <summary>
        /// 节径 (D)，单位 mm
        /// </summary>
        public double? PitchDiameter_D { get; set; }

        /// <summary>
        /// 接触角 (α)，单位度，默认 0
        /// </summary>
        public double ContactAngle_alpha { get; set; } = 0;

        /// <summary>
        /// 外圈故障特征系数 (BPFO)
        /// </summary>
        public double? BPFO_Multiplier { get; set; }

        /// <summary>
        /// 内圈故障特征系数 (BPFI)
        /// </summary>
        public double? BPFI_Multiplier { get; set; }

        /// <summary>
        /// 滚动体故障特征系数 (BSF)
        /// </summary>
        public double? BSF_Multiplier { get; set; }

        /// <summary>
        /// 保持架故障特征系数 (FTF)
        /// </summary>
        public double? FTF_Multiplier { get; set; }

        /// <summary>
        /// 显示名称（厂商 - 型号）
        /// </summary>
        [NotMapped]
        public string DisplayName => $"{Manufacturer} - {Model}";
    }
}
