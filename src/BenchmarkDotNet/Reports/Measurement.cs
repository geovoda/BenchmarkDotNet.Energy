using System;
using System.Collections.Generic;
#if DEBUG
using System.Diagnostics;
#endif
using System.Globalization;
using System.Linq;
using System.Text;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Helpers;
using Perfolizer.Horology;

namespace BenchmarkDotNet.Reports
{
    /// <summary>
    /// The basic captured statistics for a benchmark
    /// </summary>
    public struct Measurement : IComparable<Measurement>
    {
        // We always use the same CultureInfo to simplify string conversions (ToString and Parse)
        private static readonly CultureInfo MainCultureInfo = DefaultCultureInfo.Instance;

        private const string NsSymbol = "ns";
        private const string OpSymbol = "op";

        private struct EnergySymbols
        {
            public const string SocketSymbol = "socket";
            public const string DramJSymbol = "uJdram";
            public const string PackageJSymbol = "uJpkg";
            public const string CoreJSymbol = "uJcore";
            public const string UncoreJSymbol = "uJuncore";
            public const string PsysJSymbol = "uJpsys";
            public const string CpuCelsiusTemperatureSymbol = "degCCpu";
        }

        private static Measurement Error() => new Measurement(-1, IterationMode.Unknown, IterationStage.Unknown, 0, 0, 0, new List<EnergyMeasurement> { EnergyMeasurement.Error(0) });

        private static readonly int IterationInfoNameMaxWidth
            = Enum.GetNames(typeof(IterationMode)).Max(text => text.Length) + Enum.GetNames(typeof(IterationStage)).Max(text => text.Length);

        public IterationMode IterationMode { get; }

        public IterationStage IterationStage { get; }

        public int LaunchIndex { get; }

        public int IterationIndex { get; }

        /// <summary>
        /// Gets the number of operations performed.
        /// </summary>
        public long Operations { get; }

        /// <summary>
        /// Gets the total number of nanoseconds it took to perform all operations.
        /// </summary>
        public double Nanoseconds { get; }

        /// <summary>
        /// Gets the energy consumed to perform all operations.
        /// </summary>
        public List<EnergyMeasurement> EnergyMeasurements { get; }

        /// <summary>
        /// Creates an instance of <see cref="Measurement"/> struct.
        /// </summary>
        /// <param name="launchIndex"></param>
        /// <param name="iterationMode"></param>
        /// <param name="iterationStage"></param>
        /// <param name="iterationIndex"></param>
        /// <param name="operations">The number of operations performed.</param>
        /// <param name="nanoseconds">The total number of nanoseconds it took to perform all operations.</param>
        /// <param name="energyMeasurements">The energy consumed to perform all operations grouped per cpu Socket id.</param>
        public Measurement(int launchIndex, IterationMode iterationMode, IterationStage iterationStage, int iterationIndex, long operations, double nanoseconds, List<EnergyMeasurement> energyMeasurements)
        {
            Operations = operations;
            Nanoseconds = nanoseconds;
            EnergyMeasurements = energyMeasurements;
            LaunchIndex = launchIndex;
            IterationMode = iterationMode;
            IterationStage = iterationStage;
            IterationIndex = iterationIndex;
        }

        private static IterationMode ParseIterationMode(string name) => Enum.TryParse(name, out IterationMode mode) ? mode : IterationMode.Unknown;

        private static IterationStage ParseIterationStage(string name) => Enum.TryParse(name, out IterationStage stage) ? stage : IterationStage.Unknown;

        /// <summary>
        /// Gets the average duration of one operation.
        /// </summary>
        public TimeInterval GetAverageTime() => TimeInterval.FromNanoseconds(Nanoseconds / Operations);

        public int CompareTo(Measurement other) => Nanoseconds.CompareTo(other.Nanoseconds);

