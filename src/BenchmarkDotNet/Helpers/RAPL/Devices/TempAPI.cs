using System.Collections.Generic;
using System.IO;
using System;

namespace BenchmarkDotNet.Helpers.RAPL.Devices {
    public class TempAPI : DeviceAPI
    {
        override public List<(string, double)> openRAPLFiles()
        {
            string path = "/sys/class/thermal/";
            int thermalId = 0;
            while (Directory.Exists(path + "/thermal_zone" + thermalId))
            {
                string dirname = path + "/thermal_zone" + thermalId;
                string type = File.ReadAllText(dirname + "/type").Trim();
                if (type.Contains("pkg_temp"))
                    return new List<(string, double)>() {(dirname + "/temp", double.NaN)};
                thermalId++;
            }
            // throw new Exception("No thermal zone found for the package");
            return new List<(string, double)>();
        }
    }
}