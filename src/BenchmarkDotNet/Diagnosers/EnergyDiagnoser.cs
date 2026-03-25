using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Detectors;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Validators;

namespace BenchmarkDotNet.Diagnosers
{
    public class EnergyDiagnoser: IDiagnoser
    {
        private const string DiagnoserId = nameof(EnergyDiagnoser);

        public static readonly EnergyDiagnoser Default = new EnergyDiagnoser(new EnergyDiagnoserConfig(displayEnergyColumn: true));

        public EnergyDiagnoser(EnergyDiagnoserConfig config) => Config = config;

        public EnergyDiagnoserConfig Config { get; }

        public RunMode GetRunMode(BenchmarkCase benchmarkCase) => RunMode.NoOverhead;

        public IEnumerable<string> Ids => new[] { DiagnoserId };
        public IEnumerable<IExporter> Exporters => Array.Empty<IExporter>();
        public IEnumerable<IAnalyser> Analysers => Array.Empty<IAnalyser>();
        public void DisplayResults(ILogger logger) { }

        public IEnumerable<ValidationError> Validate(ValidationParameters validationParameters)
        {
            if (!OsDetector.IsLinux())
            {
                yield return new ValidationError(true, "The EnergyDiagnoser works only on Linux!");
            }
        }

        public void Handle(HostSignal signal, DiagnoserActionParameters parameters)
        {
            System.Console.WriteLine(DiagnoserId + " Signal " + signal.ToString());
        }