        public override string ToString()
        {
            var builder = new StringBuilder();

            builder.Append((IterationMode.ToString() + IterationStage).PadRight(IterationInfoNameMaxWidth, ' '));
            builder.Append(' ');

            // Usually, a benchmarks takes more than 10 iterations (rarely more than 99)
            // PadLeft(2, ' ') looks like a good trade-off between alignment and amount of characters
            builder.Append(IterationIndex.ToString(MainCultureInfo).PadLeft(2, ' '));
            builder.Append(": ");

            builder.Append(Operations.ToString(MainCultureInfo));
            builder.Append(' ');
            builder.Append(OpSymbol);
            builder.Append(", ");

            builder.Append(Nanoseconds.ToString("0.00", MainCultureInfo));
            builder.Append(' ');
            builder.Append(NsSymbol);
            builder.Append(", ");

            builder.Append(GetAverageTime().ToDefaultString("0.0000").ToAscii());
            builder.Append("/op");

            // If we have no energy info at all (RAPL disabled), just stop here.
            if (EnergyMeasurements.Count == 0 || EnergyMeasurements.All(em => double.IsNaN(em.PackageEnergy) && double.IsNaN(em.DramEnergy) &&
                double.IsNaN(em.CoreEnergy) && double.IsNaN(em.UncoreEnergy) && double.IsNaN(em.PsysEnergy) &&
                double.IsNaN(em.AverageCpuTemperature)))
                return builder.ToString();

            builder.Append(", ");

            foreach (var energyMeasurement in EnergyMeasurements)
            {
                builder.Append(energyMeasurement.PackageEnergy.ToString("0.00", MainCultureInfo).ToAscii());
                builder.Append(' ');
                builder.Append(EnergySymbols.PackageJSymbol);
                builder.Append("_");
                builder.Append(EnergySymbols.SocketSymbol);
                builder.Append("_");
                builder.Append(energyMeasurement.SocketId);
                builder.Append(", ");

                builder.Append(energyMeasurement.DramEnergy.ToString("0.00", MainCultureInfo).ToAscii());
                builder.Append(' ');
                builder.Append(EnergySymbols.DramJSymbol);
                builder.Append("_");
                builder.Append(EnergySymbols.SocketSymbol);
                builder.Append("_");
                builder.Append(energyMeasurement.SocketId);
                builder.Append(", ");

                builder.Append(energyMeasurement.CoreEnergy.ToString("0.00", MainCultureInfo).ToAscii());
                builder.Append(' ');
                builder.Append(EnergySymbols.CoreJSymbol);
                builder.Append("_");
                builder.Append(EnergySymbols.SocketSymbol);
                builder.Append("_");
                builder.Append(energyMeasurement.SocketId);
                builder.Append(", ");

                builder.Append(energyMeasurement.UncoreEnergy.ToString("0.00", MainCultureInfo).ToAscii());
                builder.Append(' ');
                builder.Append(EnergySymbols.UncoreJSymbol);
                builder.Append("_");
                builder.Append(EnergySymbols.SocketSymbol);
                builder.Append("_");
                builder.Append(energyMeasurement.SocketId);
                builder.Append(", ");

                builder.Append(energyMeasurement.PsysEnergy.ToString("0.00", MainCultureInfo).ToAscii());
                builder.Append(' ');
                builder.Append(EnergySymbols.PsysJSymbol);
                builder.Append("_");
                builder.Append(EnergySymbols.SocketSymbol);
                builder.Append("_");
                builder.Append(energyMeasurement.SocketId);
                builder.Append(", ");

                builder.Append(energyMeasurement.AverageCpuTemperature.ToString("0.00", MainCultureInfo).ToAscii());
                builder.Append(' ');
                builder.Append(EnergySymbols.CpuCelsiusTemperatureSymbol);
                builder.Append("_");
                builder.Append(EnergySymbols.SocketSymbol);
                builder.Append("_");
                builder.Append(energyMeasurement.SocketId);
            }

            return builder.ToString();
        }

        /// <summary>
        /// Parses the benchmark statistics from the plain text line.
        ///
        /// E.g. given the input <paramref name="line"/>:
        ///
        ///     WorkloadTarget 1: 10 op, 1005842518 ns
        ///
        /// Will extract the number of <see cref="Operations"/> performed and the
        /// total number of <see cref="Nanoseconds"/> it took to perform them.
        /// </summary>
        /// <param name="line">The line to parse.</param>
        /// <param name="processIndex">Process launch index, indexed from one.</param>
        /// <returns>An instance of <see cref="Measurement"/> if parsed successfully. <c>Null</c> in case of any trouble.</returns>
        // ReSharper disable once UnusedParameter.Global
        public static Measurement Parse(string line, int processIndex)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(GcStats.ResultsLinePrefix))
                return Error();

