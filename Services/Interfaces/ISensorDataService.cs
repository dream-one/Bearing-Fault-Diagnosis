using System.Collections.Concurrent;
using BearingFaultDiagnosis.Models;

namespace BearingFaultDiagnosis.Services.Interfaces
{
    public interface ISensorDataService
    {   
        ConcurrentQueue<double> BufferV1_X { get; }
        ConcurrentQueue<double> BufferV1_Y { get; }
        ConcurrentQueue<double> BufferV1_Z { get; }
        PuMetadata puMetadata { get; }

        void HandleVibrationDataForChart();
        Task TestFlushAsync(double[] signData);
        Task ProcessFolderAsync(string folderPath, Action<string> onMessage, Action<PuMetadata> onFileRead);
    
    }
}