        public IEnumerable<Metric> ProcessResults(DiagnoserResults diagnoserResults)
        {
            var samples = diagnoserResults.Measurements
                .Where(m => m.IterationMode == IterationMode.Workload &&
                            m.IterationStage == IterationStage.Result)
                .ToList();

            if (samples.Count == 0)
                yield break;

            var socketCount = samples.First().EnergyMeasurements.Count;

            for (int i = 0; i < socketCount; ++i)
            {
                // Per-iteration uJ/op samples
                var pkgPerOpSeries = samples
                    .Where(m => (m.Operations > 0 && m.EnergyMeasurements.Count > i))
                    .Select(m => m.EnergyMeasurements[i].PackageEnergy / m.Operations)
                    .ToArray();

                var dramPerOpSeries = samples
                    .Where(m => (m.Operations > 0 && m.EnergyMeasurements.Count > i))
                    .Select(m => m.EnergyMeasurements[i].DramEnergy / m.Operations)
                    .ToArray();

                var uncorePerOpSeries = samples
                    .Where(m => (m.Operations > 0 && m.EnergyMeasurements.Count > i))
                    .Select(m => m.EnergyMeasurements[i].UncoreEnergy / m.Operations)
                    .ToArray();

                var corePerOpSeries = samples
                    .Where(m => (m.Operations > 0 && m.EnergyMeasurements.Count > i))
                    .Select(m => m.EnergyMeasurements[i].CoreEnergy / m.Operations)
                    .ToArray();

                var psysPerOpSeries = samples
                    .Where(m => (m.Operations > 0 && m.EnergyMeasurements.Count > i))
                    .Select(m => m.EnergyMeasurements[i].PsysEnergy / m.Operations)
                    .ToArray();

                //var dramPerOpSeries = samples.Where(m => m.Operations > 0).Select(m => m.DramEnergy / m.Operations).ToArray();

                // Mean across iterations
                double pkgPerOp = pkgPerOpSeries.Length > 0 ? pkgPerOpSeries.Average() : double.NaN;
                double dramPerOp = dramPerOpSeries.Length > 0 ? dramPerOpSeries.Average() : double.NaN;
                double uncorePerOp = uncorePerOpSeries.Length > 0 ? uncorePerOpSeries.Average() : double.NaN;
                double corePerOp = corePerOpSeries.Length > 0 ? corePerOpSeries.Average() : double.NaN;
                double psysPerOp = psysPerOpSeries.Length > 0 ? psysPerOpSeries.Average() : double.NaN;

                // Variability (sample SD + CV%)
                double pkgPerOpStdDev = StdDevSample(pkgPerOpSeries, pkgPerOp);
                double pkgPerOpCvPct = (pkgPerOp > 0 && !double.IsNaN(pkgPerOpStdDev))
                    ? (pkgPerOpStdDev / pkgPerOp) * 100.0
                    : double.NaN;

                // uJ/iteration (mean iteration energy)
                double pkgPerIter = samples.Any() ? samples.Average(m => m.EnergyMeasurements[i].PackageEnergy) : double.NaN;
                double dramPerIter = samples.Any() ? samples.Average(m => m.EnergyMeasurements[i].DramEnergy) : double.NaN;
                double uncorePerIter = samples.Any() ? samples.Average(m => m.EnergyMeasurements[i].UncoreEnergy) : double.NaN;
                double corePerIter = samples.Any() ? samples.Average(m => m.EnergyMeasurements[i].CoreEnergy) : double.NaN;
                double psysPerIter = samples.Any() ? samples.Average(m => m.EnergyMeasurements[i].PsysEnergy) : double.NaN;
                double avgTempPerIter = samples.Any() ? samples.Average(m => m.EnergyMeasurements[i].AverageCpuTemperature) : double.NaN;

                yield return new Metric(EnergyMetricDescriptor.DramEnergyPerOp(i), dramPerOp);
                yield return new Metric(EnergyMetricDescriptor.PackageEnergyPerOp(i), pkgPerOp);
                yield return new Metric(EnergyMetricDescriptor.UncoreEnergyPerOp(i), uncorePerOp);
                yield return new Metric(EnergyMetricDescriptor.CoreEnergyPerOp(i), corePerOp);

                yield return new Metric(EnergyMetricDescriptor.DramEnergyPerIteration(i), dramPerIter);
                yield return new Metric(EnergyMetricDescriptor.PackageEnergyPerIteration(i), pkgPerIter);
                yield return new Metric(EnergyMetricDescriptor.UncoreEnergyPerIteration(i), uncorePerIter);
                yield return new Metric(EnergyMetricDescriptor.CoreEnergyPerIteration(i), corePerIter);

                if (!double.IsNaN(psysPerOp) && !double.IsNaN(psysPerIter))
                {
                    yield return new Metric(EnergyMetricDescriptor.PsysEnergyPerOp, psysPerOp);
                    yield return new Metric(EnergyMetricDescriptor.PsysEnergyPerIteration, psysPerIter);
                }

                yield return new Metric(EnergyMetricDescriptor.PackageEnergyPerOpStdDev(i), pkgPerOpStdDev);
                yield return new Metric(EnergyMetricDescriptor.PackageEnergyPerOpCvPct(i), pkgPerOpCvPct);
                yield return new Metric(EnergyMetricDescriptor.AverageTemperaturePerIteration(i), avgTempPerIter);
            }
        }
        private static double StdDevSample(double[] values, double mean)
        {
            int n = values.Length;
            if (n < 2) return double.NaN;

            double sumSq = 0;
            for (int i = 0; i < n; i++)
            {
                double d = values[i] - mean;
                sumSq += d * d;
            }
            return Math.Sqrt(sumSq / (n - 1));
        }

        private class EnergyMetricDescriptor : IMetricDescriptor
        {
            internal static IMetricDescriptor DramEnergyPerOp(int socketId) =>
                new EnergyMetricDescriptor(
                    $"AvgEnergyDramPerOp{socketId}",
                    string.Format(Column.DramEnergyPerOp, socketId),
                    $"Average DRAM energy consumed per operation (uJ) on socket {socketId}.");

            internal static IMetricDescriptor PackageEnergyPerOp(int socketId) =>
                new EnergyMetricDescriptor(
                    $"AvgEnergyPackagePerOp{socketId}",
                    string.Format(Column.PackageEnergyPerOp, socketId),
                    $"Average CPU package energy consumed per operation (uJ) on socket {socketId}.");

            internal static IMetricDescriptor UncoreEnergyPerOp(int socketId) =>
                new EnergyMetricDescriptor(
                    $"AvgEnergyUncorePerOp{socketId}",
                    string.Format(Column.UncoreEnergyPerOp, socketId),
                    $"Average CPU uncore energy consumed per operation (uJ) on socket {socketId}.");

