using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BenchmarkDotNet.Helpers.RAPL.Devices
{

    public class PsysAPI : DeviceAPI
    {
        public PsysAPI(List<int> socketIds = null) : base(socketIds) {}

        public override List<(string, double)> openRAPLFiles()
        {
            string directoryName = this.GetRaplDir();
            List<(string, double)> raplFiles = new List<(string, double)>();

            int raplSocketId = 0;
            while (Directory.Exists(directoryName + "/intel-rapl:" + raplSocketId))
            {
                var dirName = directoryName + "/intel-rapl:" + raplSocketId;
                var content = File.ReadAllText(dirName + "/name").Trim();
                if (content.Equals("psys"))
                {
                    double maxEnergyRange = double.Parse(File.ReadAllText(dirName + "/max_energy_range_uj"));
                    raplFiles.Add((dirName + "/energy_uj", maxEnergyRange));
                }

                raplSocketId += 1;
            }

            return raplFiles;
        }

        // TODO AAU: Check if this works
        public override List<double> Collect()
        {
            if (this._sysFiles.Count == 0)
                return new List<double>() {-1};

            if (double.TryParse(File.ReadAllText(_sysFiles[0].filePath), out double energyVal))
                return new List<double> { energyVal };

            return new List<double>() {-1};
        }
    }

}