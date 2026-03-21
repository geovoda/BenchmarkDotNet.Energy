using System;
using System.Linq;
using System.Collections.Generic;

public enum CollectionApproach
{
    Average,
    Difference
}

namespace BenchmarkDotNet.Helpers.RAPL
{

    public class Sensor
    {
        public string Name { get; }
        private DeviceAPI api;
        private CollectionApproach approach;
        public List<double> Delta { get; private set; }
        private List<double> startValues;
        private List<double> endValues;

        public Sensor(string name, DeviceAPI api, CollectionApproach approach)
        {
            Name = name;
            this.api = api;
            this.approach = approach;
        }

        public void Start() => startValues = api.Collect();

        public void End()
        {
            endValues = api.Collect();
            UpdateDelta();
        }

        public bool IsValid()
            => startValues.All(val => val != -1.0)
               && endValues.All(val => val != -1.0)
               && Delta.Any(val => val >= 0);

        public long GetNumSockets() => api.GetNumSockets();

        private double ComputeDeltaDifference(int i)
        {
            if (endValues[i] - startValues[i] >= 0)
                return endValues[i] - startValues[i];

            return api.GetMaxEnergyValue(i) - startValues[i] + endValues[i];
        }

        private void UpdateDelta()
        {
            switch (approach)
            {
                case CollectionApproach.Difference:
                    this.Delta = Enumerable.Range(0, endValues.Count).Select(i => ComputeDeltaDifference(i)).ToList();
                    break;
                case CollectionApproach.Average:
                    this.Delta = Enumerable.Range(0, endValues.Count).Select(i => (endValues[i] + startValues[i]) / 2).ToList();
                    break;
                default:
                    throw new Exception("Collection approach is not available");
            }
        }

        public double GetValueForSocket(int socketId)
        {
            if (socketId >= Delta.Count)
                return double.NaN;

            return Delta[socketId];
        }
    }
}