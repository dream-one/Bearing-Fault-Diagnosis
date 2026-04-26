using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BearingFaultDiagnosis.Models
{
    public class PuMetadata
    {
        public string source_file { get; set; }
        public string channel { get; set; }
        public int sample_rate { get; set; }
        public int rpm { get; set; }
        public int length { get; set; }
        public string dtype { get; set; }
        public string endian { get; set; }
        public override string ToString()
        {
            return $"source_file: {source_file}, channel: {channel}, sample_rate: {sample_rate}, rpm: {rpm}, length: {length}, dtype: {dtype}, endian: {endian}";
        }
    }
}