            internal static IMetricDescriptor CoreEnergyPerOp(int socketId) =>
                new EnergyMetricDescriptor(
                    $"AvgEnergyCorePerOp{socketId}",
                    string.Format(Column.CoreEnergyPerOp, socketId),
                    $"Average CPU core energy consumed per operation (uJ) on socket {socketId}.");

            internal static readonly IMetricDescriptor PsysEnergyPerOp =
                new EnergyMetricDescriptor(
                    "AvgEnergyPackagePerOp",
                    Column.PackageEnergyPerOp,
                    "Average CPU psys energy consumed per operation (uJ).");

            internal static IMetricDescriptor PackageEnergyPerOpStdDev(int socketId) =>
                new EnergyMetricDescriptor(
                    "PkgEPerOpStdDev",
                    string.Format(Column.PackageEnergyPerOpStdDev, socketId),
                    $"Standard deviation of package energy per operation (uJ/op) on socket {socketId}.",
                    unit: "uJ");

            internal static IMetricDescriptor PackageEnergyPerOpCvPct(int socketId) =>
                new EnergyMetricDescriptor(
                    "PkgEPerOpCvPct",
                    string.Format(Column.PackageEnergyPerOpCvPct, socketId),
                    $"Coefficient of variation of package energy per operation (%) on socket {socketId}.",
                    numberFormat: "#0.00", // no "uj"
                    unit: "");

            internal static IMetricDescriptor DramEnergyPerIteration(int socketId) =>
                new EnergyMetricDescriptor(
                    $"AvgEnergyDramPerIter{socketId}",
                    string.Format(Column.DramEnergyPerIter, socketId),
                    $"Average DRAM energy consumed per benchmark iteration (uJ) on socket {socketId}.");

            internal static IMetricDescriptor PackageEnergyPerIteration(int socketId) =>
                new EnergyMetricDescriptor(
                    $"AvgEnergyPackagePerIter{socketId}",
                    string.Format(Column.PackageEnergyIter, socketId),
                    $"Average CPU package energy consumed per benchmark iteration (uJ) on socket {socketId}.");

            internal static IMetricDescriptor UncoreEnergyPerIteration(int socketId) =>
                new EnergyMetricDescriptor(
                    $"AvgEnergyUncorePerIter{socketId}",
                    string.Format(Column.UncoreEnergyPerIter, socketId),
                    $"Average CPU uncore energy consumed per benchmark iteration (uJ) on socket {socketId}.");

            internal static IMetricDescriptor CoreEnergyPerIteration(int socketId) =>
                new EnergyMetricDescriptor(
                    $"AvgEnergyCorePerIter{socketId}",
                    string.Format(Column.CoreEnergyPerIter, socketId),
                    $"Average CPU core energy consumed per benchmark iteration (uJ) on socket {socketId}.");

            internal static readonly IMetricDescriptor PsysEnergyPerIteration =
                new EnergyMetricDescriptor(
                    "AvgEnergyPsysPerIter",
                    Column.PsysEnergyPerIter,
                    "Average psys energy consumed per benchmark iteration (uJ).");

            internal static IMetricDescriptor AverageTemperaturePerIteration(int socketId) =>
                new EnergyMetricDescriptor(
                    $"AvgTemperaturePerIter{socketId}",
                    string.Format(Column.AverageTemperaturePerIter, socketId),
                    $"Average CPU temperature per benchmark iteration (Celsius degrees) on socket {socketId}.",
                    numberFormat: "#0.00",
                    unit: "degC");

            private EnergyMetricDescriptor(string id, string columnName, string legend,
                string numberFormat = "#0.00 uj", string unit = "uJ")
            {
                Id = id;
                DisplayName = columnName;
                Legend = legend;
                NumberFormat = numberFormat;
                Unit = unit;
            }

            public string Id { get; }
            public string DisplayName { get; }
            public string Legend { get; }
            public string NumberFormat { get; }
            public UnitType UnitType => UnitType.Dimensionless;
            public string Unit { get; }
            public bool TheGreaterTheBetter => false;
            public int PriorityInCategory { get; }
            public bool GetIsAvailable(Metric metric)
                => !double.IsNaN(metric.Value) && metric.Value != 0.0;
        }
    }
}