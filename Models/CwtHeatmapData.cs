using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BearingFaultDiagnosis.Models
{
    public class CwtHeatmapData
    {
        public double[] Matrix { get; set; } = Array.Empty<double>();
        public int NumScales { get; set; }
        public int NumTimeBins { get; set; }
        public double FreqLow { get; set; }
        public double FreqHigh { get; set; }
    }
}
