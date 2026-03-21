using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Collections.Generic;

namespace BenchmarkDotNet.Helpers.RAPL
{
    public abstract class DeviceAPI
    {
        private List<int> _socketIds;
        protected List<(string filePath, double maxEnergyRange)> _sysFiles;
        private const string RaplDir = "/sys/class/powercap/intel-rapl/";

        public string GetRaplDir()
        {
            return RaplDir;
        }

        private List<int> GetCpus()
        {
            string apiFile = "/sys/devices/system/cpu/present";
            List<int> cpuList = new List<int>();
            Regex cpuCountRegex = new Regex(@"\d+|-");
            MatchCollection cpuMatches = cpuCountRegex.Matches(File.ReadAllText(apiFile).Trim());

            for (int i = 0; i < cpuMatches.Count; i++)
            {
                if (cpuMatches[i].Value == "-")
                {
                    int before = int.Parse(cpuMatches[i - 1].Value);
                    int after = int.Parse(cpuMatches[i + 1].Value);
                    foreach (int j in Enumerable.Range(before, after - before))
                        cpuList.Add(j);
                }
                else
                    cpuList.Add(int.Parse(cpuMatches[i].Value));
            }

            return cpuList;
        }

        private List<int> GetSocketIds()
        {
            List<int> socketIdList = new List<int>();

            foreach (var cpuId in GetCpus())
            {
                string path = $"/sys/devices/system/cpu/cpu{cpuId}/topology/physical_package_id";
                socketIdList.Add(int.Parse(File.ReadAllText(path).Trim()));
            }

            return socketIdList.Distinct().ToList();
        }

        public DeviceAPI(List<int> socketIds = null)
        {
            List<int> allSocketIds = GetSocketIds();
            if (socketIds == null){
                this._socketIds = allSocketIds;
            }
            else
            {
                foreach (var sid in socketIds)
                {
                    if (!allSocketIds.Contains(sid))
                        throw new Exception("PyRAPLBadSocketIdException"); //TODO: Proper exceptions

                    this._socketIds = socketIds;
                }
            }

            this._socketIds.Sort();
            this._sysFiles = this.openRAPLFiles();
        }

        public abstract List<(string, double)> openRAPLFiles();

        public virtual List<(string dirName, int raplId)> GetSocketDirectoryNames()
        {
            void addToResult((string dirName, int raplId) directoryInfo, List<(int, string, int)> result){
                string pkgStr = File.ReadAllText(directoryInfo.dirName + "/name").Trim();

                if (!pkgStr.Contains("package"))
                    return;
                var packageId = int.Parse(pkgStr.Split('-')[1]);

                if (this._socketIds != null && !this._socketIds.Contains(packageId)){
                    return;
                }

                result.Add((packageId, directoryInfo.dirName, directoryInfo.raplId));
            }

            var raplId = 0;
            var resultList = new List<(int packageId, string dirName, int raplId)>();

            while (Directory.Exists("/sys/class/powercap/intel-rapl/intel-rapl:" + raplId)){
                string dirName = "/sys/class/powercap/intel-rapl/intel-rapl:" + raplId;
                addToResult((dirName, raplId), resultList);
                raplId += 1;
            }

            if (resultList.Count != this._socketIds.Count)
                throw new Exception("PyRAPLCantInitDeviceAPI"); //TODO: Proper exceptions

            resultList.OrderBy(t => t.packageId);
            return resultList.Select(t => (t.dirName, t.raplId)).ToList();
        }
        // Collect all results for every socket.
        virtual public List<double> Collect()
        {
            var result = Enumerable.Range(0, this._socketIds.Count).Select(i => -1.0).ToList();
            for (int i = 0; i < _sysFiles.Count; i++){
                var deviceFile = this._sysFiles[i].filePath;
                //TODO: Test om der er mærkbar forskel ved at holde filen åben og læse linjen på ny
                if (double.TryParse(File.ReadAllText(deviceFile), out double energyVal))
                    result[this._socketIds[i]] = energyVal;
            }
            return result;
        }

        virtual public double GetMaxEnergyValue(int i) => _sysFiles[i].maxEnergyRange;

        public long GetNumSockets() => this._socketIds.Count;
    }
}