using System.Collections.Generic;

namespace BenchmarkDotNet.Helpers.RAPL.Devices
{

    public class PackageAPI : DeviceAPI
    {
        public PackageAPI(List<int> socketIds = null) : base(socketIds) {}

        public override List<string> openRAPLFiles()
        {
            List<(string, int)> socketDirectoryNames = this.GetSocketDirectoryNames();
            List<string> raplFiles = new List<string>();

            foreach (var (dir, id) in socketDirectoryNames)
            {
                raplFiles.Add(dir + "/energy_uj");
            }

            return raplFiles;
        }
    }

}