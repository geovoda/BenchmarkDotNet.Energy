using System;
using System.Linq;
using System.Collections.Generic;

public enum CollectionApproach
{
    AVERAGE,
    DIFFERENCE
}

namespace BenchmarkDotNet.Helpers.RAPL
{

    public class Sensor
    {
        public string Name { get; }
        private DeviceAPI _api;
        private CollectionApproach _approach;
        public List<double> Delta { get; private set; }
        private List<double> _startValue;
        private List<double> _endValue;

        public Sensor(string name, DeviceAPI api, CollectionApproach approach)
        {
            Name = name;
            _api = api;
            _approach = approach;
        }

        public void Start() => _startValue = _api.Collect();

        public void End()
        {
            _endValue = _api.Collect();
            UpdateDelta();
        }

        public bool IsValid()
            => _startValue.All(val => val != -1.0)
               && _endValue.All(val => val != -1.0)
               && Delta.Any(val => val >= 0);

        private void UpdateDelta()
        {
            switch (_approach)
            {
                case CollectionApproach.DIFFERENCE:
                    this.Delta = Enumerable.Range(0, _endValue.Count).Select(i => _endValue[i] - _startValue[i]).ToList();
                    break;
                case CollectionApproach.AVERAGE:
                    this.Delta = Enumerable.Range(0, _endValue.Count).Select(i => (_endValue[i] + _startValue[i]) / 2).ToList();
                    break;
                default:
                    throw new Exception("Collection approach is not available");
            }
        }
    }
}