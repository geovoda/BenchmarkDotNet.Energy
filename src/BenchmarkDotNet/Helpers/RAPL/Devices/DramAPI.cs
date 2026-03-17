using System.Collections.Generic;
using System.IO;
using System;

namespace BenchmarkDotNet.Helpers.RAPL.Devices
{

    public class DramAPI : DeviceAPI
    {
        public DramAPI(List<int> socketIds = null) : base(socketIds) {}

        public override List<(string, double)> openRAPLFiles()
        {
            List<(string, int)> socketDirectoryNames = this.GetSocketDirectoryNames();

            (string, double) getDramFile(string directoryName, int raplSocketId)
            {
                int raplDeviceId = 0;
                while (Directory.Exists(directoryName + "/intel-rapl:" + raplSocketId + ":" + raplDeviceId))
                {
                    var dirName = directoryName + "/intel-rapl:" + raplSocketId + ":" + raplDeviceId;
                    var content = File.ReadAllText(dirName + "/name").Trim();
                    if (content.Equals("dram"))
                    {
                        double maxEnergyRange = double.Parse(File.ReadAllText(dirName + "/max_energy_range_uj"));
                        return (dirName + "/energy_uj", maxEnergyRange);
                    }

                    raplDeviceId += 1;
                }

                throw new Exception("PyRAPLCantInitDeviceAPI"); //TODO: Proper exceptions
            }

            List<(string, double)> raplFiles = new List<(string, double)>();
            foreach (var (socketDirectoryName, raplSocketId) in socketDirectoryNames)
            {
                raplFiles.Add(getDramFile(socketDirectoryName, raplSocketId));
            }

            return raplFiles;
        }
    }

}