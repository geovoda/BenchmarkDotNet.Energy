using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Helpers.RAPL.Devices;

namespace BenchmarkDotNet.Helpers.RAPL {

    public class RAPL
    {
        private List<Sensor> _apis;

        public RAPL() => _apis = new List<Sensor>();

        public void Start() => _apis.ForEach(api => api.Start());

        public void End() => _apis.ForEach(api => api.End());

        public bool IsValid() => _apis.All(api => api.IsValid());

        public void AddPackageSensor()
        {
            Sensor sensor = new Sensor("package", new PackageAPI(), CollectionApproach.DIFFERENCE);
            _apis.Add(sensor);
        }

        public void AddDRAMSensor()
        {
            Sensor sensor = new Sensor("dram", new DramAPI(), CollectionApproach.DIFFERENCE);
            _apis.Add(sensor);
        }

        public double GetDeviceResult(string deviceName)
        {
            foreach (var api in _apis)
            {
                if (api.Name == deviceName)
                    return api.Delta[0];
            }

            return 0;
        }
    }
}