            try
            {
                var lineSplit = line.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries);

                string iterationInfo = lineSplit[0];
                var iterationInfoSplit = iterationInfo.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                int iterationStageIndex = 0;
                for (int i = 1; i < iterationInfoSplit[0].Length; i++)
                    if (char.IsUpper(iterationInfoSplit[0][i]))
                    {
                        iterationStageIndex = i;
                        break;
                    }

                string iterationModeStr = iterationInfoSplit[0].Substring(0, iterationStageIndex);
                string iterationStageStr = iterationInfoSplit[0].Substring(iterationStageIndex);

                var iterationMode = ParseIterationMode(iterationModeStr);
                var iterationStage = ParseIterationStage(iterationStageStr);
                int.TryParse(iterationInfoSplit[1], out int iterationIndex);

                string measurementsInfo = lineSplit[1];
                var measurementsInfoSplit = measurementsInfo.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                long op = 1L;
                double ns = double.PositiveInfinity;

                // Dictionary to store energy measurements grouped by socket ID
                var socketMeasurements = new Dictionary<int, Dictionary<string, double>>();

                foreach (string item in measurementsInfoSplit)
                {
                    var measurementSplit = item.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    string value = measurementSplit[0];
                    string unit = measurementSplit[1];

                    switch (unit)
                    {
                        case NsSymbol:
                            ns = double.Parse(value, MainCultureInfo);
                            break;
                        case OpSymbol:
                            op = long.Parse(value, MainCultureInfo);
                            break;
                    }

                    if (!unit.Contains(EnergySymbols.SocketSymbol))
                        continue;

                    var energySplit = unit.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
                    string energyUnit = energySplit[0];
                    int socketId = int.Parse(energySplit[2]);

                    // Initialize socket measurements if not exists
                    if (!socketMeasurements.ContainsKey(socketId))
                    {
                        socketMeasurements[socketId] = new Dictionary<string, double>
                        {
                            { EnergySymbols.PackageJSymbol, double.NaN },
                            { EnergySymbols.DramJSymbol, double.NaN },
                            { EnergySymbols.CoreJSymbol, double.NaN },
                            { EnergySymbols.UncoreJSymbol, double.NaN },
                            { EnergySymbols.PsysJSymbol, double.NaN },
                            { EnergySymbols.CpuCelsiusTemperatureSymbol, double.NaN }
                        };
                    }

                    // Parse and store the energy value
                    double energyValue = double.Parse(value, MainCultureInfo);
                    if (socketMeasurements[socketId].ContainsKey(energyUnit))
                    {
                        socketMeasurements[socketId][energyUnit] = energyValue;
                    }
                }

                // Convert dictionary to list of EnergyMeasurement objects
                var energyMeasurements = new List<EnergyMeasurement>();
                foreach (var kvp in socketMeasurements.OrderBy(x => x.Key))
                {
                    int socketId = kvp.Key;
                    var measurements = kvp.Value;

                    var energyMeasurement = new EnergyMeasurement(
                        socketId,
                        measurements[EnergySymbols.PackageJSymbol],
                        measurements[EnergySymbols.DramJSymbol],
                        measurements[EnergySymbols.CoreJSymbol],
                        measurements[EnergySymbols.UncoreJSymbol],
                        measurements[EnergySymbols.PsysJSymbol],
                        measurements[EnergySymbols.CpuCelsiusTemperatureSymbol]
                    );
                    energyMeasurements.Add(energyMeasurement);
                }

                return new Measurement(processIndex, iterationMode, iterationStage, iterationIndex, op, ns, energyMeasurements);
            }
            catch (Exception)
            {
#if DEBUG // some benchmarks need to write to console and when we display this error it's confusing
                Debug.WriteLine("Parse error in the following line:");
                Debug.WriteLine(line);
#endif
                return Error();
            }
        }
    }
}