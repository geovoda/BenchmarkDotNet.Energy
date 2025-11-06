using JetBrains.Annotations;

namespace BenchmarkDotNet.Diagnosers
{
    public class EnergyDiagnoserConfig
    {
        /// <param name="displayEnergyColumn">Display energy consumption. True by default.</param>
        [PublicAPI]
        public EnergyDiagnoserConfig(bool displayEnergyColumn = true)
        {
            DisplayEnergyColumn = displayEnergyColumn;
        }

        public bool DisplayEnergyColumn { get; }
    }
}