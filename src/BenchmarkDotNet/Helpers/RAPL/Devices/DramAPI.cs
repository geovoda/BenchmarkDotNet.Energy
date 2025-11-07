using System.Collections.Generic;
using System.IO;
using System;

namespace BenchmarkDotNet.Helpers.RAPL.Devices
{

    public class DramAPI : DeviceAPI
    {
        public DramAPI(List<int> socketIds = null) : base(socketIds) {}

        public override List<string> openRAPLFiles()
        {
            List<(string, int)> socketDirectoryNames = this.GetSocketDirectoryNames();

            string getDramFile(string directoryName, int raplSocketId)
            {
                int raplDeviceId = 0;
                while (Directory.Exists(directoryName + "/intel-rapl:" + raplSocketId + ":" + raplDeviceId))
                {
                    var dirName = directoryName + "/intel-rapl:" + raplSocketId + ":" + raplDeviceId;
                    var content = File.ReadAllText(dirName + "/name").Trim();
                    if (content.Equals("dram"))
                        return dirName + "/energy_uj";

                    raplDeviceId += 1;
                }

                throw new Exception("PyRAPLCantInitDeviceAPI"); //TODO: Proper exceptions
            }

            List<string> raplFiles = new List<string>();
            foreach (var (socketDirectoryName, raplSocketId) in socketDirectoryNames)
            {
                raplFiles.Add(getDramFile(socketDirectoryName, raplSocketId));
            }

            return raplFiles;
        }
    }

}