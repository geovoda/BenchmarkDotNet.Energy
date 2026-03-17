using System.Collections.Generic;
using System.IO;

namespace BenchmarkDotNet.Helpers.RAPL.Devices
{

    public class PackageAPI : DeviceAPI
    {
        public PackageAPI(List<int> socketIds = null) : base(socketIds) {}

        public override List<(string, double)> openRAPLFiles()
        {
            List<(string, int)> socketDirectoryNames = this.GetSocketDirectoryNames();
            List<(string, double)> raplFiles = new List<(string, double)>();

            foreach (var (dir, id) in socketDirectoryNames)
            {
                double maxEnergyRange = double.Parse(File.ReadAllText(dir + "/max_energy_range_uj"));
                raplFiles.Add((dir + "/energy_uj", maxEnergyRange));
            }

            return raplFiles;
        }
    }

}