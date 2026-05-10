namespace BearingFaultDiagnosis.Models
{
    public class BearingFaultResult
    {
        /// <summary>外圈故障频率 (Hz)</summary>
        public double BPFO_Hz { get; set; }
        /// <summary>内圈故障频率 (Hz)</summary>
        public double BPFI_Hz { get; set; }
        /// <summary>滚动体故障频率 (Hz)</summary>
        public double BSF_Hz { get; set; }
        /// <summary>保持架故障频率 (Hz)</summary>
        public double FTF_Hz { get; set; }
        /// <summary>是否基于几何参数计算（而非查表）</summary>
        public bool IsCalculated { get; set; }
    }
}
