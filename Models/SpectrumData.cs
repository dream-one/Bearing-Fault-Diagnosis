using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BearingFaultDiagnosis.Models
{
    public class SpectrumData
    {
        public double[] Magnitudes { get; set; } = Array.Empty<double>();
        public double Resolution { get; set; } = 1.0;
    }
}
