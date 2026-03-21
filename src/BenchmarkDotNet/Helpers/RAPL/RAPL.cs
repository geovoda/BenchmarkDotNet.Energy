using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Helpers.RAPL.Devices;
using BenchmarkDotNet.Reports;

public enum SensorType
{
    DramSensor,
    PackageSensor,
    CoreSensor,
    UncoreSensor,
    PsysSensor,
    TemperatureSensor,
}

namespace BenchmarkDotNet.Helpers.RAPL {

    public class RAPL
    {
        private Dictionary<SensorType, Sensor> sensors;

        public RAPL() => sensors = new Dictionary<SensorType, Sensor>();

        public void Start()
        {
            foreach (var kvp in sensors)
                kvp.Value.Start();
        }

        public void End()
        {
            foreach (var kvp in sensors)
                kvp.Value.End();
        }

        public bool IsValid() => sensors.All(kvp => kvp.Value.IsValid());

        public void LoadAllSensors()
        {
            sensors.Add(SensorType.PackageSensor, new Sensor("package", new PackageAPI(), CollectionApproach.Difference));
            sensors.Add(SensorType.DramSensor, new Sensor("dram", new DramAPI(), CollectionApproach.Difference));
            sensors.Add(SensorType.UncoreSensor, new Sensor("uncore", new UncoreAPI(), CollectionApproach.Difference));
            sensors.Add(SensorType.CoreSensor, new Sensor("core", new CoreAPI(), CollectionApproach.Difference));
            sensors.Add(SensorType.PsysSensor, new Sensor("psys", new PsysAPI(), CollectionApproach.Difference));
            sensors.Add(SensorType.TemperatureSensor, new Sensor("temperature", new TempAPI(), CollectionApproach.Average));
        }

        public List<EnergyMeasurement> GetEnergyMeasurements()
        {
            var result = new List<EnergyMeasurement>();
            long socketNumber = sensors[SensorType.PackageSensor].GetNumSockets();

            for (int socketId = 0; socketId < socketNumber; socketId++)
            {
                var packageEnergy = double.NaN;
                var dramEnergy = double.NaN;
                var coreEnergy = double.NaN;
                var uncoreEnergy = double.NaN;
                var psysEnergy = double.NaN;
                var averageCpuTemperature = double.NaN;

                if (sensors.TryGetValue(SensorType.PackageSensor, out var packageSensor))
                    packageEnergy = packageSensor.GetValueForSocket(socketId);

                if (sensors.TryGetValue(SensorType.DramSensor, out var dramSensor))
                    dramEnergy = dramSensor.GetValueForSocket(socketId);

                if (sensors.TryGetValue(SensorType.CoreSensor, out var coreSensor))
                    coreEnergy = coreSensor.GetValueForSocket(socketId);

                if (sensors.TryGetValue(SensorType.UncoreSensor, out var uncoreSensor))
                    uncoreEnergy = uncoreSensor.GetValueForSocket(socketId);

                if (sensors.TryGetValue(SensorType.PsysSensor, out var psysSensor))
                    psysEnergy = psysSensor.GetValueForSocket(socketId);

                if (sensors.TryGetValue(SensorType.TemperatureSensor, out var temperatureSensor))
                    averageCpuTemperature = temperatureSensor.GetValueForSocket(socketId);

                result.Add(new EnergyMeasurement(socketId, packageEnergy, dramEnergy, coreEnergy, uncoreEnergy, psysEnergy, averageCpuTemperature));
            }

            return result;
        }
    }
}