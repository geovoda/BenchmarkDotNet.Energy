using JetBrains.Annotations;

namespace BenchmarkDotNet.Diagnosers
{
    public class EnergyDiagnoserConfig
    {
        /// <param name="displayPackageColumn">Display energy consumption for CPU's package power zone. True by default.</param>
        /// <param name="displayDramColumn">Display energy consumption for CPU's DRAM power zone. True by default.</param>
        /// <param name="displayUncoreColumn">Display energy consumption for CPU's uncore power zone. True by default.</param>
        /// <param name="displayCoreColumn">Display energy consumption for CPU's core power zone. True by default.</param>
        /// <param name="displayPsysColumn">Display energy consumption for system's psys power zone. True by default.</param>
        /// <param name="displayCpuTemperatureColumn">Display the average CPU temperature. True by default.</param>
        [PublicAPI]
        public EnergyDiagnoserConfig(
            bool displayPackageColumn = true,
            bool displayDramColumn = true,
            bool displayUncoreColumn = true,
            bool displayCoreColumn = true,
            bool displayPsysColumn = true,
            bool displayCpuTemperatureColumn = true)
        {
            DisplayPackageColumn = displayPackageColumn;
            DisplayDramColumn = displayDramColumn;
            DisplayUncoreColumn = displayUncoreColumn;
            DisplayCoreColumn = displayCoreColumn;
            DisplayPsysColumn = displayPsysColumn;
            DisplayCpuTemperatureColumn = displayCpuTemperatureColumn;
        }

        public bool DisplayPackageColumn { get; }
        public bool DisplayDramColumn { get; }
        public bool DisplayUncoreColumn { get; }
        public bool DisplayCoreColumn { get; }
        public bool DisplayPsysColumn { get; }
        public bool DisplayCpuTemperatureColumn { get; }
    }
}